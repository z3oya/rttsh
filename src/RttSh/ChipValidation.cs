namespace Toolbox.Tools.RttCli;

/// <summary>Pre-dispatch chip-name validation against the J-Link DLL device database - the same
/// enumeration list-devices prints. An unknown name must not reach JLinkConnection's
/// ExecCommand("device = ..."): the DLL answers an unknown device name by opening its modal
/// device-selection dialog, which hangs a non-interactive run until the dialog is dismissed.
/// Every degradation is silent by design - when the DLL is missing, fails to load, or lacks the
/// enumeration exports (very old DLLs), the session path keeps its own error handling, so the
/// check only ever narrows the failure modes, never widens them. The DLL is loaded twice per
/// run (here for the enumeration, then again by the session's connection); list-devices already
/// establishes load-and-enumerate without a link, and the cost is milliseconds against a run
/// that is about to open a hardware link anyway.</summary>
internal static class ChipValidation
{
    /// <summary>The dispatch-time step, called from Program.Run right after EnsureChip - after
    /// the config merge, so a chip coming from .rttsh/config.json is validated too. A
    /// whitespace-only chip is left to EnsureChip and the session's Clamped() backstop.</summary>
    public static void EnsureKnown(CommandLineOptions options) =>
        EnsureKnown(options.Chip.Trim(), options.DllPath);

    /// <summary>The (chip, dllPath) core behind the options overload; the MCP tool host
    /// validates its connect calls through this same path.</summary>
    public static void EnsureKnown(string chip, string dllPath)
    {
        if (chip.Length == 0)
            return;

        using var library = new JLinkLibrary();
        if (!library.Load(dllPath, out _))
            return;
        if (!JLinkDeviceDatabase.TryEnumerate(library, out List<JLinkDeviceRecord> records, out _))
            return;
        EnsureKnownInDb(records, chip);
    }

    /// <summary>The policy over one already-enumerated database, DLL-free so tests drive it:
    /// the DLL resolves device names case-insensitively, so the check does too (it must not
    /// reject a name the DLL would accept); anything else is a usage error pointing at
    /// list-devices and naming similar entries.</summary>
    internal static void EnsureKnownInDb(List<JLinkDeviceRecord> records, string chip)
    {
        if (records.Any(r => r.Name.Equals(chip, StringComparison.OrdinalIgnoreCase)))
            return;

        List<string> similar = SimilarNames(records, chip);
        string message = $"unknown chip '{chip}' - no such name in the J-Link device database " +
                         $"(list exact names with 'rttsh list-devices --filter {chip}')";
        if (similar.Count > 0)
            message += $". Similar names: {string.Join(", ", similar)}";
        throw new UsageException(message);
    }

    /// <summary>Database-order names containing the chip as a substring, capped so the usage
    /// error stays one line.</summary>
    internal static List<string> SimilarNames(List<JLinkDeviceRecord> records, string chip)
    {
        var hits = new List<string>();
        foreach (JLinkDeviceRecord record in records)
        {
            if (record.Name.Contains(chip, StringComparison.OrdinalIgnoreCase))
                hits.Add(record.Name);
            if (hits.Count == 6)
                break;
        }
        return hits;
    }
}
