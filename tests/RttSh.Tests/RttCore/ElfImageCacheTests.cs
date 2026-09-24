using System.Text.Json.Nodes;
using RttSh.Core.Rtt.Elf;

namespace Toolbox.Tests.RttCore;

/// <summary>The .rttsh/elf-cache mechanics, over real temp files. The load-with-tampered-entry
/// tests are the heart of the suite: swapping an entry's symbols for one no parse could ever
/// produce (_CACHE_PROBE) proves a load was served from the cache, not re-derived from the
/// file - and the same-stamp rewrite test proves the stored content hash still catches a
/// cp -p-style swap the mtime gate cannot see.</summary>
public class ElfImageCacheTests
{
    private const string CbName = "_SEGGER_RTT";
    private const uint CbAddress = 0x2400_0070;

    private static byte[] ImageWithCb(string name = CbName, uint address = CbAddress) => new ElfBuilder
    {
        Symbols = { new ElfBuilder.Sym(name, address, 168, ElfBuilder.TypeObject, ElfBuilder.BindGlobal) },
    }.Build();

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rttsh-cache-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (string ElfPath, string CacheDir) Warm()
    {
        string cacheDir = TempDir();
        string elfPath = Path.Combine(cacheDir, "fw.elf");
        File.WriteAllBytes(elfPath, ImageWithCb());
        ElfImage first = ElfImageCache.Load(elfPath, cacheDir);
        Assert.Equal(ElfLoadStatus.Ok, first.Status);
        return (elfPath, cacheDir);
    }

    private static string EntryPath(string cacheDir)
    {
        string[] entries = Directory.GetFiles(cacheDir, "*.json");
        Assert.Single(entries);
        return entries[0];
    }

    /// <summary>Swaps an entry's symbols for one no real parse could produce - the marker that
    /// a later load came from the cache rather than the file. Sections stay untouched so the
    /// round-trip test can still exercise them on the warm path.</summary>
    private static void PlantProbe(string entryPath) =>
        SetSymbols(entryPath, SymbolNode("_CACHE_PROBE", 0xDEAD_BEEF));

    private static JsonObject SymbolNode(string? name, ulong address, string kind = "Object", string binding = "Global") => new()
    {
        ["name"] = name,
        ["address"] = address,
        ["size"] = 4,
        ["kind"] = kind,
        ["binding"] = binding,
    };

    private static void SetSymbols(string entryPath, params JsonNode?[] symbols)
    {
        var entry = JsonNode.Parse(File.ReadAllText(entryPath))!;
        var array = new JsonArray();
        foreach (JsonNode? symbol in symbols)
            array.Add(symbol);
        entry["symbols"] = array;
        File.WriteAllText(entryPath, entry.ToJsonString());
    }

    // ---- cold path ----------------------------------------------------------------------

    [Fact]
    public void A_cold_load_parses_and_writes_one_entry()
    {
        string elfDir = TempDir(), cacheDir = TempDir();
        string elfPath = Path.Combine(elfDir, "fw.elf");
        File.WriteAllBytes(elfPath, ImageWithCb());

        ElfImage image = ElfImageCache.Load(elfPath, cacheDir);

        Assert.Equal(ElfLoadStatus.Ok, image.Status);
        Assert.True(image.Lookup(CbName, ElfSymbolKind.Object).TryGetSymbol(out ElfSymbol symbol));
        Assert.Equal(CbAddress, symbol.Address);

        string[] entries = Directory.GetFiles(cacheDir, "*.json");
        Assert.Single(entries);
        string text = File.ReadAllText(entries[0]);
        Assert.Contains(ElfImageCache.Schema, text);
        Assert.Contains(CbName, text);
        Assert.Empty(Directory.GetFiles(cacheDir, "*.tmp"));   // the atomic move leaves no debris
    }

    [Fact]
    public void A_null_cache_dir_is_a_plain_parse()
    {
        string elfDir = TempDir(), untouched = TempDir();
        string elfPath = Path.Combine(elfDir, "fw.elf");
        File.WriteAllBytes(elfPath, ImageWithCb());

        ElfImage image = ElfImageCache.Load(elfPath, null);

        Assert.Equal(ElfLoadStatus.Ok, image.Status);
        Assert.Empty(Directory.GetFiles(untouched, "*", SearchOption.AllDirectories));
    }

