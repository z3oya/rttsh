using System.Buffers.Binary;

namespace Toolbox.Tests;

/// <summary>Synthetic ELF32 builder for the Elf submodule tests: emits a minimal but fully
/// valid image (ELF header, the sections you declare, optional .symtab/.dynsym, .shstrtab) so
/// every branch of the parser can be exercised without real firmware. Pure byte assembly - no
/// ELFSharp involvement, so parser failures cannot be masked by builder failures. Tests declare
/// everything explicitly; there are no implicit sections or symbols.
///
/// Symbols are given as raw ELF encodings (type/bind nibbles) to keep the builder independent
/// of the module's own enums: a mis-mapping in ElfImage then shows up as a test failure instead
/// of silently matching the builder's vocabulary.</summary>
internal sealed class ElfBuilder
{
    public const byte TypeNotTyped = 0, TypeObject = 1, TypeFunc = 2, TypeSection = 3, TypeFile = 4;
    public const byte BindLocal = 0, BindGlobal = 1, BindWeak = 2;

    public const ushort ShnAbs = 0xfff1;   // SHN_ABS: value is absolute, not section-relative

    /// <summary>A symbol to place into .symtab (or .dynsym). shndx defaults to the first data
    /// section; ABS symbols pass ShnAbs.</summary>
    public sealed record Sym(string Name, uint Value, uint Size, byte Type, byte Bind, ushort Shndx = 1);

    public sealed record DataSection(string Name, uint Address, byte[] Contents);

    public sealed record NoBitsSection(string Name, uint Address, uint Size);

    public bool BigEndian { get; set; }
    /// <summary>Emits a header whose EI_CLASS is 2 (ELF64) with a zeroed body: enough for the
    /// ElfImage class-byte pre-check, which must reject it before any parser runs.</summary>
    public bool Elf64Header { get; set; }
    public bool WithSymtab { get; set; } = true;
    public bool WithDynsym { get; set; }
    public List<Sym> Symbols { get; } = [];
    public List<Sym> DynamicSymbols { get; } = [];
    public List<DataSection> DataSections { get; } = [];
    public List<NoBitsSection> NoBitsSections { get; } = [];

    public byte[] Build()
    {
        if (Elf64Header)
        {
            var h = new byte[64];
            Magic.AsSpan().CopyTo(h);
            h[4] = 2;   // ELFCLASS64
            h[5] = (byte)(BigEndian ? 2 : 1);
            return h;
        }

        // ---- string tables (offset 0 is the mandatory NUL); offsets feed name fields ----
        List<string> symbolNames = [.. Symbols.Select(s => s.Name), .. DynamicSymbols.Select(s => s.Name)];
        var (strtab, strtabOffsets) = BuildStrtab(WithSymtab ? symbolNames : []);
        var (dynstr, dynstrOffsets) = BuildStrtab(WithDynsym ? DynamicSymbols.Select(s => s.Name) : []);

        var sectionNames = new List<string> { "" };
        sectionNames.AddRange(DataSections.Select(d => d.Name));
        sectionNames.AddRange(NoBitsSections.Select(s => s.Name));
        if (WithSymtab) { sectionNames.Add(".symtab"); sectionNames.Add(".strtab"); }
        if (WithDynsym) { sectionNames.Add(".dynsym"); sectionNames.Add(".dynstr"); }
        sectionNames.Add(".shstrtab");
        var (shstrtab, shstrtabOffsets) = BuildStrtab(sectionNames);
        uint NameOff(string name) => shstrtabOffsets.GetValueOrDefault(name);

        // ---- section table: entry and body travel together, so layout can never desync ----
        const int ehsize = 52, shentsize = 40;
        var sections = new List<Shdr> { Shdr.Null() };
        foreach (var d in DataSections)
            sections.Add(Shdr.ProgBits(NameOff(d.Name), d.Address, d.Contents));
        foreach (var n in NoBitsSections)
            sections.Add(Shdr.NoBits(NameOff(n.Name), n.Address, n.Size));
        int symtabIndex = sections.Count;
        if (WithSymtab)
        {
            sections.Add(Shdr.Symtab(NameOff(".symtab"), BuildSymtab(Symbols, strtabOffsets), link: (uint)(sections.Count + 1)));
            sections.Add(Shdr.Strtab(NameOff(".strtab"), strtab));
        }
        if (WithDynsym)
        {
            sections.Add(Shdr.Dynsym(NameOff(".dynsym"), BuildSymtab(DynamicSymbols, dynstrOffsets), link: (uint)(sections.Count + 1)));
            sections.Add(Shdr.Strtab(NameOff(".dynstr"), dynstr));
        }
        sections.Add(Shdr.Strtab(NameOff(".shstrtab"), shstrtab));
        int shstrndx = sections.Count - 1;

        // ---- file layout: ehdr | bodies (4-aligned) | section header table ----
        uint shoff = ehsize;
        var offsets = new uint[sections.Count];
        for (int i = 0; i < sections.Count; i++)
        {
            if (sections[i].Body is not { } body) continue;
            shoff = (shoff + 3u) & ~3u;
            offsets[i] = shoff;
            shoff += (uint)body.Length;
        }
        shoff = (shoff + 3u) & ~3u;

        using var outStream = new MemoryStream();
        using var writer = new BinaryWriter(outStream);

        // ---- ELF header ----
        writer.Write(Magic);
        writer.Write((byte)1);                    // EI_CLASS = ELF32
        writer.Write((byte)(BigEndian ? 2 : 1));  // EI_DATA
        writer.Write((byte)1);                    // EI_VERSION
        writer.Write((byte)0);                    // EI_OSABI
        writer.Write((byte)0);                    // EI_ABIVERSION
        writer.Write(new byte[7]);                // EI_PAD - e_ident totals 16 bytes
        W16(2); W16(40); W32(1);                  // e_type=EXEC, e_machine=ARM, e_version
        W32(0x0800_0000);                         // e_entry
        W32(0); W32(shoff);                       // e_phoff, e_shoff
        W32(0);                                   // e_flags
        W16(ehsize); W16(0); W16(0);              // e_ehsize, e_phentsize, e_phnum
        W16(shentsize); W16((ushort)sections.Count); W16((ushort)shstrndx);

        // ---- section bodies, then the section header table ----
        for (int i = 0; i < sections.Count; i++)
        {
            if (sections[i].Body is not { } body) continue;
            PadTo(offsets[i]);
            writer.Write(body);
        }
        PadTo(shoff);
        for (int i = 0; i < sections.Count; i++)
        {
            Shdr s = sections[i];
            WriteShdr(s.NameOff, s.Type, flags: 0, s.Addr, s.Body is null ? 0 : offsets[i], s.Size, s.Link, s.Info, s.Align, s.EntSize);
        }

        writer.Flush();
        return outStream.ToArray();

        void W16(ushort v) { Span<byte> b = stackalloc byte[2]; if (BigEndian) BinaryPrimitives.WriteUInt16BigEndian(b, v); else BinaryPrimitives.WriteUInt16LittleEndian(b, v); writer.Write(b); }
        void W32(uint v) { Span<byte> b = stackalloc byte[4]; if (BigEndian) BinaryPrimitives.WriteUInt32BigEndian(b, v); else BinaryPrimitives.WriteUInt32LittleEndian(b, v); writer.Write(b); }
        void PadTo(uint target) { while (writer.BaseStream.Length < target) writer.Write((byte)0); }
        void WriteShdr(uint name, uint type, uint flags, uint addr, uint off, uint size, uint link, uint info, uint align, uint entsize)
        {
            W32(name); W32(type); W32(flags); W32(addr); W32(off); W32(size); W32(link); W32(info); W32(align); W32(entsize);
        }
    }

