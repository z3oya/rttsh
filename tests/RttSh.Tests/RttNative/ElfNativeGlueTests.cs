using RttSh.Core.Rtt;
using RttSh.Core.Rtt.Elf;
using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttNative;

/// <summary>Glue coverage for the Rust ELF crate (rttsh_elf_native.dll) through the
/// ElfImage facade: the load ladder, materialized symbols/sections, lookup semantics,
/// the control-block locate rungs, and the panic discipline. Images come from the C#
/// ElfBuilder - a second, independent encoder whose output the Rust parser must accept
/// (the Rust-side tests do the mirror image with their own builder). Pure in-process
/// FFI - no hardware.</summary>
public class ElfNativeGlueTests
{
    private const string CbName = "_SEGGER_RTT";

    private static byte[] Image(Action<ElfBuilder>? configure = null)
    {
        var builder = new ElfBuilder();
        configure?.Invoke(builder);
        return builder.Build();
    }

    // ---- ABI ---------------------------------------------------------------------------

    [Fact]
    public void AbiVersion_Matches()
    {
        ElfNative.EnsureAbi();   // first use fails fast on a missing/mismatched DLL
        Assert.Equal(ElfNative.AbiVersion, ElfNative.RttshElfAbiVersion());
    }

    [Fact]
    public void PanicProbe_Answers_ErrInternal()
    {
        Assert.Equal(ElfNative.ErrInternal, ElfNative.PanicProbe());
    }

    // ---- the load ladder through the facade ---------------------------------------------

    [Fact]
    public void A_well_formed_image_loads_ok()
    {
        ElfImage image = ElfImage.Load(Image(b =>
        {
            b.DataSections.Add(new ElfBuilder.DataSection(".text", 0x0800_0000, [0xAA, 0xBB]));
            b.Symbols.Add(new ElfBuilder.Sym(CbName, 0x2400_0070, 168, ElfBuilder.TypeObject, ElfBuilder.BindGlobal));
        }));
        Assert.Equal(ElfLoadStatus.Ok, image.Status);
        Assert.Equal("", image.FailureReason);
        Assert.True(image.IsLittleEndian);
    }

    [Fact]
    public void Not_elf_carries_the_verbatim_reason()
    {
        ElfImage image = ElfImage.Load([1, 2, 3]);
        Assert.Equal(ElfLoadStatus.NotElf, image.Status);
        Assert.Equal(@"not an ELF image (bad \x7fELF magic)", image.FailureReason);
        Assert.Empty(image.Symbols);
        Assert.Empty(image.Sections);
        Assert.Equal(ElfLoadStatus.NotElf, ElfImage.Load(null).Status);
    }

    [Fact]
    public void Elf64_is_rejected_before_anything_else_parses()
    {
        ElfImage image = ElfImage.Load(new ElfBuilder { Elf64Header = true }.Build());
        Assert.Equal(ElfLoadStatus.UnsupportedClass, image.Status);
        Assert.Equal("ELF64 images are out of scope (ELF32 only)", image.FailureReason);
    }

    [Fact]
    public void A_truncated_image_fails_as_malformed()
    {
        ElfImage image = ElfImage.Load([0x7f, (byte)'E', (byte)'L', (byte)'F', 1, 0, 0]);
        Assert.Equal(ElfLoadStatus.ParseFailed, image.Status);
        Assert.StartsWith("malformed ELF:", image.FailureReason);
    }

    // ---- materialization ----------------------------------------------------------------

    [Fact]
    public void Symbols_materialize_named_sorted_and_mapped()
    {
        ElfImage image = ElfImage.Load(Image(b =>
        {
            // Declared out of address order; the FUNC twin proves the OBJECT filter later.
            b.Symbols.Add(new ElfBuilder.Sym("late", 0x2400_1000, 4, ElfBuilder.TypeFunc, ElfBuilder.BindWeak));
            b.Symbols.Add(new ElfBuilder.Sym("early", 0x2400_0000, 8, ElfBuilder.TypeObject, ElfBuilder.BindGlobal));
        }));

        Assert.Equal(["early", "late"], image.Symbols.Select(s => s.Name).ToArray());
        ElfSymbol early = image.Symbols[0];
        Assert.Equal(0x2400_0000ul, early.Address);
        Assert.Equal(8ul, early.Size);
        Assert.Equal(ElfSymbolKind.Object, early.Kind);
        Assert.Equal(ElfSymbolBinding.Global, early.Binding);
        Assert.Equal(ElfSymbolBinding.Weak, image.Symbols[1].Binding);
    }

