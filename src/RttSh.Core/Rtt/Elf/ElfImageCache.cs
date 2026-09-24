using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RttSh.Core.Rtt.Elf;

/// <summary>Disk cache in front of ElfImage.FromFile: a firmware parses once, and every later
/// run over an unchanged image rehydrates symbols/sections from a small JSON entry instead of
/// repeating the ELFSharp parse. The CLI points this at &lt;cwd&gt;/.rttsh/elf-cache, one entry
/// file per image (&lt;stem&gt;-&lt;path-hash&gt;.json, overwritten in place, so nothing
/// accumulates).
///
/// Correctness ladder: the size+mtime key is only the cheap gate that keeps a fresh rebuild
/// from even opening an entry; a gate match is still verified against the entry's stored
/// SHA256 of the image bytes, so a same-stamp content change (cp -p, restores) can never serve
/// stale symbols - its worst case is one wasted hash. The image bytes themselves are never
/// cached: they ride along from the live file on both paths, so TryGetSectionBytes and the
/// FromFile exception semantics are unchanged. Every cache problem degrades to a plain parse -
/// missing/corrupt/foreign entries are misses, an unwritable directory silently skips the
/// write - so the cache can only change speed, never behavior.
///
/// Trust model: an entry carries the same authority as .rttsh/config.json, because anyone
/// able to write one can already rewrite the other. The content hash therefore guards against
/// accidents (same-stamp swaps, torn writes), not adversaries.</summary>
public static class ElfImageCache
{
    /// <summary>Bumped whenever the entry layout changes incompatibly; older entries fail the
    /// schema check and are rewritten.</summary>
    public const string Schema = "rttsh-elf-cache-1";

    /// <summary>Loads an image through the cache. cacheDir null = plain FromFile. The IO
    /// exceptions FromFile raises for a missing/unreadable image propagate identically here:
    /// the live file is read on both paths, before any cache is consulted.</summary>
    public static ElfImage Load(string elfPath, string? cacheDir)
    {
        if (cacheDir is null)
            return ElfImage.FromFile(elfPath);

        // Stat before reading, and refuse the write when the stat moved under us: a rebuild
        // landing mid-load must not file the OLD parse under the NEW timestamp.
        long mtimeTicks = File.GetLastWriteTimeUtc(elfPath).Ticks;
        byte[] bytes = File.ReadAllBytes(elfPath);

        string hash = ContentHash(bytes);
        var key = new ElfCacheKey(bytes.Length, mtimeTicks);
        string cachePath = CachePath(cacheDir, elfPath);

        ElfCacheEntry? entry = TryRead(cachePath, key);
        if (entry is not null && entry.Sha256 == hash)
            return ElfImage.FromMaterialized(bytes,
                [.. entry.Symbols.Select(MapSymbol)],
                [.. entry.Sections.Select(MapSection)],
                entry.LittleEndian);

        ElfImage image = ElfImage.Load(bytes);
        if (image.Status == ElfLoadStatus.Ok && File.GetLastWriteTimeUtc(elfPath).Ticks == mtimeTicks)
            TryWrite(cachePath, BuildEntry(image, hash, key));
        return image;
    }

    private static ElfCacheEntry BuildEntry(ElfImage image, string hash, ElfCacheKey key) => new(
        Schema,
        key,
        image.IsLittleEndian,
        hash,
        [.. image.Symbols.Select(s => new ElfCacheSymbol(s.Name, s.Address, s.Size, s.Kind, s.Binding))],
        [.. image.Sections.Select(s => new ElfCacheSection(s.Name, s.Address, s.Size, s.FileOffset, s.HasContents))]);

    private static ElfSymbol MapSymbol(ElfCacheSymbol s) => new(s.Name, s.Address, s.Size, s.Kind, s.Binding);

    private static ElfSection MapSection(ElfCacheSection s) => new(s.Name, s.Address, s.Size, s.FileOffset, s.HasContents);

    /// <summary>One entry file per image: the stem stays human-readable, the full-path hash
    /// keeps two same-named images in different directories from sharing an entry.</summary>
    private static string CachePath(string cacheDir, string elfPath)
    {
        string stem = Path.GetFileNameWithoutExtension(elfPath);
        string pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(elfPath))))[..8]
            .ToLowerInvariant();
        return Path.Combine(cacheDir, $"{stem}-{pathHash}.json");
    }

    /// <summary>Any read problem - absent, torn, hand-edited, wrong schema, wrong key, null
    /// fields - is a miss, never an error; the miss path re-parses and rewrites the entry.
    /// The field-level checks matter: a name that deserialized as null would otherwise reach
    /// ElfImage's lookup dictionaries and die as an unhandled ArgumentNullException.</summary>
    private static ElfCacheEntry? TryRead(string cachePath, ElfCacheKey key)
    {
        try
        {
            if (!File.Exists(cachePath))
                return null;
            ElfCacheEntry? entry = JsonSerializer.Deserialize<ElfCacheEntry>(File.ReadAllBytes(cachePath), JsonOptions);
            if (entry is null || entry.Schema != Schema || entry.Key != key
                || entry.Symbols is null || entry.Sections is null
                || entry.Symbols.Any(s => s?.Name is null)
                || entry.Sections.Any(s => s?.Name is null))
                return null;
            return entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Best effort: a uniquely-named temp file plus an atomic-ish move, so readers
    /// (and concurrent writers - no shared temp name to collide on) never see a torn entry,
    /// only the previous or the new one.</summary>
    private static void TryWrite(string cachePath, ElfCacheEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            string temp = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions));
            File.Move(temp, cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // unwritable cache location: the parse already succeeded, so just run uncached
        }
    }

    private static string ContentHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };   // entry files are human-readable

    internal sealed record ElfCacheKey(
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("mtimeUtcTicks")] long MtimeUtcTicks);

    /// <summary>Kinds/bindings travel as strings (the enum order must never become a file
    /// format), names and numbers mirror the domain records one-to-one.</summary>
    internal sealed record ElfCacheSymbol(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("address")] ulong Address,
        [property: JsonPropertyName("size")] ulong Size,
        [property: JsonPropertyName("kind"), JsonConverter(typeof(JsonStringEnumConverter<ElfSymbolKind>))] ElfSymbolKind Kind,
        [property: JsonPropertyName("binding"), JsonConverter(typeof(JsonStringEnumConverter<ElfSymbolBinding>))] ElfSymbolBinding Binding);

    internal sealed record ElfCacheSection(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("address")] ulong Address,
        [property: JsonPropertyName("size")] ulong Size,
        [property: JsonPropertyName("fileOffset")] long FileOffset,
        [property: JsonPropertyName("hasContents")] bool HasContents);

    internal sealed record ElfCacheEntry(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("key")] ElfCacheKey Key,
        [property: JsonPropertyName("littleEndian")] bool LittleEndian,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("symbols")] IReadOnlyList<ElfCacheSymbol> Symbols,
        [property: JsonPropertyName("sections")] IReadOnlyList<ElfCacheSection> Sections);
}