    private static readonly byte[] Magic = [0x7f, (byte)'E', (byte)'L', (byte)'F'];

    /// <summary>One section header plus its file body (null for NOBITS and the null section) -
    /// keeping them together is what makes the file layout unable to desync from the table.</summary>
    private sealed record Shdr(
        uint NameOff, uint Type, uint Addr, uint Size, uint Link, uint Info, uint Align, uint EntSize, byte[]? Body)
    {
        public static Shdr Null() => new(0, 0, 0, 0, 0, 0, 0, 0, null);
        public static Shdr ProgBits(uint nameOff, uint addr, byte[] body) => new(nameOff, 1, addr, (uint)body.Length, 0, 0, 4, 0, body);
        public static Shdr NoBits(uint nameOff, uint addr, uint size) => new(nameOff, 8, addr, size, 0, 0, 1, 0, null);
        public static Shdr Symtab(uint nameOff, byte[] body, uint link) => new(nameOff, 2, 0, (uint)body.Length, link, 1, 4, 16, body);
        public static Shdr Dynsym(uint nameOff, byte[] body, uint link) => new(nameOff, 11, 0, (uint)body.Length, link, 1, 4, 16, body);
        public static Shdr Strtab(uint nameOff, byte[] body) => new(nameOff, 3, 0, (uint)body.Length, 0, 0, 1, 0, body);
    }

    /// <summary>Builds a string table (offset 0 = NUL, each name NUL-terminated) and the name
    /// offsets in the same pass, so they cannot disagree.</summary>
    private (byte[] Bytes, Dictionary<string, uint> Offsets) BuildStrtab(IEnumerable<string> names)
    {
        var offsets = new Dictionary<string, uint>();
        var body = new List<byte> { 0 };
        foreach (var name in names.Where(n => n.Length > 0))
        {
            offsets.TryAdd(name, (uint)body.Count);
            foreach (char c in name) body.Add((byte)c);
            body.Add(0);
        }
        return ([.. body], offsets);
    }

    private byte[] BuildSymtab(IReadOnlyList<Sym> symbols, Dictionary<string, uint> strtabOffsets)
    {
        using var outStream = new MemoryStream();
        using var writer = new BinaryWriter(outStream);

        void W16(ushort v) { Span<byte> b = stackalloc byte[2]; if (BigEndian) BinaryPrimitives.WriteUInt16BigEndian(b, v); else BinaryPrimitives.WriteUInt16LittleEndian(b, v); writer.Write(b); }
        void W32(uint v) { Span<byte> b = stackalloc byte[4]; if (BigEndian) BinaryPrimitives.WriteUInt32BigEndian(b, v); else BinaryPrimitives.WriteUInt32LittleEndian(b, v); writer.Write(b); }
        void WriteSym(uint name, uint value, uint size, byte info, byte other, ushort shndx)
        {
            W32(name); W32(value); W32(size); writer.Write(info); writer.Write(other); W16(shndx);
        }

        // The mandatory null symbol (all zeros) first, matching real linkers.
        WriteSym(0, 0, 0, 0, 0, 0);
        foreach (var s in symbols)
            WriteSym(strtabOffsets.GetValueOrDefault(s.Name), s.Value, s.Size,
                     (byte)((s.Bind << 4) | s.Type), 0 /*st_other*/, s.Shndx);
        writer.Flush();
        return outStream.ToArray();
    }
}
