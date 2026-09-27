using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

/// <summary>SettingsFile's strict schema, merge, path anchoring and validation.</summary>
public class SettingsFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"rtt-settings-{Guid.NewGuid():N}");

    private string Write(string content)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Creates a file inside the settings dir (configured paths must exist).</summary>
    private string TempFile(string relative)
    {
        string path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    // ---- Parse: key mapping and strictness ----

    [Fact]
    public void Both_keys_map_to_their_members()
    {
        SettingsValues v = SettingsFile.Parse(
            """{ "dll": "C:/SEGGER/JLink_x64.dll", "fromelf": "C:/Keil/bin/fromelf.exe" }""", "t.json");
        Assert.Equal("C:/SEGGER/JLink_x64.dll", v.DllPath);
        Assert.Equal("C:/Keil/bin/fromelf.exe", v.FromElfPath);
    }

    [Fact]
    public void Empty_object_sets_nothing()
    {
        SettingsValues v = SettingsFile.Parse("{}", "t.json");
        Assert.Null(v.DllPath);
        Assert.Null(v.FromElfPath);
    }

    [Fact]
    public void Null_values_count_as_unset()
    {
        SettingsValues v = SettingsFile.Parse("""{ "dll": null, "fromelf": null }""", "t.json");
        Assert.Null(v.DllPath);
        Assert.Null(v.FromElfPath);
    }

    [Fact]
    public void Blank_path_values_count_as_unset()
    {
        string path = Write("""{ "dll": "", "fromelf": " " }""");
        var options = new CommandLineOptions();
        SettingsFile.ApplyTo(options, path);
        Assert.Equal("", options.DllPath);
        Assert.Null(options.FromElfPath);
    }

    [Fact]
    public void Unknown_key_is_named_with_its_source()
    {
        var ex = Assert.Throws<UsageException>(() => SettingsFile.Parse("""{ "chip": "X" }""", "my.json"));
        Assert.Contains("settings: unknown key 'chip' (my.json)", ex.Message);
    }

    [Fact]
    public void Path_keys_reject_non_string_values()
    {
        var ex = Assert.Throws<UsageException>(() => SettingsFile.Parse("""{ "fromelf": 5 }""", "t.json"));
        Assert.Contains("settings: 'fromelf': expected a string", ex.Message);
    }

    [Fact]
    public void Broken_json_names_the_source()
    {
        var ex = Assert.Throws<UsageException>(() => SettingsFile.Parse("{dll", "cfg.json"));
        Assert.StartsWith("settings: cannot parse 'cfg.json'", ex.Message);
    }

    [Fact]
    public void Non_object_root_is_a_parse_error()
    {
        var ex = Assert.Throws<UsageException>(() => SettingsFile.Parse("[1]", "t.json"));
        Assert.StartsWith("settings: cannot parse 't.json'", ex.Message);
    }

    // ---- ApplyTo(options, values): fill-null merge, CLI wins ----

    [Fact]
    public void Empty_options_take_both_values()
    {
        var options = new CommandLineOptions();
        SettingsFile.ApplyTo(options, new SettingsValues { DllPath = "a.dll", FromElfPath = "fe.exe" });
        Assert.Equal("a.dll", options.DllPath);
        Assert.Equal("fe.exe", options.FromElfPath);
    }

    [Fact]
    public void Cli_set_values_are_kept()
    {
        var options = new CommandLineOptions { DllPath = "FROM_CLI.dll", FromElfPath = "FROM_CLI.exe" };
        SettingsFile.ApplyTo(options, new SettingsValues { DllPath = "a.dll", FromElfPath = "fe.exe" });
        Assert.Equal("FROM_CLI.dll", options.DllPath);
        Assert.Equal("FROM_CLI.exe", options.FromElfPath);
    }

    [Fact]
    public void Empty_dll_counts_as_unset_and_fills()
    {
        var options = new CommandLineOptions { DllPath = "" };
        SettingsFile.ApplyTo(options, new SettingsValues { DllPath = "a.dll" });
        Assert.Equal("a.dll", options.DllPath);
    }

    [Fact]
    public void Partial_values_fill_only_their_members()
    {
        var options = new CommandLineOptions();
        SettingsFile.ApplyTo(options, new SettingsValues { FromElfPath = "fe.exe" });
        Assert.Equal("", options.DllPath);
        Assert.Equal("fe.exe", options.FromElfPath);
    }

    // ---- ApplyTo(options, path): file resolution ----

    [Fact]
    public void Missing_file_is_a_silent_no_op()
    {
        string path = Path.Combine(_dir, "settings.json");
        var options = new CommandLineOptions();
        SettingsFile.ApplyTo(options, path);
        Assert.Equal("", options.DllPath);
        Assert.Null(options.FromElfPath);
    }

    [Fact]
    public void File_values_are_loaded_and_merged()
    {
        TempFile("fe.exe");
        string path = Write("""{ "fromelf": "fe.exe" }""");
        var options = new CommandLineOptions();
        SettingsFile.ApplyTo(options, path);
        Assert.Equal(Path.Combine(_dir, "fe.exe"), options.FromElfPath);
    }

    [Fact]
    public void Relative_values_anchor_to_the_settings_directory()
    {
        TempFile("tools/JLink_x64.dll");
        string path = Write("""{ "dll": "tools/JLink_x64.dll" }""");
        var options = new CommandLineOptions();
        SettingsFile.ApplyTo(options, path);
        Assert.Equal(Path.Combine(_dir, "tools", "JLink_x64.dll"), options.DllPath);
    }

    [Fact]
    public void A_configured_dll_that_does_not_exist_is_a_hard_error()
    {
        string path = Write("""{ "dll": "C:/no/such/JLink_x64.dll" }""");
        var ex = Assert.Throws<UsageException>(() => SettingsFile.ApplyTo(new CommandLineOptions(), path));
        Assert.Contains("'dll' points to a missing file", ex.Message);
        Assert.Contains("C:/no/such/JLink_x64.dll", ex.Message);
    }

    [Fact]
    public void A_configured_fromelf_that_does_not_exist_is_a_hard_error()
    {
        string path = Write("""{ "fromelf": "C:/no/such/fromelf.exe" }""");
        var ex = Assert.Throws<UsageException>(() => SettingsFile.ApplyTo(new CommandLineOptions(), path));
        Assert.Contains("'fromelf' points to a missing file", ex.Message);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
