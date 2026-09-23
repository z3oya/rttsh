namespace RttSh.Core.Rtt;

using RttSh.Core.Rtt.Elf;

/// <summary>Outcome of resolving the control block through a firmware image's symbol table.
/// The statuses are the phase-3 policy ladder: SymbolMissing means "warn and let the SDK scan"
/// (the firmware may simply not be built with RTT), while Ambiguous and ImplausibleSize mean
/// the image resolved but cannot be trusted - also a scan fallback, but the CLI warning should
/// tell the user the image itself is suspect.</summary>
public enum RttElfLocateStatus
{
    Resolved,
    SymbolMissing,
    Ambiguous,
    ImplausibleSize,
    InvalidImage,
}

/// <summary>Locate result: the resolved address when Resolved, and a one-line human-readable
/// reason in every other case (also a short confirmation note on success, for verbose logs).</summary>
public sealed record RttElfLocateResult(RttElfLocateStatus Status, uint Address, string Reason)
{
    public bool Resolved => Status == RttElfLocateStatus.Resolved;
}

/// <summary>The control block identification used when the caller pins a RAM window
/// (RttAddress + RttRange): the "SEGGER RTT" magic preceding the block header.</summary>
public static class RttControlBlock
{
    /// <summary>SEGGER_RTT.c's control-block variable; the de-facto lookup target across the
    /// RTT tool ecosystem (RTT viewers, probe-rs, pyOCD all resolve this name).</summary>
    public const string ControlBlockSymbolName = "_SEGGER_RTT";

    public static readonly byte[] Signature = "SEGGER RTT"u8.ToArray();

    /// <summary>Offset of the first signature occurrence in a RAM dump, or -1 when absent
    /// (case-sensitive, no partial tail match — same semantics as the reference tool's indexOf).</summary>
    public static int FindSignature(ReadOnlySpan<byte> dump) => dump.IndexOf(Signature);

    /// <summary>Resolves the control-block address from a firmware image's symbol table: the
    /// unique OBJECT named _SEGGER_RTT whose size fits the SEGGER_RTT_CB layout (16-byte ID +
    /// two ints + N buffer descriptors; descriptors are 24 bytes in current SEGGER releases
    /// with the sName field, 20 bytes in legacy ones). 64 is the smallest legal block: one up
    /// and one down buffer in the legacy layout. Zero-buffer (24) or oversized sizes fail the
    /// plausibility gate - a wrong-build image should fail here with a nameable reason.
    ///
    /// Deliberately no section-containment gate: a custom linker script may place the block in
    /// an unusual (but valid) section, and a cross-build mismatch is caught later by reading
    /// the 16-byte ID off the target and running FindSignature on it. Endianness and the Thumb
    /// bit never matter: OBJECT st_value is the plain RAM address.</summary>
    public static RttElfLocateResult LocateFromElf(ElfImage? image)
    {
        if (image is null)
            return new RttElfLocateResult(RttElfLocateStatus.InvalidImage, 0, "no firmware image to resolve _SEGGER_RTT from");
        if (image.Status != ElfLoadStatus.Ok)
            return new RttElfLocateResult(RttElfLocateStatus.InvalidImage, 0, $"cannot resolve {ControlBlockSymbolName}: {image.FailureReason}");

        SymbolLookup lookup = image.Lookup(ControlBlockSymbolName, ElfSymbolKind.Object);
        switch (lookup.Status)
        {
            case SymbolLookupStatus.NotFound:
                return new RttElfLocateResult(RttElfLocateStatus.SymbolMissing, 0,
                    $"{ControlBlockSymbolName} not in the symbol table (firmware built without RTT, " +
                    "or the image is stripped); fall back to the SDK RAM scan");
            case SymbolLookupStatus.Ambiguous:
                string candidates = string.Join(", ", lookup.Candidates.Select(c => $"0x{c.Address:X}"));
                return new RttElfLocateResult(RttElfLocateStatus.Ambiguous, 0,
                    $"ambiguous {ControlBlockSymbolName}: {lookup.Candidates.Count} OBJECT candidates at {candidates}");
        }

        lookup.TryGetSymbol(out ElfSymbol cb);
        ulong size = cb.Size;
        ulong body = size >= 24 ? size - 24 : 0;
        if (size < 64 || (body % 24 != 0 && body % 20 != 0))
        {
            return new RttElfLocateResult(RttElfLocateStatus.ImplausibleSize, 0,
                $"implausible {ControlBlockSymbolName} size {size} (control block is 16+2*4 bytes plus " +
                "20- or 24-byte buffer descriptors) - image likely from a different build");
        }

        uint address = (uint)cb.Address;
        return new RttElfLocateResult(RttElfLocateStatus.Resolved, address,
            $"resolved {ControlBlockSymbolName} at 0x{address:X} (size {size}); " +
            "verify the \"SEGGER RTT\" ID on target before trusting it");
    }
}
