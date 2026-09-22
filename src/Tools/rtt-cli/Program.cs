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
        HelpCommand c => PrintHelp(c),
        ManualCommand => PrintManual(),
        VersionCommand => PrintVersion(),
        UsageErrorCommand c => UsageError(c.Message),
        ListDevicesCommand c => ListDevices(c),
        MonitorCommand c => Monitor(c.Options),
        SendCommand c => Send(c),
        ScriptCommand c => RunScript(c),
        _ => UsageError("internal: unhandled command"),
    };

    private static int PrintManual()
    {
        Console.WriteLine(LuaManual.Text);
        return 0;
    }

    // ---- monitor: interactive terminal ---------------------------------------------

    private static int Monitor(CommandLineOptions options)
    {
        RttConnectionConfig config = options.ToConnectionConfig(resetDefault: false);
        using TargetLock? guard = TargetLock.TryAcquire(config, Console.Error);
        if (guard is null) return 1;   // TryAcquire already wrote the "already held" warning
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
            using var renderer = new RttRenderer(options.EffectiveEncoding, options.Hex, options.LogFile, Console.Out, ConsoleLock, ui);
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

            string banner = $"RTT terminal on {config.Chip} (channel {config.Channel}). Type a line + Enter to send; Ctrl+C to exit.";
            if (!Console.IsOutputRedirected)   // TUI implies not redirected; this only trims the stderr branch
            {
                WriteSessionLine(ui, banner);
            }

            Encoding encoding = TextCodec.Resolve(options.EffectiveEncoding);
            byte[] eol = PayloadCodec.Terminator(options.Eol, options.EffectiveEncoding);

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
        using TargetLock? guard = TargetLock.TryAcquire(config, Console.Error);
        if (guard is null) return 1;   // TryAcquire already wrote the "already held" warning
        byte[] payload = PayloadCodec.Encode(command.Payload, options.EffectiveEncoding, options.Hex);
        // Line targets need a terminator to execute; --hex sends raw bytes untouched.
        if (!options.Hex)
            payload = [.. payload, .. PayloadCodec.Terminator(options.Eol, options.EffectiveEncoding)];

        using var transport = new JLinkRttTransport();
        using var renderer = new RttRenderer(options.EffectiveEncoding, options.Hex, options.LogFile, Console.Out, ConsoleLock);
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

        // --log is a root option, so script accepts it too. Opened here (with the other option
        // checks) so a bad path fails before the probe is touched, and wired to DataReceived
        // because script has no RttRenderer to do the capture.
        using var logStream = RttLogFile.Open(options.LogFile);

        RttConnectionConfig config = options.ToConnectionConfig(resetDefault: false);
        using TargetLock? guard = TargetLock.TryAcquire(config, Console.Error);
        if (guard is null) return 1;   // TryAcquire already wrote the "already held" warning
        Encoding encoding = TextCodec.Resolve(options.EffectiveEncoding);
        byte[] eol = PayloadCodec.Terminator(options.Eol, options.EffectiveEncoding);

        using var transport = new JLinkRttTransport();
        if (logStream is not null)
            transport.DataReceived += data => RttLogFile.Append(logStream, data);
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
        DeviceTable.Format(records, Console.Out);
        Console.Error.WriteLine($"{records.Count} device(s).");
        return 0;
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

    /// <summary>Help text is rendered by the parser from the CliSpec declarations
    /// (library-generated; the exit-codes footer is part of it).</summary>
    private static int PrintHelp(HelpCommand command)
    {
        Console.Write(command.Text);
        return 0;
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"rtt-cli: {message}");
        Console.Error.WriteLine("Run 'rtt-cli --help' for usage.");
        return 2;
    }

    private static bool ContainsIgnoreCase(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
