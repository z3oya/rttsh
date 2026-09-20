using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Toolbox.Core.Rtt;
using Toolbox.Core.SerialComm;
using Toolbox.Tools.RttCli.Scripting;

namespace Toolbox.Tools.RttCli;

/// <summary>Entry point: parse, dispatch (monitor / list-devices / send), exit codes.
/// Data goes to stdout, every diagnostic to stderr, so stdout stays pipeable.
///
/// Ctrl+C handling (monitor): Console.CancelKeyPress is NOT the exit path. Dispatch of that
/// event was observed to never run while a thread is blocked in console input (reproduced
/// minimal on .NET 10 / Win11), and a subscribed handler suppresses the default termination -
/// a dead process. Instead TreatControlCAsInput turns Ctrl+C into a plain input char that the
/// editor loop reads deterministically; CancelKeyPress stays subscribed only as a Ctrl+Break
/// fallback. The exit path never calls Environment.Exit (also deadlock-prone here): threads
/// only signal `done`, the main thread wakes and returns normally. Script mode has no
/// console-input reader, so CancelKeyPress is its primary, reliable cancel path - see RunScript.</summary>
internal static class Program
{
    private static readonly object ConsoleLock = new();

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; }   // CP936 consoles would mangle UTF-8 logs
        catch { /* redirected or exotic hosts may refuse; the default is still usable */ }

        try
        {
            return Run(CommandLine.Parse(args));
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"rtt-cli: {ex.Message}");
            Console.Error.WriteLine("Run 'rtt-cli --help' for usage.");
            return 2;
        }
    }

    private static int Run(RttCommand command) => command switch
    {
        HelpCommand => PrintHelp(),
        VersionCommand => PrintVersion(),
        UsageErrorCommand c => UsageError(c.Message),
        ListDevicesCommand c => ListDevices(c),
        MonitorCommand c => Monitor(c.Options),
        SendCommand c => Send(c),
        ScriptCommand c => RunScript(c),
        _ => UsageError("internal: unhandled command"),
    };

    // ---- monitor: interactive terminal ---------------------------------------------

    private static int Monitor(CommandLineOptions options)
    {
        RttConnectionConfig config = options.ToConnectionConfig(resetDefault: false);
        using var transport = new JLinkRttTransport();
        using var done = new ManualResetEventSlim(false);
        var exitCode = new StrongBox<int>();

        bool interactive = !Console.IsInputRedirected;
        TerminalUi? ui = interactive && options.Tui ? TerminalUi.TryCreate(ConsoleLock) : null;
        ui?.Layout();

        // PowerShell can write and close a short stdin pipe before J-Link/OpenEx finishes.
        // Consume the redirected script before opening the native DLL; otherwise Console.In
        // can race the DLL initialization and report EOF even though PowerShell wrote lines.
        List<string>? redirectedLines = null;
        if (!interactive)
        {
            redirectedLines = [];
            try
            {
                while (Console.ReadLine() is { } line)
                    redirectedLines.Add(line);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"rtt-cli: cannot read redirected input: {ex.Message}");
                return 1;
            }
        }

        try
        {
            using var renderer = new RttRenderer(options, Console.Out, ConsoleLock, ui);
            WireEvents(transport, renderer, done, exitCode, ui, options.Verbose);

            try
            {
                transport.Open(config);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                Console.Error.WriteLine($"rtt-cli: {ex.Message}");
                return 1;
            }

            string banner = $"RTT terminal on {config.Chip} (channel 0). Type a line + Enter to send; Ctrl+C to exit.";
            if (!Console.IsOutputRedirected)   // TUI implies not redirected; this only trims the stderr branch
            {
                WriteSessionLine(ui, banner);
            }

            Encoding encoding = TextCodec.Resolve(options.EffectiveEncoding);
            byte[] eol = encoding.GetBytes(EolText(options.Eol ?? TextEol.Lf));

            void SendInputLine(string line)
            {
                if (line.Length == 0) return;
                byte[] payload = [.. encoding.GetBytes(line), .. eol];
                try
                {
                    transport.Write(payload);
                }
                catch (IOException ex)
                {
                    WriteSessionLine(ui, $"rtt-cli: {ex.Message}");
                    exitCode.Value = 1;
                    throw;
                }
            }

            // Line input runs on a background thread so the main thread stays wakeable.
            if (interactive) Console.TreatControlCAsInput = true;
            var history = new InputHistory();   // session-only; owned by the input thread
            var inputThread = new Thread(() =>
            {
                try
                {
                    if (redirectedLines is not null)
                    {
                        foreach (var line in redirectedLines)
                            SendInputLine(line);
                    }
                    else
                    {
                        while (ReadLine(interactive, ui, history) is { } line)
                        {
                            history.Record(line);
                            SendInputLine(line);
                        }
                    }

                    // A piped script reaches EOF as soon as PowerShell writes it. Give the
                    // target a short window to echo/reply before tearing down the J-Link link;
                    // otherwise the reply is still in flight when RTT stops.
                    if (redirectedLines is not null && exitCode.Value == 0)
                    {
                        int waitMs = options.WaitMs ?? 500;
                        if (waitMs > 0)
                            done.Wait(waitMs);
                    }
                }
                catch (IOException)
                {
                    // SendInputLine has already reported the failed write.
                }
                done.Set();   // Ctrl+C in the editor, stdin EOF, or a failed write
            })
            {
                IsBackground = true,
                Name = "rtt-input",
            };
            inputThread.Start();

            done.Wait();
            transport.Close();   // joins the poll thread before the UI is torn down below
            return exitCode.Value;
        }
        finally
        {
            // TreatControlCAsInput flips the SHARED console input mode; leaving it set would
            // break Ctrl+C in the parent shell after we exit. The VT scroll region is the
            // same story - reset both on every exit path.
            if (interactive) Console.TreatControlCAsInput = false;
            ui?.Dispose();
        }
    }

    /// <summary>One input line. Returns null on Ctrl+C (interactive) or EOF (redirected).</summary>
    private static string? ReadLine(bool interactive, TerminalUi? ui, InputHistory history) =>
        interactive ? (ConsoleInput.TryReadLine(ui, history, out string? line) ? line : null)
                    : Console.ReadLine();

    // ---- send: one-shot payload ------------------------------------------------------

    private static int Send(SendCommand command)
    {
        CommandLineOptions options = command.Options;
        RttConnectionConfig config = options.ToConnectionConfig(resetDefault: false);
        byte[] payload = EncodePayload(command.Payload, options);
        // Line targets need a terminator to execute; --hex sends raw bytes untouched.
        if (!options.Hex)
        {
            Encoding encoding = TextCodec.Resolve(options.EffectiveEncoding);
            payload = [.. payload, .. encoding.GetBytes(EolText(options.Eol ?? TextEol.Lf))];
        }

        using var transport = new JLinkRttTransport();
        using var renderer = new RttRenderer(options, Console.Out, ConsoleLock);
        using var done = new ManualResetEventSlim(false);
        var exitCode = new StrongBox<int>();

        WireEvents(transport, renderer, done, exitCode, ui: null, options.Verbose);

        try
        {
            transport.Open(config);
            transport.Write(payload);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"rtt-cli: {ex.Message}");
            return 1;
        }

        // Waiting is interruptible by link failure or Ctrl+C instead of a blind sleep.
        int waitMs = options.WaitMs ?? 0;
        if (waitMs > 0) done.Wait(waitMs);
        transport.Close();
        return exitCode.Value;
    }

    // ---- script: Lua automation -------------------------------------------------------

    /// <summary>Runs a Lua script against a live RTT link. The script thread owns the Lua state;
    /// transport events reach it only through ScriptRuntime (which subscribed itself). Double
    /// Ctrl+C: the first press asks the script to stop at the next rtt.* boundary, the second is
    /// left to default termination (a script stuck in pure Lua cannot be interrupted safely).</summary>
    private static int RunScript(ScriptCommand command)
    {
        CommandLineOptions options = command.Options;
        if (command.EvalSource is null && !File.Exists(command.ScriptPath))
            throw new UsageException($"script: file not found: {command.ScriptPath}");
        if (options.ScriptTimeoutMs is < 0)
            throw new UsageException("script: --script-timeout must be >= 0 ms (0 = off)");

        RttConnectionConfig config = options.ToConnectionConfig(resetDefault: false);
        Encoding encoding = TextCodec.Resolve(options.EffectiveEncoding);
        byte[] eol = encoding.GetBytes(EolText(options.Eol ?? TextEol.Lf));

        using var transport = new JLinkRttTransport();
        var runtime = new ScriptRuntime(transport, encoding, eol, WriteDiag, options.ScriptTimeoutMs ?? 0);

        try
        {
            transport.Open(config);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"rtt-cli: {ex.Message}");
            return 1;
        }

        bool cancelRequested = false;
        Console.CancelKeyPress += (_, e) =>
        {
            if (cancelRequested) return;   // second press: e.Cancel stays false, default kills us
            cancelRequested = true;
            e.Cancel = true;
            runtime.RequestCancel();
        };

        var exitCode = new StrongBox<int>(1);
        using var done = new ManualResetEventSlim(false);
        var scriptThread = new Thread(() =>
        {
            try
            {
                var host = command.EvalSource is not null
                    ? new LuaScriptHost(runtime, command.EvalSource, "=eval")
                    : new LuaScriptHost(runtime, File.ReadAllText(command.ScriptPath!), "@" + command.ScriptPath);
                exitCode.Value = host.Run();
            }
            catch (ScriptError ex)
            {
                WriteDiag($"rtt-cli: {ex.Message}");
                exitCode.Value = 1;
            }
            catch (Exception ex)
            {
                // binding-internal or unexpected failure must not crash the process uncleanly
                WriteDiag($"rtt-cli: script crashed: {ex.Message}");
                exitCode.Value = 1;
            }
            finally
            {
                done.Set();
            }
        })
        { IsBackground = true, Name = "rtt-script" };
        scriptThread.Start();

        done.Wait();
        transport.Close();   // joins the poll thread before we report
        scriptThread.Join();
        return exitCode.Value;
    }

    /// <summary>Shared wiring for both streaming commands: data into the renderer, DLL chatter
    /// into the log region (or stderr in plain mode) when verbose, and every failure path
    /// converging on `done` (never Environment.Exit - see the class doc). Ctrl+Break also lands
    /// on `done`. StrongBox because the input thread writes the exit code too.</summary>
    private static void WireEvents(JLinkRttTransport transport, RttRenderer renderer, ManualResetEventSlim done, StrongBox<int> exitCode, TerminalUi? ui, bool verbose)
    {
        // Without --verbose the DLL's connection chatter stays unsubscribed; Error events
        // (runtime failures) are always delivered regardless.
        if (verbose)
            transport.LogLine += line => WriteSessionLine(ui, line);
        transport.DataReceived += renderer.OnData;
        // Raised on the poll thread after the transport closed itself; wake the main thread.
        // Same surface as LogLine: in TUI mode stderr would land on the input row, where the
        // next keystroke's redraw would erase it.
        transport.Error += ex =>
        {
            WriteSessionLine(ui, $"rtt-cli: {ex.Message}");
            exitCode.Value = 1;
            done.Set();
        };
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;   // we drive a clean shutdown ourselves (Ctrl+Break path)
            done.Set();
        };
    }

    private static byte[] EncodePayload(string payload, CommandLineOptions options)
    {
        if (options.Hex)
        {
            if (!HexCodec.TryParse(payload, out byte[] bytes, out string? error))
                throw new UsageException($"--hex: {error}");
            return bytes;
        }
        return TextCodec.Resolve(options.EffectiveEncoding).GetBytes(UnescapeSendText(payload));
    }

    /// <summary>Interprets \n \r \t \\ in send text: shell quoting for a literal newline varies
    /// (PowerShell needs `n), and C-style escapes are what users naturally type. Unknown
    /// escapes stay as-is.</summary>
    private static string UnescapeSendText(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\' || i + 1 >= text.Length)
            {
                sb.Append(c);
                continue;
            }
            char next = text[++i];
            switch (next)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '\\': sb.Append('\\'); break;
                default: sb.Append(c).Append(next); break;
            }
        }
        return sb.ToString();
    }

    // ---- list-devices: dump the DLL device database ----------------------------------

    private static int ListDevices(ListDevicesCommand command)
    {
        using var library = new JLinkLibrary();
        if (!library.Load(command.DllPath, out string error))
        {
            Console.Error.WriteLine($"rtt-cli: {error}");
            return 1;
        }
        if (!library.TryEnumerateDevices(out List<JLinkDeviceRecord> records, out error))
        {
            Console.Error.WriteLine($"rtt-cli: {error}");
            return 1;
        }

        if (command.Filter is { Length: > 0 } filter)
        {
            records = records.Where(r => ContainsIgnoreCase(r.Name, filter)
                || ContainsIgnoreCase(r.Manufacturer, filter)
                || ContainsIgnoreCase(r.Core, filter)).ToList();
        }
        PrintDeviceTable(records);
        Console.Error.WriteLine($"{records.Count} device(s).");
        return 0;
    }

    private static void PrintDeviceTable(List<JLinkDeviceRecord> records)
    {
        int nameWidth = Math.Clamp(records.Max(r => r.Name.Length), 8, 40);
        const int manuWidth = 14;
        const int coreWidth = 12;
        Console.WriteLine($"{"NAME".PadRight(nameWidth)} {"MANUFACTURER".PadRight(manuWidth)} {"CORE".PadRight(coreWidth)} {"FLASH",8} {"RAM",8}");
        foreach (var record in records)
        {
            Console.WriteLine($"{Truncate(record.Name, nameWidth).PadRight(nameWidth)} " +
                $"{Truncate(record.Manufacturer, manuWidth).PadRight(manuWidth)} " +
                $"{Truncate(record.Core, coreWidth).PadRight(coreWidth)} " +
                $"{SizeText(record.FlashBytes),8} {SizeText(record.RamBytes),8}");
        }
    }

    // ---- shared helpers ----------------------------------------------------------------

    private static void WriteDiag(string line)
    {
        lock (ConsoleLock)
        {
            Console.Error.WriteLine(line);
        }
    }

    /// <summary>One session-diagnostic line: into the TUI log surface when active, stderr otherwise.
    /// The split must never be bypassed - raw stderr writes in TUI mode land on the pinned input row.</summary>
    private static void WriteSessionLine(TerminalUi? ui, string line)
    {
        if (ui is not null)
        {
            lock (ConsoleLock) ui.WriteLog(line + "\r\n");
        }
        else
        {
            WriteDiag(line);
        }
    }

    private static int PrintVersion()
    {
        string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
        Console.WriteLine($"rtt-cli {version}");
        return 0;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
            rtt-cli - SEGGER J-Link RTT terminal (channel 0)

            Usage:
              rtt-cli [options]                         interactive terminal (default command)
              rtt-cli list-devices [--filter <text>]    list the J-Link DLL device database
              rtt-cli send <text> [--hex] [options]     send once, optionally wait for a reply
                                        (text: \n \r \t \\ escapes are interpreted)
              rtt-cli script <file.lua> [options]
              rtt-cli script --eval '<lua code>' [options]
                                        run a Lua automation script (rtt.* API)

            Connection options:
              --chip <name>       target device, e.g. STM32H743XI (required for monitor/send)
              --speed <kHz>       interface speed, default 4000
              --if swd|jtag       target interface, default swd
              --reset             reset the target on connect (off by default: J-Link reset
                                  halts the core briefly; it is resumed automatically)
              --no-reset          connect without resetting (default for every command)
              --rtt-addr <hex>    known control-block address (default: SDK auto-scan)
              --rtt-range <hex>   byte range searched for the "SEGGER RTT" signature
              --sn <number>       probe USB serial number (default: first probe)
              --dll <path>        JLink DLL path (default: auto-detect, incl. SEGGER roots)

            Display options (monitor/send):
              --eol lf|cr|crlf|none   line ending appended to text sent by monitor input and
                                    send (default lf; --hex send payloads are raw)
              --encoding utf8|ascii|latin1
                                     decode received bytes (default utf8)
              --hex                 show payload as a 16-byte-per-line hex dump (send: parse payload as hex)
              --log <file>          also append raw received bytes to a file
              -tui, --tui           (monitor only) chat-style layout: log on top, "> " input
                                    pinned to the bottom (off by default; needs a VT terminal);
                                    Up/Down recall previously sent lines

            Script options:
              --script-timeout <ms>  hard limit for the whole script (0/absent = off; enforced
                                  at rtt.* call boundaries). API: send/send_hex/log/wait/
                                  wait_hex/expect/now/sleep/exit. Text APIs are ASCII-reliable;
                                  use wait_hex/send_hex for binary. First Ctrl+C asks the
                                  script to stop, second Ctrl+C hard-exits.

            Other:
              --wait <ms>         (send / redirected monitor) print received bytes for this
                                  long before exiting; redirected monitor defaults to 500 ms
              --verbose           show J-Link connection progress logs (default: quiet;
                                  runtime errors are always shown)
              --version, -v       show rtt-cli's own version
              --help              this help

            Exit codes: 0 ok, 1 runtime failure, 2 usage error.
            """);
        return 0;
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"rtt-cli: {message}");
        Console.Error.WriteLine("Run 'rtt-cli --help' for usage.");
        return 2;
    }

    private static string EolText(TextEol eol) => eol switch
    {
        TextEol.Lf => "\n",
        TextEol.Cr => "\r",
        TextEol.CrLf => "\r\n",
        TextEol.None => "",
        _ => "\n",
    };

    private static string SizeText(uint bytes) => bytes switch
    {
        0 => "-",
        < 1024 => bytes.ToString(),
        < 1024 * 1024 => $"{bytes / 1024}K",
        _ => $"{bytes / (1024 * 1024)}M",
    };

    private static string Truncate(string text, int width) =>
        text.Length <= width ? text : text[..(width - 1)] + "…";

    private static bool ContainsIgnoreCase(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