    [Fact]
    public void Big_endian_images_report_their_endianness()
    {
        ElfImage image = ElfImage.Load(Image(b =>
        {
            b.BigEndian = true;
            b.Symbols.Add(new ElfBuilder.Sym("be_cb", 0x2400_0070, 168, ElfBuilder.TypeObject, ElfBuilder.BindGlobal));
        }));
        Assert.True(image.Status == ElfLoadStatus.Ok);
        Assert.False(image.IsLittleEndian);
        Assert.Equal(0x2400_0070ul, image.Symbols.Single().Address);
    }

    // ---- lookup semantics ---------------------------------------------------------------

    [Fact]
    public void Kind_filtering_runs_before_the_uniqueness_check()
    {
        ElfImage image = ElfImage.Load(Image(b =>
        {
            b.Symbols.Add(new ElfBuilder.Sym("dup", 0x2400_1000, 4, ElfBuilder.TypeFunc, ElfBuilder.BindGlobal));
            b.Symbols.Add(new ElfBuilder.Sym("dup", 0x2400_0000, 8, ElfBuilder.TypeObject, ElfBuilder.BindLocal));
            b.Symbols.Add(new ElfBuilder.Sym("single", 0x2400_2000, 12, ElfBuilder.TypeObject, ElfBuilder.BindGlobal));
        }));

        SymbolLookup objectOnly = image.Lookup("dup", ElfSymbolKind.Object);
        Assert.Equal(SymbolLookupStatus.Found, objectOnly.Status);
        objectOnly.TryGetSymbol(out ElfSymbol symbol);
        Assert.Equal(0x2400_0000ul, symbol.Address);

        SymbolLookup unfiltered = image.Lookup("dup");
        Assert.Equal(SymbolLookupStatus.Ambiguous, unfiltered.Status);
        Assert.Equal([0x2400_0000ul, 0x2400_1000ul], unfiltered.Candidates.Select(c => c.Address));

        Assert.Equal(SymbolLookupStatus.NotFound, image.Lookup("absent").Status);
        Assert.Equal(SymbolLookupStatus.NotFound, image.Lookup("").Status);
        Assert.Equal(SymbolLookupStatus.NotFound, ElfImage.Load([1, 2, 3]).Lookup("x").Status);
    }

    [Fact]
    public void Ambiguous_candidates_survive_a_wide_gap_in_address_order()
    {
        // 40 same-name symbols: the first candidate count exceeds any fixed drain buffer,
        // so this proves the facade reads the actual count back and captures all of them.
        ElfImage image = ElfImage.Load(Image(b =>
        {
            for (uint i = 0; i < 40; i++)
                b.Symbols.Add(new ElfBuilder.Sym("many", 0x2400_0000 + i * 0x10, 4, ElfBuilder.TypeObject, ElfBuilder.BindLocal));
        }));

        SymbolLookup lookup = image.Lookup("many");
        Assert.Equal(SymbolLookupStatus.Ambiguous, lookup.Status);
        Assert.Equal(40, lookup.Candidates.Count);
        Assert.Equal(0x2400_0000ul, lookup.Candidates.First().Address);
        Assert.Equal(0x2400_0270ul, lookup.Candidates.Last().Address);
    }

    // ---- sections -----------------------------------------------------------------------