    // ---- warm path ----------------------------------------------------------------------

    [Fact]
    public void A_warm_load_is_served_from_the_entry_not_a_parse()
    {
        (string elfPath, string cacheDir) = Warm();
        PlantProbe(EntryPath(cacheDir));

        ElfImage image = ElfImageCache.Load(elfPath, cacheDir);

        Assert.True(image.Lookup("_CACHE_PROBE", ElfSymbolKind.Object).TryGetSymbol(out ElfSymbol symbol));
        Assert.Equal(0xDEAD_BEEFu, symbol.Address);
        Assert.Equal(SymbolLookupStatus.NotFound, image.Lookup(CbName).Status);
    }

    // ---- invalidation --------------------------------------------------------------------

    [Fact]
    public void A_rebuilt_image_with_a_new_timestamp_reparses()
    {
        (string elfPath, string cacheDir) = Warm();
        File.WriteAllBytes(elfPath, ImageWithCb(address: 0x2000_0100));
        File.SetLastWriteTimeUtc(elfPath, File.GetLastWriteTimeUtc(elfPath).AddHours(1));

        ElfImage image = ElfImageCache.Load(elfPath, cacheDir);

        Assert.True(image.Lookup(CbName, ElfSymbolKind.Object).TryGetSymbol(out ElfSymbol symbol));
        Assert.Equal(0x2000_0100u, symbol.Address);
    }

    [Fact]
    public void A_same_stamp_same_size_rewrite_is_caught_by_the_hash()
    {
        (string elfPath, string cacheDir) = Warm();
        byte[] swapped = ImageWithCb("_SEGGER_RTX", 0x2000_0200);
        Assert.Equal(File.ReadAllBytes(elfPath).Length, swapped.Length);   // the premise: the gate alone cannot see this
        DateTime warmMtime = File.GetLastWriteTimeUtc(elfPath);
        File.WriteAllBytes(elfPath, swapped);
        File.SetLastWriteTimeUtc(elfPath, warmMtime);

        ElfImage image = ElfImageCache.Load(elfPath, cacheDir);

        Assert.True(image.Lookup("_SEGGER_RTX", ElfSymbolKind.Object).TryGetSymbol(out ElfSymbol symbol));
        Assert.Equal(0x2000_0200u, symbol.Address);
        Assert.Equal(SymbolLookupStatus.NotFound, image.Lookup(CbName).Status);
    }

    [Fact]
    public void A_corrupt_entry_degrades_to_a_parse_and_is_rewritten()
    {
        (string elfPath, string cacheDir) = Warm();
        File.WriteAllText(EntryPath(cacheDir), "torn write {{{");

        ElfImage image = ElfImageCache.Load(elfPath, cacheDir);

        Assert.Equal(ElfLoadStatus.Ok, image.Status);
        Assert.Equal(SymbolLookupStatus.Found, image.Lookup(CbName).Status);
        Assert.Contains(CbName, File.ReadAllText(EntryPath(cacheDir)));
    }

    [Fact]
    public void An_unusable_cache_directory_still_parses()
    {
        string blockerDir = TempDir(), elfDir = TempDir();
        string notADir = Path.Combine(blockerDir, "blocker");
        File.WriteAllBytes(notADir, [1]);   // Directory.CreateDirectory under this must fail
        string elfPath = Path.Combine(elfDir, "fw.elf");
        File.WriteAllBytes(elfPath, ImageWithCb());

        ElfImage image = ElfImageCache.Load(elfPath, notADir);

        Assert.Equal(ElfLoadStatus.Ok, image.Status);
        Assert.Equal(SymbolLookupStatus.Found, image.Lookup(CbName).Status);
    }

    // ---- what the entry must carry --------------------------------------------------------

