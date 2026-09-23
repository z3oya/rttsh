using Toolbox.Core.Rtt;
using Toolbox.Core.Rtt.Elf;

namespace Toolbox.Tools.RttCli;

/// <summary>--elf: resolve the RTT control-block address from a firmware image and pin it onto
/// the connection options before dispatch. Layering is deliberate and load-bearing:
/// Decide carries ALL policy (and never touches files or consoles - tests drive it with
/// in-memory images, and its load delegate proves when IO must not happen); Apply is the
/// mechanical shell that reads, maps file-level problems onto usage errors, writes one
/// diagnostic line, and rewrites RttAddress/RttRange.
///
/// Policy summary: an explicit --rtt-addr/--rttAddr (CLI or config) wins without consulting
/// the file (a temporary debug address must not be blocked by a stale config image); a
/// resolved control block clears --rtt-range, because a window scan at the resolved address
/// could lock onto a different "SEGGER RTT" hit; content problems (no symbol, ambiguous,
/// implausible) degrade to the SDK RAM scan with a warning, while file-level problems
/// (missing, unreadable, not an ELF32 image) are usage errors - exit 2, like --config.</summary>
internal static class ElfResolver
{
    /// <summary>The --elf outcome: pin Address onto the connection (ClearRange accompanies it
    /// when --rtt-range must not interfere), with Message as the always-shown diagnostic.</summary>
    internal sealed record ElfDecision(bool Override, uint Address, bool ClearRange, string Message)
    {
        public static ElfDecision Fallback(string message) => new(false, 0, false, message);
        public static ElfDecision Pin(uint address, string info, bool clearRange) => new(true, address, clearRange, info);
    }

    /// <summary>Applies --elf to the options: no-op without a path, otherwise load, decide,
    /// announce, and pin. Called from Program's pre-dispatch switch for monitor/send/script.</summary>
    public static void Apply(CommandLineOptions options)
    {
        if (string.IsNullOrEmpty(options.ElfPath))
            return;

        ElfDecision decision = Decide(options.RttAddress, options.RttRange is not null, options.ElfPath,
            () => LoadOrUsage(options.ElfPath));

        SessionSupport.WriteDiag($"rtt-cli: {decision.Message}");
        if (decision.Override)
        {
            options.RttAddress = decision.Address;
            if (decision.ClearRange)
                options.RttRange = null;
        }
    }

    /// <summary>The whole --elf policy over one already-loaded image.</summary>
    internal static ElfDecision Decide(uint? explicitAddress, bool explicitRange, string elfPath, Func<ElfImage> load)
    {
        if (explicitAddress is not null)
            return ElfDecision.Fallback(
                $"--elf ignored: --rtt-addr/--rttAddr already pins the control block (0x{explicitAddress.Value:X})");

        ElfImage image = load();
        if (image.Status != ElfLoadStatus.Ok)
            throw new UsageException($"--elf: cannot use '{elfPath}': {image.FailureReason}");

        RttElfLocateResult locate = RttControlBlock.LocateFromElf(image);
        return locate.Status switch
        {
            RttElfLocateStatus.Resolved => ElfDecision.Pin(locate.Address,
                $"--elf: {RttControlBlock.ControlBlockSymbolName} at 0x{locate.Address:X} (from '{elfPath}')"
                + (explicitRange ? "; --rtt-range ignored (the resolved address is used as-is)" : ""),
                clearRange: explicitRange),
            RttElfLocateStatus.SymbolMissing => ElfDecision.Fallback(
                $"--elf: {RttControlBlock.ControlBlockSymbolName} not in '{elfPath}' " +
                "(built without RTT, or stripped); falling back to the SDK RAM scan"),
            _ => ElfDecision.Fallback($"--elf: {locate.Reason}; falling back to the SDK RAM scan"),
        };
    }

    /// <summary>File-level problems are usage errors with the option's own prefix; the exact
    /// message of the underlying IO failure is kept for locked/ACL cases.</summary>
    private static ElfImage LoadOrUsage(string path)
    {
        try
        {
            return ElfImage.FromFile(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
                                       or IOException or UnauthorizedAccessException)
        {
            throw new UsageException($"--elf: cannot read '{path}': {ex.Message}");
        }
    }
}
