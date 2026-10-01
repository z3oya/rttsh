namespace RttSh.Core.Rtt.Elf;

/// <summary>Load outcome, mirroring the CLI's error ladder. IO failures are deliberately NOT
/// here - FromFile lets them propagate as exceptions because a missing/unreadable file is a
/// usage error, while these statuses describe what the bytes themselves are.</summary>
public enum ElfLoadStatus
{
    Ok,
    NotElf,
    UnsupportedClass,
    ParseFailed,
}

/// <summary>Section metadata for consumers that need where bytes live, not just the bytes
/// themselves (variable watch will need file offsets to read .data initial values).
/// HasContents is false for SHT_NOBITS and every other non-PROGBITS type - only those sections
/// can be fetched via ElfImage.TryGetSectionBytes.</summary>
public sealed record ElfSection(
    string Name,
    ulong Address,
    ulong Size,
    long FileOffset,
    bool HasContents);

/// <summary>Read-only ELF32 image over our own types: symbols, section lookup, and a status
/// that mirrors the CLI's error ladder (NotElf / UnsupportedClass / ParseFailed) so callers can
/// degrade without catching. Thin facade over the Rust ELF crate (native/rttsh-elf, via
/// <see cref="ElfNative"/>): parsing, the load ladder, symbol lookup and the control-block
/// locate live in the native model behind a SafeHandle; this class materializes the records
/// the public surface exposes and keeps the image bytes for section-content slicing. Load
/// never throws for image problems; symbol and section records are materialized eagerly while
/// section contents are sliced from the image bytes on request.
///
/// Scope decisions baked in: ELF64 is rejected (UnsupportedClass) per project decision;
/// big-endian images parse fine (symbol reads are endian-agnostic); only SHT_SYMTAB is read -
/// .dynsym is ignored (bare-metal images have no dynamic linker); entries without a name (the
/// null symbol, STT_SECTION with st_name=0) are not resolvable and not listed.</summary>
public sealed class ElfImage
{
    private readonly byte[] _imageBytes;
    private readonly Dictionary<string, ElfSection> _sectionsByName;
    private readonly ElfNativeHandle? _native;

    private ElfImage(byte[] imageBytes, ElfNativeParseResult parsed)
    {
        _imageBytes = imageBytes;
        _native = parsed.Handle;
        Status = ElfLoadStatus.Ok;
        FailureReason = "";
        IsLittleEndian = parsed.IsLittleEndian;
        Symbols = parsed.Materialization!.Symbols
            .Select(s => new ElfSymbol(s.Name, s.Address, s.Size, (ElfSymbolKind)s.Kind, (ElfSymbolBinding)s.Binding))
            .ToArray();
        Sections = parsed.Materialization.Sections
            .Select(s => new ElfSection(s.Name, s.Address, s.Size, s.FileOffset, s.HasContents))
            .ToArray();
        _sectionsByName = Sections
            .Where(s => s.Name.Length > 0)
            .GroupBy(s => s.Name)
            .ToDictionary(g => g.Key, g => g.First());   // duplicate section names: first match wins
    }

    private ElfImage(ElfLoadStatus status, string failureReason)
    {
        Status = status;
        FailureReason = failureReason;
        IsLittleEndian = true;
        Symbols = [];
        Sections = [];
        _imageBytes = [];
        _sectionsByName = [];
    }

    /// <summary>Parses an in-memory image. Never throws for image problems: any structural
    /// issue becomes a Status with a reason in <see cref="FailureReason"/>. The magic and
    /// class-byte checks run in the native parser before anything else, so a class byte of 2
    /// is reported as out-of-scope even when the rest is garbage.</summary>
    public static ElfImage Load(byte[]? bytes)
    {
        ElfNativeParseResult parsed = ElfNative.Parse(bytes);
        return parsed.Status == ElfLoadStatus.Ok
            ? new ElfImage(bytes ?? [], parsed)
            : new ElfImage(parsed.Status, parsed.Reason);
    }

    /// <summary>Reads a file and parses it. IO failures (missing path, unreadable) propagate to
    /// the caller as exceptions - they are usage errors, not image problems; only the bytes'
    /// content is reported through Status.</summary>
    public static ElfImage FromFile(string path) => Load(File.ReadAllBytes(path));

    public ElfLoadStatus Status { get; }
    public string FailureReason { get; }
    public bool IsLittleEndian { get; }

    /// <summary>Every named .symtab entry, ordered by address. Empty when Status is not Ok.</summary>
    public IReadOnlyList<ElfSymbol> Symbols { get; }

    /// <summary>Every named section, in file order. Empty when Status is not Ok.</summary>
    public IReadOnlyList<ElfSection> Sections { get; }

    /// <summary>Looks a symbol up by exact name, optionally restricted to one kind. Kind
    /// filtering runs BEFORE the uniqueness check, so a FUNC and an OBJECT sharing a name do
    /// not look ambiguous to an OBJECT-only query (the RTT control-block path relies on this -
    /// GCC ships symbols like memmove twice, and ARM mapping symbols share names wholesale).
    /// The semantics live in the native crate; the status guard here keeps failed images
    /// answer-only-NotFound without crossing the FFI boundary.</summary>
    public SymbolLookup Lookup(string name, ElfSymbolKind? kind = null)
    {
        if (Status != ElfLoadStatus.Ok || string.IsNullOrEmpty(name))
            return SymbolLookup.NotFound();
        return ElfNative.Lookup(_native!, name, kind);
    }

    /// <summary>Fetches the raw bytes of a PROGBITS section by name - a slice of the image
    /// bytes we already hold, so nothing is retained or re-read. NoBits (e.g. .bss) has no
    /// file-backed contents and returns false, as does any absent section.</summary>
    public bool TryGetSectionBytes(string name, out byte[] data)
    {
        if (_sectionsByName.TryGetValue(name, out ElfSection? section) && section.HasContents)
        {
            data = _imageBytes.AsSpan(checked((int)section.FileOffset), checked((int)section.Size)).ToArray();
            return true;
        }
        data = [];
        return false;
    }

    /// <summary>Section metadata lookup by name; first match wins when names repeat.</summary>
    public bool TryGetSection(string name, out ElfSection section)
    {
        if (_sectionsByName.TryGetValue(name, out ElfSection? found))
        {
            section = found;
            return true;
        }
        section = default!;
        return false;
    }

    /// <summary>The native model handle; only meaningful for an Ok image (callers guard on
    /// Status first - RttControlBlock.LocateFromElf composes its InvalidImage reasons there).</summary>
    internal ElfNativeHandle NativeHandle => _native!;
}
