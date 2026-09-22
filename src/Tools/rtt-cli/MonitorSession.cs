using System.Runtime.CompilerServices;
using System.Text;
using Toolbox.Core.Rtt;
using Toolbox.Core.SerialComm;

namespace Toolbox.Tools.RttCli;

/// <summary>The monitor command: an interactive terminal over an RTT up/down channel pair, with
/// optional TUI layout and redirected-input (piped script) support.
///
/// Ctrl+C handling: Console.CancelKeyPress is NOT the exit path here. Dispatch of that event was
/// observed to never run while a thread is blocked in console input (reproduced minimal on
/// .NET 10 / Win11), and a subscribed handler suppresses the default termination - a dead
/// process. Instead TreatControlCAsInput turns Ctrl+C into a plain input char that the editor
/// loop reads deterministically; CancelKeyPress stays subscribed only as a Ctrl+Break fallback
/// (SessionSupport.WireEvents). The exit path never calls Environment.Exit (also deadlock-prone
/// here): threads only signal `done`, the main thread wakes and returns normally.</summary>
internal static class MonitorSession
{
    public static int Run(CommandLineOptions options)
    {
        RttConnectionConfig config = options.ToConnectionConfig(resetDefault: false);
        using TargetLock? guard = SessionSupport.AcquireExclusive(config);
        if (guard is null) return 1;
        using var transport = new JLinkRttTransport();
        using var done = new ManualResetEventSlim(false);
        var exitCode = new StrongBox<int>();

        bool interactive = !Console.IsInputRedirected;
        TerminalUi? ui = interactive && options.Tui ? TerminalUi.TryCreate(SessionSupport.ConsoleLock) : null;
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
            using var renderer = new RttRenderer(options.EffectiveEncoding, options.Hex, options.LogFile, Console.Out, SessionSupport.ConsoleLock, ui);
            SessionSupport.WireEvents(transport, renderer, done, exitCode, ui, options.Verbose);

            if (!SessionSupport.OpenOrReport(transport, config)) return 1;

            string banner = $"RTT terminal on {config.Chip} (channel {config.Channel}). Type a line + Enter to send; Ctrl+C to exit.";
            if (!Console.IsOutputRedirected)   // TUI implies not redirected; this only trims the stderr branch
            {
                SessionSupport.WriteSessionLine(ui, banner);
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
                    SessionSupport.WriteSessionLine(ui, $"rtt-cli: {ex.Message}");
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
}
