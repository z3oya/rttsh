using System.Runtime.CompilerServices;
using Toolbox.Core.Rtt;

namespace Toolbox.Tools.RttCli;

/// <summary>State and patterns shared by the streaming commands (monitor, send, script): the one
/// console lock every writer takes, diagnostic routing, transport event wiring, and the uniform
/// open-and-report / exclusive-target-lock shapes.</summary>
internal static class SessionSupport
{
    /// <summary>Every console write (data, diagnostics, TUI log) takes this lock so poll-thread
    /// output never interleaves with input echo or other diagnostics.</summary>
    public static readonly object ConsoleLock = new();

    public static void WriteDiag(string line)
    {
        lock (ConsoleLock)
        {
            Console.Error.WriteLine(line);
        }
    }

    /// <summary>One session-diagnostic line: into the TUI log surface when active, stderr otherwise.
    /// The split must never be bypassed - raw stderr writes in TUI mode land on the pinned input row.</summary>
    public static void WriteSessionLine(TerminalUi? ui, string line)
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

    /// <summary>Shared wiring for both renderer commands: data into the renderer, DLL chatter
    /// into the log region (or stderr in plain mode) when verbose, and every failure path
    /// converging on `done` (never Environment.Exit - see Program's class doc). Ctrl+Break also
    /// lands on `done`. StrongBox because the input thread writes the exit code too.</summary>
    public static void WireEvents(JLinkRttTransport transport, RttRenderer renderer, ManualResetEventSlim done, StrongBox<int> exitCode, TerminalUi? ui, bool verbose)
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

    /// <summary>Opens the transport; a connect failure prints "rtt-cli: &lt;message&gt;" to stderr
    /// and returns false (the caller exits 1). Covers transport.Open only - each session wraps
    /// its own Write failures.</summary>
    public static bool OpenOrReport(JLinkRttTransport transport, RttConnectionConfig config)
    {
        try
        {
            transport.Open(config);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"rtt-cli: {ex.Message}");
            return false;
        }
    }

    /// <summary>Acquires the single-instance-per-target lock. Null means the target is already
    /// held - TryAcquire has written the warning, the caller just exits 1.</summary>
    public static TargetLock? AcquireExclusive(RttConnectionConfig config) =>
        TargetLock.TryAcquire(config, Console.Error);
}
