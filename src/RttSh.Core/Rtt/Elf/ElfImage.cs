using ELFSharp;
using ELFSharp.ELF;
using ELFSharp.ELF.Sections;

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
/// degrade without catching. Load never throws; symbol and section metadata are materialized
/// eagerly while section contents are sliced from the image bytes on request. The library stays
/// an implementation detail: no ELFSharp type is reachable from the public surface (enforced by
/// the ElfNoLeakTests reflection test), and nothing outlives Load except our own records.
///
/// Scope decisions baked in: ELF64 is rejected (UnsupportedClass) per project decision;
/// big-endian images parse fine (symbol reads are endian-agnostic); only SHT_SYMTAB is read -
/// .dynsym is ignored (bare-metal images have no dynamic linker); entries without a name (the
/// null symbol, STT_SECTION with st_name=0) are not resolvable and not listed.</summary>
public sealed class ElfImage
{
    private static readonly byte[] Magic = [0x7f, (byte)'E', (byte)'L', (byte)'F'];

    private readonly byte[] _imageBytes;
    private readonly Dictionary<string, List<ElfSymbol>> _symbolsByName;
    private readonly Dictionary<string, ElfSection> _sectionsByName;

    private ElfImage(byte[] imageBytes, IReadOnlyList<ElfSymbol> symbols, IReadOnlyList<ElfSection> sections, bool isLittleEndian)
    {
        _imageBytes = imageBytes;
        Status = ElfLoadStatus.Ok;
        FailureReason = "";
        IsLittleEndian = isLittleEndian;
        Symbols = symbols;
        Sections = sections;
        _sectionsByName = sections
            .Where(s => s.Name.Length > 0)
            .GroupBy(s => s.Name)
            .ToDictionary(g => g.Key, g => g.First());   // duplicate section names: first match wins
        _symbolsByName = symbols
            .GroupBy(s => s.Name)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Address).ToList());
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
        _symbolsByName = [];
    }

    /// <summary>Parses an in-memory image. Never throws: any structural problem becomes a
    /// Status with a reason in <see cref="FailureReason"/>.</summary>
    public static ElfImage Load(byte[] bytes)
    {
        if (bytes is null || bytes.Length < Magic.Length || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            return new ElfImage(ElfLoadStatus.NotElf, "not an ELF image (bad \\x7fELF magic)");
        if (bytes.Length < 16)
            return new ElfImage(ElfLoadStatus.ParseFailed, "malformed ELF: truncated before the class field");

        // EI_CLASS: 1 = ELF32, 2 = ELF64. Checked before the library sees the bytes so a
        // class byte of 2 is reported as out-of-scope even when the rest is garbage.
        switch (bytes[4])
        {
            case 1: break;
            case 2: return new ElfImage(ElfLoadStatus.UnsupportedClass, "ELF64 images are out of scope (ELF32 only)");
            default: return new ElfImage(ElfLoadStatus.ParseFailed, $"malformed ELF: invalid EI_CLASS {bytes[4]}");
        }

        IELF elf;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            // The bool stays positional: ELFSharp 2.17.3's second parameter is not what you'd
            // guess from "leaveOpen", and false is safe under either reading.
            try
            {
                elf = ELFReader.Load(stream, false);
            }
            catch (Exception ex)
            {
                return new ElfImage(ElfLoadStatus.ParseFailed, $"malformed ELF: {FirstLine(ex.Message)}");
            }
        }

        if (elf.Class != Class.Bit32)
            return new ElfImage(ElfLoadStatus.UnsupportedClass, "ELF64 images are out of scope (ELF32 only)");

        try
        {
            return Materialize(bytes, (ELF<uint>)elf);
        }
        catch (Exception ex)
        {
            return new ElfImage(ElfLoadStatus.ParseFailed, $"malformed ELF: {FirstLine(ex.Message)}");
        }
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
    /// GCC ships symbols like memmove twice, and ARM mapping symbols share names wholesale).</summary>
    public SymbolLookup Lookup(string name, ElfSymbolKind? kind = null)
    {
        if (Status != ElfLoadStatus.Ok || string.IsNullOrEmpty(name))
            return SymbolLookup.NotFound();
        if (!_symbolsByName.TryGetValue(name, out var matches))
            return SymbolLookup.NotFound();
        if (kind is { } wanted)
            matches = matches.Where(s => s.Kind == wanted).ToList();
        return matches.Count switch
        {
            0 => SymbolLookup.NotFound(),
            1 => SymbolLookup.Found(matches[0]),
            _ => SymbolLookup.Ambiguous(matches),
        };
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

    private static ElfImage Materialize(byte[] bytes, ELF<uint> elf)
    {
        bool littleEndian = elf.Endianess == Endianess.LittleEndian;

        var sections = new List<ElfSection>();
        SymbolTable<uint>? symtab = null;
        foreach (var raw in elf.Sections.OfType<Section<uint>>())
        {
            if (raw.Name.Length > 0)
                sections.Add(new ElfSection(raw.Name, raw.LoadAddress, raw.Size, raw.Offset, raw.Type == SectionType.ProgBits));
            if (symtab is null && raw.Type == SectionType.SymbolTable)
                symtab = raw as SymbolTable<uint>;
        }

        var symbols = new List<ElfSymbol>();
        if (symtab is not null)
        {
            foreach (var entry in symtab.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;   // null symbol, STT_SECTION with st_name=0, and any other unnamed entry
                symbols.Add(new ElfSymbol(
                    entry.Name,
                    entry.Value,
                    entry.Size,
                    MapKind(entry.Type),
                    MapBinding(entry.Binding)));
            }
        }
        symbols.Sort((a, b) => a.Address.CompareTo(b.Address));
        return new ElfImage(bytes, symbols, sections, littleEndian);
    }

    private static ElfSymbolKind MapKind(SymbolType type) => type switch
    {
        SymbolType.Object => ElfSymbolKind.Object,
        SymbolType.Function => ElfSymbolKind.Function,
        SymbolType.Section => ElfSymbolKind.Section,
        SymbolType.File => ElfSymbolKind.File,
        SymbolType.NotSpecified => ElfSymbolKind.NotTyped,
        _ => ElfSymbolKind.Other,
    };

    private static ElfSymbolBinding MapBinding(SymbolBinding binding) => binding switch
    {
        SymbolBinding.Global => ElfSymbolBinding.Global,
        SymbolBinding.Local => ElfSymbolBinding.Local,
        SymbolBinding.Weak => ElfSymbolBinding.Weak,
        _ => ElfSymbolBinding.Other,
    };

    /// <summary>Library exception texts can be long multi-line dumps; the reason string stays
    /// one line so a future CLI warning reads cleanly.</summary>
    private static string FirstLine(string text)
    {
        int cut = text.IndexOfAny(['\r', '\n']);
        string line = cut < 0 ? text : text[..cut];
        return line.Length == 0 ? "parser failed" : line.Trim();
    }
}
