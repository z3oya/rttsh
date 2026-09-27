using System.Text.Json;

namespace Toolbox.Tools.RttCli;

/// <summary>Machine-level tool paths loaded from settings.json.</summary>
internal sealed class SettingsValues
{
    public string? DllPath { get; set; }
    public string? FromElfPath { get; set; }
}

/// <summary>~/.rttsh/settings.json: machine-level tool paths ("dll", "fromelf"), shared by
/// every project. Strict schema like the config file; a missing file is a no-op; relative
/// paths anchor to the settings directory; configured paths must exist.</summary>
internal static class SettingsFile
{
    public const string FileName = "settings.json";

    /// <summary>~/.rttsh</summary>
    public static string DirPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ConfigFile.DirName);
    public static string FilePath => Path.Combine(DirPath, FileName);

    /// <summary>Loads the settings file (an explicit path for tests, the default
    /// ~/.rttsh/settings.json otherwise) and merges it; a missing file is a no-op.</summary>
    public static void ApplyTo(CommandLineOptions options, string? settingsPath = null)
    {
        string path = settingsPath ?? FilePath;
        if (!File.Exists(path)) return;

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UsageException($"settings: cannot read '{path}': {ex.Message}");
        }

        SettingsValues values = Parse(json, path);
        string dir = Path.GetDirectoryName(path)!;
        values.DllPath = Anchor(BlankToNull(values.DllPath), dir);
        values.FromElfPath = Anchor(BlankToNull(values.FromElfPath), dir);
        ValidateExists(values.DllPath, "dll", path);
        ValidateExists(values.FromElfPath, "fromelf", path);
        ApplyTo(options, values);
    }

    /// <summary>Fills only what the CLI left unset - command line wins.</summary>
    internal static void ApplyTo(CommandLineOptions options, SettingsValues values)
    {
        if (options.DllPath.Length == 0 && values.DllPath is not null) options.DllPath = values.DllPath;
        options.FromElfPath ??= values.FromElfPath;
    }

    /// <summary>Strict schema - unknown keys are errors.</summary>
    internal static SettingsValues Parse(string json, string sourceName)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new UsageException($"settings: cannot parse '{sourceName}': {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new UsageException($"settings: cannot parse '{sourceName}': expected a JSON object");

            var values = new SettingsValues();
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                    continue;   // JSON null = not set

                switch (property.Name)
                {
                    case "dll": values.DllPath = Text(property); break;
                    case "fromelf": values.FromElfPath = Text(property); break;
                    default:
                        throw new UsageException($"settings: unknown key '{property.Name}' ({sourceName})");
                }
            }
            return values;
        }
    }

    private static string Text(JsonProperty property)
    {
        if (property.Value.ValueKind == JsonValueKind.String)
            return property.Value.GetString()!;
        throw new UsageException($"settings: '{property.Name}': expected a string");
    }

    private static string? Anchor(string? value, string dir) =>
        value is null || Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(dir, value));

    /// <summary>An empty path value counts as unset.</summary>
    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static void ValidateExists(string? value, string key, string source)
    {
        if (value is not null && !File.Exists(value))
            throw new UsageException($"settings: '{key}' points to a missing file: '{value}' ({source})");
    }
}
