using System.Runtime.CompilerServices;
using System.Text;
using Toolbox.Core.Rtt;
using Toolbox.Core.SerialComm;

namespace Toolbox.Tools.RttCli;

/// <summary>Entry point: parse, dispatch (monitor / list-devices / send), exit codes.
/// Data goes to stdout, every diagnostic to stderr, so stdout stays pipeable.
///
/// Ctrl+C handling: Console.CancelKeyPress is NOT the exit path. Dispatch of that event was
/// observed to never run while a thread is blocked in console input (reproduced minimal on
/// .NET 10 / Win11), and a subscribed handler suppresses the default termination - a dead
/// process. Instead TreatControlCAsInput turns Ctrl+C into a plain input char that the
/// editor loop reads deterministically; CancelKeyPress stays subscribed only as a
/// Ctrl+Break fallback. The exit path never calls Environment.Exit (also deadlock-prone
/// here): threads only signal `done`, the main thread wakes and returns normally.</summary>
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
        UsageErrorCommand c => UsageError(c.Message),
        ListDevicesCommand c => ListDevices(c),
        MonitorCommand c => Monitor(c.Options),
        SendCommand c => Send(c),
        _ => UsageError("internal: unhandled command"),
    };

    // ---- monitor: interactive terminal ---------------------------------------------

    private static int Monitor(CommandLineOptions options)
    {
        RttConnectionConfig config = options.ToConnectionConfig(resetDefault: false);
        using var transport = new JLinkRttTransport();
        using var renderer = new RttRenderer(options, Console.Out, ConsoleLock);
        using var done = new ManualResetEventSlim(false);
        var exitCode = new StrongBox<int>();

        WireEvents(transport, renderer, done, exitCode);

        try
        {
            transport.Open(config);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"rtt-cli: {ex.Message}");
            return 1;
        }

        if (!Console.IsOutputRedirected)
        {
            Console.Error.WriteLine($"RTT terminal on {config.Chip} (channel 0). Type a line + Enter to send; Ctrl+C to exit.");
        }

        Encoding encoding = TextCodec.Resolve(options.EffectiveEncoding);
        byte[] eol = encoding.GetBytes(EolText(options.Eol ?? TextEol.Lf));

        // Line input runs on a background thread so the main thread stays wakeable.
        bool interactive = !Console.IsInputRedirected;
        if (interactive) Console.TreatControlCAsInput = true;
        var inputThread = new Thread(() =>
        {
            while (ReadLine(interactive) is { } line)
            {
                if (line.Length == 0) continue;
                byte[] payload = [.. encoding.GetBytes(line), .. eol];
                try
                {
                    transport.Write(payload);
                }
                catch (IOException ex)
                {
                    WriteDiag($"rtt-cli: {ex.Message}");
                    exitCode.Value = 1;
                    break;
                }
            }
            done.Set();   // Ctrl+C in the editor, stdin EOF, or a failed write
        })
        {
            IsBackground = true,
            Name = "rtt-input",
        };
        inputThread.Start();

        done.Wait();
        transport.Close();
        return exitCode.Value;   // usings dispose renderer (flushes the pending hex line) then transport
    }

    /// <summary>One input line. Returns null on Ctrl+C (interactive) or EOF (redirected).</summary>
    private static string? ReadLine(bool interactive) =>
        interactive ? (ConsoleInput.TryReadLine(out string? line) ? line : null)
                    : Console.ReadLine();

    // ---- send: one-shot payload ------------------------------------------------------

    private static int Send(SendCommand command)
    {
        CommandLineOptions options = command.Options;
        RttConnectionConfig config = options.ToConnectionConfig(resetDefault: false);
        byte[] payload = EncodePayload(command.Payload, options);

        using var transport = new JLinkRttTransport();
        using var renderer = new RttRenderer(options, Console.Out, ConsoleLock);
        using var done = new ManualResetEventSlim(false);
        var exitCode = new StrongBox<int>();

        WireEvents(transport, renderer, done, exitCode);

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

    /// <summary>Shared wiring for both streaming commands: data into the renderer, DLL chatter
    /// onto stderr, and every failure path converging on `done` (never Environment.Exit - see
    /// the class doc). Ctrl+Break (and Ctrl+C in environments where the event still dispatches)
    /// also lands on `done`. StrongBox because the input thread writes the exit code too.</summary>
    private static void WireEvents(JLinkRttTransport transport, RttRenderer renderer, ManualResetEventSlim done, StrongBox<int> exitCode)
    {
        transport.LogLine += WriteDiag;
        transport.DataReceived += renderer.OnData;
        // Raised on the poll thread after the transport closed itself; wake the main thread.
        transport.Error += ex =>
        {
            WriteDiag($"rtt-cli: {ex.Message}");
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
        return TextCodec.Resolve(options.EffectiveEncoding).GetBytes(payload);
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

    private static int PrintHelp()
    {
        Console.WriteLine("""
            rtt-cli - SEGGER J-Link RTT terminal (channel 0)

            Usage:
              rtt-cli [options]                         interactive terminal (default command)
              rtt-cli list-devices [--filter <text>]    list the J-Link DLL device database
              rtt-cli send <text> [--hex] [options]     send once, optionally wait for a reply

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
              --eol lf|cr|crlf|none   line ending appended to sent lines (default lf)
              --encoding utf8|ascii|latin1
                                     decode received bytes (default utf8)
              --hex                 show payload as a 16-byte-per-line hex dump (send: parse payload as hex)
              --log <file>          also append raw received bytes to a file

            Other:
              --wait <ms>         (send) print received bytes for this long before exiting
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