    [Fact]
    public void Sections_keep_file_order_and_slice_progbits_contents()
    {
        byte[] body = [0xDE, 0xAD, 0xBE, 0xEF];
        ElfImage image = ElfImage.Load(Image(b =>
        {
            b.DataSections.Add(new ElfBuilder.DataSection(".text", 0x0800_0000, body));
            b.NoBitsSections.Add(new ElfBuilder.NoBitsSection(".bss", 0x2000_0000, 64));
        }));

        ElfSection text = image.Sections[0];
        Assert.True(text.HasContents);
        Assert.Equal(0x0800_0000ul, text.Address);
        Assert.Equal(body.Length, (int)text.Size);
        Assert.True(image.TryGetSectionBytes(".text", out byte[] contents));
        Assert.Equal(body, contents);

        ElfSection bss = image.Sections[1];
        Assert.False(bss.HasContents);
        Assert.False(image.TryGetSectionBytes(".bss", out byte[] none));
        Assert.Empty(none);
        Assert.False(image.TryGetSectionBytes(".absent", out _));
    }

    [Fact]
    public void Duplicate_section_names_resolve_first_match_wins()
    {
        ElfImage image = ElfImage.Load(Image(b =>
        {
            b.DataSections.Add(new ElfBuilder.DataSection(".dup", 0x0800_0000, [1]));
            b.DataSections.Add(new ElfBuilder.DataSection(".dup", 0x0800_1000, [2]));
        }));

        Assert.True(image.TryGetSection(".dup", out ElfSection first));
        Assert.Equal(0x0800_0000ul, first.Address);
    }

    // ---- the control-block locate ladder --------------------------------------------------

    [Fact]
    public void The_locate_rungs_match_the_documented_reasons()
    {
        RttElfLocateResult resolved = RttControlBlock.LocateFromElf(ElfImage.Load(Image(b =>
            b.Symbols.Add(new ElfBuilder.Sym(CbName, 0x2400_0070, 168, ElfBuilder.TypeObject, ElfBuilder.BindGlobal)))));
        Assert.Equal(RttElfLocateStatus.Resolved, resolved.Status);
        Assert.Equal(0x2400_0070u, resolved.Address);
        Assert.Equal(
            "resolved _SEGGER_RTT at 0x24000070 (size 168); verify the \"SEGGER RTT\" ID on target before trusting it",
            resolved.Reason);

        RttElfLocateResult missing = RttControlBlock.LocateFromElf(ElfImage.Load(Image()));
        Assert.Equal(RttElfLocateStatus.SymbolMissing, missing.Status);
        Assert.Equal(
            "_SEGGER_RTT not in the symbol table (firmware built without RTT, or the image is stripped); fall back to the SDK RAM scan",
            missing.Reason);

        RttElfLocateResult implausible = RttControlBlock.LocateFromElf(ElfImage.Load(Image(b =>
            b.Symbols.Add(new ElfBuilder.Sym(CbName, 0x2400_0000, 999, ElfBuilder.TypeObject, ElfBuilder.BindGlobal)))));
        Assert.Equal(RttElfLocateStatus.ImplausibleSize, implausible.Status);
        Assert.Contains("implausible _SEGGER_RTT size 999", implausible.Reason);

        RttElfLocateResult ambiguous = RttControlBlock.LocateFromElf(ElfImage.Load(Image(b =>
        {
            b.Symbols.Add(new ElfBuilder.Sym(CbName, 0x2400_0000, 168, ElfBuilder.TypeObject, ElfBuilder.BindLocal));
            b.Symbols.Add(new ElfBuilder.Sym(CbName, 0x2400_1000, 168, ElfBuilder.TypeObject, ElfBuilder.BindGlobal));
        })));
        Assert.Equal(RttElfLocateStatus.Ambiguous, ambiguous.Status);
        Assert.Equal(
            "ambiguous _SEGGER_RTT: 2 OBJECT candidates at 0x24000000, 0x24001000",
            ambiguous.Reason);
    }

    [Fact]
    public void Invalid_image_guards_stay_managed_side()
    {
        RttElfLocateResult none = RttControlBlock.LocateFromElf(null);
        Assert.Equal(RttElfLocateStatus.InvalidImage, none.Status);
        Assert.Equal("no firmware image to resolve _SEGGER_RTT from", none.Reason);

        RttElfLocateResult broken = RttControlBlock.LocateFromElf(ElfImage.Load([1, 2, 3]));
        Assert.Equal(RttElfLocateStatus.InvalidImage, broken.Status);
        Assert.Equal($"cannot resolve {CbName}: not an ELF image (bad \\x7fELF magic)", broken.Reason);
    }
}