    [Fact]
    public void Sections_and_endianness_survive_the_entry()
    {
        byte[] data = [0x12, 0x34, 0x56, 0x78];
        string cacheDir = TempDir();
        string elfPath = Path.Combine(cacheDir, "fw.elf");
        File.WriteAllBytes(elfPath, new ElfBuilder
        {
            BigEndian = true,
            DataSections = { new ElfBuilder.DataSection(".data", 0x2000_0000, data) },
            NoBitsSections = { new ElfBuilder.NoBitsSection(".bss", 0x2000_1000, 0x400) },
        }.Build());
        ElfImage cold = ElfImageCache.Load(elfPath, cacheDir);
        Assert.False(cold.IsLittleEndian);

        PlantProbe(EntryPath(cacheDir));
        ElfImage warm = ElfImageCache.Load(elfPath, cacheDir);

        Assert.False(warm.IsLittleEndian);
        Assert.True(warm.TryGetSection(".data", out ElfSection dataSection));
        Assert.Equal(0x2000_0000u, dataSection.Address);
        Assert.True(dataSection.HasContents);
        Assert.True(warm.TryGetSectionBytes(".data", out byte[] bytes));
        Assert.Equal(data, bytes);
        Assert.True(warm.TryGetSection(".bss", out ElfSection bssSection));
        Assert.False(bssSection.HasContents);
        Assert.False(warm.TryGetSectionBytes(".bss", out _));
    }

    // ---- poisoned entries: every tamper degrades to a re-parse, never crashes -----------

    [Theory]
    [InlineData("nullSymbolName")]
    [InlineData("nullSymbolElement")]
    [InlineData("missingSymbolsProperty")]
    [InlineData("nullSections")]
    [InlineData("foreignSchema")]
    public void A_poisoned_entry_degrades_to_a_parse_and_is_rewritten(string variant)
    {
        (string elfPath, string cacheDir) = Warm();
        string entryPath = EntryPath(cacheDir);
        var entry = JsonNode.Parse(File.ReadAllText(entryPath))!;
        switch (variant)
        {
            case "nullSymbolName":
                entry["symbols"] = new JsonArray(SymbolNode(null, 1));
                break;
            case "nullSymbolElement":
            {
                var array = new JsonArray();
                array.Add((JsonNode?)null);
                entry["symbols"] = array;
                break;
            }
            case "missingSymbolsProperty":
                ((JsonObject)entry).Remove("symbols");
                break;
            case "nullSections":
                entry["sections"] = null;
                break;
            case "foreignSchema":
                entry["schema"] = "some-other-tool-9";
                break;
        }
        File.WriteAllText(entryPath, entry.ToJsonString());

        ElfImage image = ElfImageCache.Load(elfPath, cacheDir);

        // the load itself must succeed off a fresh parse, and the poison must be gone
        Assert.Equal(ElfLoadStatus.Ok, image.Status);
        Assert.Equal(SymbolLookupStatus.Found, image.Lookup(CbName).Status);
        Assert.DoesNotContain("_CACHE_PROBE", File.ReadAllText(EntryPath(cacheDir)));
    }

    [Fact]
    public void An_ambiguous_symbol_survives_the_cache()
    {
        (string elfPath, string cacheDir) = Warm();
        SetSymbols(EntryPath(cacheDir),
            SymbolNode(CbName, 0x1000_0000),
            SymbolNode(CbName, 0x2000_0000));

        ElfImage image = ElfImageCache.Load(elfPath, cacheDir);

        // the control-block policy depends on ambiguity staying detectable after a hit
        SymbolLookup lookup = image.Lookup(CbName, ElfSymbolKind.Object);
        Assert.Equal(SymbolLookupStatus.Ambiguous, lookup.Status);
        Assert.Equal(2, lookup.Candidates.Count);
        Assert.Equal(0x1000_0000u, lookup.Candidates[0].Address);
        Assert.Equal(0x2000_0000u, lookup.Candidates[1].Address);
    }

    [Fact]
    public void The_kind_filter_survives_the_cache()
    {
        // the GCC shape the RTT path relies on: an OBJECT and a FUNCTION share a name, and
        // a kind-restricted query must not see ambiguity - on the warm path too
        (string elfPath, string cacheDir) = Warm();
        SetSymbols(EntryPath(cacheDir),
            SymbolNode("sh_area", 0x1000, "Object"),
            SymbolNode("sh_area", 0x2000, "Function"));

        ElfImage image = ElfImageCache.Load(elfPath, cacheDir);

        Assert.True(image.Lookup("sh_area", ElfSymbolKind.Object).TryGetSymbol(out ElfSymbol obj));
        Assert.Equal(0x1000u, obj.Address);
        Assert.True(image.Lookup("sh_area", ElfSymbolKind.Function).TryGetSymbol(out ElfSymbol func));
        Assert.Equal(0x2000u, func.Address);
        Assert.Equal(SymbolLookupStatus.Ambiguous, image.Lookup("sh_area").Status);
    }

