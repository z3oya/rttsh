using System.Globalization;
using System.Text.Json;
using Toolbox.Core.Rtt;
using Toolbox.Core.SerialComm;

namespace Toolbox.Tools.RttCli;

/// <summary>One loaded config file: the settable subset of CommandLineOptions as nullable
/// members. Null = not set in the file.</summary>
internal sealed class ConfigValues
{
    public string? Chip { get; set; }
    public int? SpeedKhz { get; set; }
    public RttInterface? Interface { get; set; }
    public uint? RttAddress { get; set; }
    public uint? RttRange { get; set; }
    public int? SerialNo { get; set; }
    public int? Channel { get; set; }
    public string? DllPath { get; set; }
    public TextEncodingKind? Encoding { get; set; }
    public TextEol? Eol { get; set; }
    public string? LogFile { get; set; }
    public int? WaitMs { get; set; }
    public int? ScriptTimeoutMs { get; set; }
}

/// <summary>JSON config file support (-c/--config, default .rttsh.config.json). Independent of
/// the CLI surface: Parse validates JSON into ConfigValues, the merge fills unset
/// CommandLineOptions members (CLI arguments always win), ApplyTo resolves the file path.</summary>
internal static class ConfigFile
{
    public const string DefaultFileName = ".rttsh.config.json";

    /// <summary>Options the CLI knows but a file must not set: payload semantics (hex),
    /// command-specific wording (filter), session preferences (tui), the reset policy, and
    /// self-reference (config).</summary>
    private static readonly HashSet<string> ExcludedKeys = ["hex", "tui", "reset", "filter", "config"];

    private static readonly Dictionary<string, RttInterface> InterfaceWords = new() { ["swd"] = RttInterface.Swd, ["jtag"] = RttInterface.Jtag };
    private static readonly Dictionary<string, TextEncodingKind> EncodingWords = new() { ["utf8"] = TextEncodingKind.Utf8, ["ascii"] = TextEncodingKind.Ascii, ["latin1"] = TextEncodingKind.Latin1 };
    private static readonly Dictionary<string, TextEol> EolWords = new() { ["lf"] = TextEol.Lf, ["cr"] = TextEol.Cr, ["crlf"] = TextEol.CrLf, ["none"] = TextEol.None };

    /// <summary>Layer 3: resolves the file - an explicit path must exist, otherwise the default
    /// file in <paramref name="baseDir"/> applies when present (no file, no config) - then
    /// parses and merges it.</summary>
    public static void ApplyTo(CommandLineOptions options, string? configPath, string? baseDir = null)
    {
        string path;
        if (!string.IsNullOrEmpty(configPath))
        {
            path = configPath;
            if (!File.Exists(path))
                throw new UsageException($"--config: file not found: {path}");
        }
        else
        {
            path = Path.Combine(baseDir ?? Environment.CurrentDirectory, DefaultFileName);
            if (!File.Exists(path)) return;   // no default file: run on CLI values alone
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // exists-but-unreadable (locked, ACL): same surface as every other config error,
            // never an unhandled exception through Main
            throw new UsageException($"--config: cannot read '{path}': {ex.Message}");
        }
        ApplyTo(options, Parse(json, path));
    }

    /// <summary>Layer 2: fills only what the CLI left unset - command-line arguments beat the
    /// file. Chip/DllPath default to "" rather than null, so "" counts as unset for them.</summary>
    internal static void ApplyTo(CommandLineOptions options, ConfigValues values)
    {
        if (options.Chip.Length == 0 && values.Chip is not null) options.Chip = values.Chip;
        options.SpeedKhz ??= values.SpeedKhz;
        options.Interface ??= values.Interface;
        options.RttAddress ??= values.RttAddress;
        options.RttRange ??= values.RttRange;
        options.SerialNo ??= values.SerialNo;
        options.Channel ??= values.Channel;
        if (options.DllPath.Length == 0 && values.DllPath is not null) options.DllPath = values.DllPath;
        options.Encoding ??= values.Encoding;
        options.Eol ??= values.Eol;
        options.LogFile ??= values.LogFile;
        options.WaitMs ??= values.WaitMs;
        options.ScriptTimeoutMs ??= values.ScriptTimeoutMs;
    }

    /// <summary>Layer 1: strict schema - unknown and excluded keys are errors, so a typo'd key
    /// never silently does nothing.</summary>
    internal static ConfigValues Parse(string json, string sourceName)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new UsageException($"--config: cannot parse '{sourceName}': {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new UsageException($"--config: cannot parse '{sourceName}': expected a JSON object");

            var values = new ConfigValues();
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                string key = property.Name;
                if (ExcludedKeys.Contains(key))
                    throw new UsageException($"--config: '{key}' is not a config key");
                if (property.Value.ValueKind == JsonValueKind.Null)
                    continue;   // JSON null = not set

                switch (key)
                {
                    case "chip": values.Chip = Text(property); break;
                    case "speed": values.SpeedKhz = Integer(property); break;
                    case "interface": values.Interface = Word(property, InterfaceWords, "swd or jtag"); break;
                    case "rttAddr": values.RttAddress = Hex(property); break;
                    case "rttRange": values.RttRange = Hex(property); break;
                    case "sn": values.SerialNo = Integer(property); break;
                    case "channel": values.Channel = Integer(property); break;
                    case "dll": values.DllPath = Text(property); break;
                    case "encoding": values.Encoding = Word(property, EncodingWords, "utf8, ascii or latin1"); break;
                    case "eol": values.Eol = Word(property, EolWords, "lf, cr, crlf or none"); break;
                    case "log": values.LogFile = Text(property); break;
                    case "wait": values.WaitMs = Integer(property); break;
                    case "scriptTimeout": values.ScriptTimeoutMs = Integer(property); break;
                    default:
                        throw new UsageException($"--config: unknown key '{key}' ({sourceName})");
                }
            }
            return values;
        }
    }

    private static string Text(JsonProperty property)
    {
        if (property.Value.ValueKind == JsonValueKind.String)
            return property.Value.GetString()!;
        throw Type(property.Name, "a string");
    }

    private static int Integer(JsonProperty property)
    {
        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out int value))
            return value;
        throw Type(property.Name, "an integer");
    }

    /// <summary>Addresses keep the CLI's --rtt-addr shape: a "0x…" string, never a bare number
    /// (a decimal address is easy to get wrong when copy-pasting from other tools).</summary>
    private static uint Hex(JsonProperty property)
    {
        if (property.Value.ValueKind == JsonValueKind.String)
        {
            string text = property.Value.GetString()!;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
                return value;
        }
        throw Type(property.Name, "a \"0x…\" hex string");
    }

    private static T Word<T>(JsonProperty property, IReadOnlyDictionary<string, T> words, string expected)
    {
        if (property.Value.ValueKind == JsonValueKind.String)
        {
            string word = property.Value.GetString()!;
            if (words.TryGetValue(word, out T value))
                return value;
            throw new UsageException($"--config: '{property.Name}': expected {expected}, got '{word}'");
        }
        throw new UsageException($"--config: '{property.Name}': expected {expected}");
    }

    private static UsageException Type(string key, string what) => new($"--config: '{key}': expected {what}");
}
