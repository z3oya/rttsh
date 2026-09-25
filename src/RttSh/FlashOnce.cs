using RttSh.Core.Rtt;

namespace Toolbox.Tools.RttCli;

/// <summary>The flash commands: download one image (DLL-driven erase+program+verify) or erase the
/// whole chip, then exit. No RTT link is started - the JLinkConnection is deliberately RTT-free
/// until a transport opens one - but the same per-target TargetLock applies, so a flash run never
/// races a live rttsh session on the same probe+chip+channel.
///
/// Flow mirrors SendOnce: validate what a bad option would break BEFORE touching the probe
/// (format/address pairing, erase confirmation), acquire the lock, open, work, close. Failure
/// paths: usage errors exit 2, runtime failures exit 1, both with an actionable message.</summary>
internal static class FlashOnce
{
    public static int Run(FlashDownloadCommand command)
    {
        // Sniff + address pairing first: a wrong file or a missing --addr must not cost a
        // probe connection (and these throw UsageException, which exits 2).
        FlashImageFormat format = FlashImage.Sniff(command.FilePath);
        uint address = FlashImage.ValidateAddress(format, command.Options.Addr, command.FilePath);

        RttConnectionConfig config = command.Options.ToConnectionConfig(resetDefault: false);
        using TargetLock? guard = SessionSupport.AcquireExclusive(config);
        if (guard is null) return 1;

        using var connection = new JLinkConnection();
        WireDiagnostics(connection, command.Options.Verbose);
        if (!OpenOrReport(connection, config)) return 1;

        try
        {
            TryHalt(connection, verbose: command.Options.Verbose);
            SetProgressReporting(connection, "download");

            int flashed = connection.DownloadFile(Path.GetFullPath(command.FilePath), address);
            connection.SetFlashProgressCallback(null);
            if (flashed < 0)
            {
                Console.Error.WriteLine($"rttsh: flash download failed (code={flashed}: {JLinkErrors.Describe(flashed)}).");
                return 1;
            }

            Console.WriteLine($"rttsh: flashed '{command.FilePath}' ({format:G}) - done.");
            if (command.Options.ResetOnConnect == true)
                connection.ResetAndResume();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Runtime failures report and exit 1 instead of crashing with a raw stack: the
            // flash paths throw IOException from every step (reset-halt gate, the optional
            // exports, the DLL's own codes via the facade).
            Console.Error.WriteLine($"rttsh: {ex.Message}");
            return 1;
        }
    }

    public static int RunErase(FlashEraseCommand command)
    {
        // Confirmation before anything touches hardware; the redirected case is a usage error
        // (exit 2) because an unattended --yes-less erase is a script bug, not a runtime fault.
        if (!EnsureEraseAllowed(interactive: !Console.IsInputRedirected, yes: command.Options.Yes,
                input: Console.In, error: Console.Error))
        {
            Console.Error.WriteLine("rttsh: flash erase: not confirmed; nothing was erased.");
            return 1;
        }

        RttConnectionConfig config = command.Options.ToConnectionConfig(resetDefault: false);
        using TargetLock? guard = SessionSupport.AcquireExclusive(config);
        if (guard is null) return 1;

        using var connection = new JLinkConnection();
        WireDiagnostics(connection, command.Options.Verbose);
        if (!OpenOrReport(connection, config)) return 1;

        try
        {
            // JLINK_EraseChip only really erases after EnableEraseAllFlashBanks + with the core
            // reset-halted (see JLinkConnection.EraseChip for the on-target findings); a failed
            // preparation must abort loudly before any erase call.
            connection.ResetHalt();
            SetProgressReporting(connection, "erase");

            connection.EraseChip();   // throws with the DLL's code + message on failure
            Console.WriteLine("rttsh: chip erased.");
            if (command.Options.ResetOnConnect == true)
                connection.ResetAndResume();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Same contract as download: a failed preparation or erase reports and exits 1 -
            // "chip erased." is only ever printed after EraseChip returned without throwing.
            Console.Error.WriteLine($"rttsh: {ex.Message}");
            return 1;
        }
    }

    /// <summary>The gate behind `flash erase`: --yes passes, an interactive terminal may confirm
    /// with 'y', anything else is refused. Redirected stdin without --yes throws UsageException
    /// (exit 2) - a non-interactive caller cannot be asked, so the flag is not optional there;
    /// an interactive decline returns false and nothing has changed.</summary>
    internal static bool EnsureEraseAllowed(bool interactive, bool yes, TextReader input, TextWriter error)
    {
        if (yes) return true;
        if (!interactive)
            throw new UsageException("flash erase requires --yes when stdin is redirected (it erases the whole chip)");
        error.Write("Erase the WHOLE chip flash? This is destructive - type 'y' to continue: ");
        string? answer = input.ReadLine();
        return string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Best-effort halt before flash work: the DLL's flash loader halts on its own, but a
    /// running application on the bus (or in low-power mode) is the classic flash failure. A halt
    /// that cannot happen (export missing, core unreachable) is a verbose-only note - the download
    /// itself reports the real error with the DLL's code if it comes to that.</summary>
    private static void TryHalt(JLinkConnection connection, bool verbose)
    {
        try
        {
            connection.Halt();
        }
        catch (IOException ex)   // Halt's only failure shape (facade): export missing or code < 0
        {
            if (verbose)
                SessionSupport.WriteDiag($"rttsh: pre-flash halt skipped: {ex.Message}");
        }
    }

    /// <summary>Throttled progress lines on stderr (Compare/Erase/Program/Verify + percent);
    /// the 100% mark always prints so a short run still shows its tail.</summary>
    private static void SetProgressReporting(JLinkConnection connection, string what)
    {
        var reporter = new ProgressReporter();
        connection.SetFlashProgressCallback((action, progress, percentage) =>
            reporter.OnProgress(what, action, progress, percentage));
    }

    /// <summary>Instance field keeps the delegate alive for the native callback's lifetime even
    /// after SetProgressReporting's frame is gone (JLinkConnection holds it too).</summary>
    private sealed class ProgressReporter
    {
        private long _lastTicks;

        public void OnProgress(string what, string action, string progress, int percentage)
        {
            long now = Environment.TickCount64;
            if (percentage < 100 && now - _lastTicks < 200) return;
            _lastTicks = now;
            SessionSupport.WriteDiag($"flash {what} - {action}: {progress} {percentage}%");
        }
    }

    private static void WireDiagnostics(JLinkConnection connection, bool verbose)
    {
        if (verbose)
            connection.LogLine += SessionSupport.WriteDiag;
    }

    /// <summary>Shared open-and-report shape with SessionSupport.OpenOrReport, minus the RTT
    /// transport - flash runs against the bare JLinkConnection.</summary>
    private static bool OpenOrReport(JLinkConnection connection, RttConnectionConfig config)
    {
        try
        {
            connection.Open(config);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"rttsh: {ex.Message}");
            return false;
        }
    }
}