    // ---- path handling ------------------------------------------------------------------

    [Fact]
    public void Different_path_spellings_resolve_to_the_same_entry()
    {
        (string elfPath, string cacheDir) = Warm();
        PlantProbe(EntryPath(cacheDir));

        // "../fw.elf" normalizes to the exact file whose entry was poisoned: a hit proves
        // the path hash is taken from the normalized full path, not the spelling
        string awkward = Path.Combine(cacheDir, "fw.elf", "..", "fw.elf");
        ElfImage image = ElfImageCache.Load(awkward, cacheDir);

        Assert.Equal(SymbolLookupStatus.Found, image.Lookup("_CACHE_PROBE").Status);
    }

    [Fact]
    public void Same_named_images_in_different_directories_keep_separate_entries()
    {
        string cacheDir = TempDir(), dirA = TempDir(), dirB = TempDir();
        string elfA = Path.Combine(dirA, "fw.elf"), elfB = Path.Combine(dirB, "fw.elf");
        File.WriteAllBytes(elfA, ImageWithCb(address: 0x1000_0000));
        File.WriteAllBytes(elfB, ImageWithCb(address: 0x2000_0000));
        ElfImageCache.Load(elfA, cacheDir);
        ElfImageCache.Load(elfB, cacheDir);
        Assert.Equal(2, Directory.GetFiles(cacheDir, "*.json").Length);

        string entryA = Directory.GetFiles(cacheDir, "*.json")
            .Single(f => File.ReadAllText(f).Contains("268435456"));   // 0x10000000, A only
        PlantProbe(entryA);

        Assert.Equal(SymbolLookupStatus.Found, ElfImageCache.Load(elfA, cacheDir).Lookup("_CACHE_PROBE").Status);
        Assert.True(ElfImageCache.Load(elfB, cacheDir).Lookup(CbName, ElfSymbolKind.Object)
            .TryGetSymbol(out ElfSymbol symbol));
        Assert.Equal(0x2000_0000u, symbol.Address);
    }

    [Fact]
    public void A_stem_with_spaces_and_dots_round_trips()
    {
        string cacheDir = TempDir(), elfDir = TempDir();
        string elfPath = Path.Combine(elfDir, "stm32 v1.2.elf");
        File.WriteAllBytes(elfPath, ImageWithCb());
        ElfImageCache.Load(elfPath, cacheDir);

        PlantProbe(EntryPath(cacheDir));
        ElfImage warm = ElfImageCache.Load(elfPath, cacheDir);

        Assert.Equal(SymbolLookupStatus.Found, warm.Lookup("_CACHE_PROBE").Status);
    }

    // ---- environment hostility ------------------------------------------------------------

    [Fact]
    public void A_locked_entry_file_degrades_to_a_parse()
    {
        (string elfPath, string cacheDir) = Warm();
        string entryPath = EntryPath(cacheDir);

        using (File.Open(entryPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // entry unreadable AND unwritable: the parse must carry the load regardless
            ElfImage image = ElfImageCache.Load(elfPath, cacheDir);
            Assert.Equal(ElfLoadStatus.Ok, image.Status);
            Assert.Equal(SymbolLookupStatus.Found, image.Lookup(CbName).Status);
        }
    }

    [Fact]
    public void A_stripped_image_with_no_symbols_round_trips()
    {
        string cacheDir = TempDir();
        string elfPath = Path.Combine(cacheDir, "fw.elf");
        File.WriteAllBytes(elfPath, new ElfBuilder().Build());   // symtab with only the null symbol

        ElfImage cold = ElfImageCache.Load(elfPath, cacheDir);
        ElfImage warm = ElfImageCache.Load(elfPath, cacheDir);

        Assert.Equal(ElfLoadStatus.Ok, cold.Status);
        Assert.Empty(cold.Symbols);
        Assert.Equal(ElfLoadStatus.Ok, warm.Status);
        Assert.Empty(warm.Symbols);
        Assert.Contains("\"symbols\": []", File.ReadAllText(EntryPath(cacheDir)));
    }
}
