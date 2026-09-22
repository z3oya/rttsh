using Toolbox.Core.Rtt;
using Toolbox.Core.SerialComm;
using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

public class ConfigFileTests
{
    // ---- Parse: key mapping ----

    [Fact]
    public void All_keys_map_to_their_members()
    {
        ConfigValues v = ConfigFile.Parse("""
            {
              "chip": "STM32F103C8",
              "speed": 4000,
              "interface": "swd",
              "rttAddr": "0x20000000",
              "rttRange": "0x1000",
              "sn": 12345,
              "channel": 2,
              "dll": "C:/segger/JLink_x64.dll",
              "encoding": "latin1",
              "eol": "crlf",
              "log": "rtt.log",
              "wait": 500,
              "scriptTimeout": 0
            }
            """, "test.json");
        Assert.Equal("STM32F103C8", v.Chip);
        Assert.Equal(4000, v.SpeedKhz);
        Assert.Equal(RttInterface.Swd, v.Interface);
        Assert.Equal(0x20000000u, v.RttAddress);
        Assert.Equal(0x1000u, v.RttRange);
        Assert.Equal(12345, v.SerialNo);
        Assert.Equal(2, v.Channel);
        Assert.Equal("C:/segger/JLink_x64.dll", v.DllPath);
        Assert.Equal(TextEncodingKind.Latin1, v.Encoding);
        Assert.Equal(TextEol.CrLf, v.Eol);
        Assert.Equal("rtt.log", v.LogFile);
        Assert.Equal(500, v.WaitMs);
        Assert.Equal(0, v.ScriptTimeoutMs);
    }

    [Fact]
    public void Empty_object_sets_nothing()
    {
        ConfigValues v = ConfigFile.Parse("{}", "t.json");
        Assert.Null(v.Chip);
        Assert.Null(v.SpeedKhz);
        Assert.Null(v.Interface);
        Assert.Null(v.RttAddress);
        Assert.Null(v.RttRange);
        Assert.Null(v.SerialNo);
        Assert.Null(v.Channel);
        Assert.Null(v.DllPath);
        Assert.Null(v.Encoding);
        Assert.Null(v.Eol);
        Assert.Null(v.LogFile);
        Assert.Null(v.WaitMs);
        Assert.Null(v.ScriptTimeoutMs);
    }

    [Fact]
    public void Null_values_count_as_unset()
    {
        ConfigValues v = ConfigFile.Parse("""{ "chip": null, "channel": null }""", "t.json");
        Assert.Null(v.Chip);
        Assert.Null(v.Channel);
    }

    // ---- Parse: enum words (tables match the CLI exactly) ----

    [Theory]
    [InlineData("swd", RttInterface.Swd)]
    [InlineData("jtag", RttInterface.Jtag)]
    public void Interface_words_parse(string word, RttInterface expected) =>
        Assert.Equal(expected, ConfigFile.Parse($$"""{ "interface": "{{word}}" }""", "t.json").Interface);

    [Fact]
    public void Bad_interface_word_names_the_expectation()
    {
        var ex = Assert.Throws<UsageException>(() => ConfigFile.Parse("""{ "interface": "usb" }""", "t.json"));
        Assert.Contains("--config: 'interface': expected swd or jtag, got 'usb'", ex.Message);
    }

    [Theory]
    [InlineData("utf8", TextEncodingKind.Utf8)]
    [InlineData("ascii", TextEncodingKind.Ascii)]
    [InlineData("latin1", TextEncodingKind.Latin1)]
    public void Encoding_words_parse(string word, TextEncodingKind expected) =>
        Assert.Equal(expected, ConfigFile.Parse($$"""{ "encoding": "{{word}}" }""", "t.json").Encoding);

    [Fact]
    public void Bad_encoding_word_names_the_expectation()
    {
        var ex = Assert.Throws<UsageException>(() => ConfigFile.Parse("""{ "encoding": "utf-8" }""", "t.json"));
        Assert.Contains("--config: 'encoding': expected utf8, ascii or latin1, got 'utf-8'", ex.Message);
    }

    [Theory]
    [InlineData("lf")]
    [InlineData("cr")]
    [InlineData("crlf")]
    [InlineData("none")]
    public void Eol_words_parse(string word)
    {
        TextEol expected = word switch { "lf" => TextEol.Lf, "cr" => TextEol.Cr, "crlf" => TextEol.CrLf, _ => TextEol.None };
        Assert.Equal(expected, ConfigFile.Parse($$"""{ "eol": "{{word}}" }""", "t.json").Eol);
    }

    [Fact]
    public void Bad_eol_word_names_the_expectation()
    {
        var ex = Assert.Throws<UsageException>(() => ConfigFile.Parse("""{ "eol": "CRLF" }""", "t.json"));
        Assert.Contains("--config: 'eol': expected lf, cr, crlf or none, got 'CRLF'", ex.Message);
    }

    // ---- Parse: hex addresses keep the CLI's "0x…" shape ----

    [Theory]
    [InlineData("0x20000000", 0x20000000u)]
    [InlineData("0X20000000", 0x20000000u)]
    public void Rtt_addr_takes_a_0x_prefixed_string(string text, uint expected) =>
        Assert.Equal(expected, ConfigFile.Parse($$"""{ "rttAddr": "{{text}}" }""", "t.json").RttAddress);

    [Theory]
    [InlineData("\"20000000\"")]    // missing the 0x prefix
    [InlineData("\"0xGGGG\"")]      // not hex
    [InlineData("536870912")]       // bare JSON number
    public void Rtt_addr_rejects_everything_but_a_0x_string(string jsonValue)
    {
        var ex = Assert.Throws<UsageException>(() => ConfigFile.Parse($$"""{ "rttAddr": {{jsonValue}} }""", "t.json"));
        Assert.Contains("--config: 'rttAddr': expected a \"0x…\" hex string", ex.Message);
    }

    [Fact]
    public void Rtt_range_follows_the_same_hex_rule()
    {
        Assert.Equal(0x1000u, ConfigFile.Parse("""{ "rttRange": "0x1000" }""", "t.json").RttRange);
        var ex = Assert.Throws<UsageException>(() => ConfigFile.Parse("""{ "rttRange": 4096 }""", "t.json"));
        Assert.Contains("--config: 'rttRange': expected a \"0x…\" hex string", ex.Message);
    }

    [Fact]
    public void Hex_value_past_uint_range_is_rejected()
    {
        var ex = Assert.Throws<UsageException>(() => ConfigFile.Parse("""{ "rttAddr": "0x1FFFFFFFF" }""", "t.json"));
        Assert.Contains("--config: 'rttAddr': expected a \"0x…\" hex string", ex.Message);
    }

    [Fact]
    public void Duplicate_keys_last_one_wins() =>
        Assert.Equal(2, ConfigFile.Parse("""{ "channel": 1, "channel": 2 }""", "t.json").Channel);

    [Fact]
    public void Excluded_key_is_flagged_even_when_null()
    {
        // the excluded check runs before the null skip: naming the key beats ignoring it
        var ex = Assert.Throws<UsageException>(() => ConfigFile.Parse("""{ "hex": null }""", "t.json"));
        Assert.Contains("--config: 'hex' is not a config key", ex.Message);
    }

    // ---- Parse: integer strictness ----

    [Fact]
    public void Int_keys_reject_floats()
    {
        var ex = Assert.Throws<UsageException>(() => ConfigFile.Parse("""{ "channel": 1.5 }""", "t.json"));
        Assert.Contains("--config: 'channel': expected an integer", ex.Message);
    }

    [Fact]
    public void Int_keys_reject_strings()
    {
        var ex = Assert.Throws<UsageException>(() => ConfigFile.Parse("""{ "speed": "4000" }""", "t.json"));
        Assert.Contains("--config: 'speed': expected an integer", ex.Message);
    }

    [Fact]
    public void Text_keys_reject_numbers()
    {
        var ex = Assert.Throws<UsageException>(() => ConfigFile.Parse("""{ "chip": 42 }""", "t.json"));
        Assert.Contains("--config: 'chip': expected a string", ex.Message);
    }

    // ---- Parse: schema errors ----

    [Fact]
    public void Unknown_key_is_named_with_its_source()
    {
        var ex = Assert.Throws<UsageException>(() => ConfigFile.Parse("""{ "hcip": "x" }""", "my.json"));
        Assert.Contains("--config: unknown key 'hcip' (my.json)", ex.Message);
    }

    [Theory]
    [InlineData("hex")]
    [InlineData("tui")]
    [InlineData("reset")]
    [InlineData("filter")]
    [InlineData("config")]
    public void Excluded_keys_get_their_own_error(string key)
    {
        var ex = Assert.Throws<UsageException>(() => ConfigFile.Parse($$"""{ "{{key}}": true }""", "t.json"));
        Assert.Contains($"--config: '{key}' is not a config key", ex.Message);
    }

    [Fact]
    public void Broken_json_names_the_source()
    {
        var ex = Assert.Throws<UsageException>(() => ConfigFile.Parse("{chip", "cfg.json"));
        Assert.StartsWith("--config: cannot parse 'cfg.json'", ex.Message);
    }

    [Fact]
    public void Non_object_root_is_a_parse_error()
    {
        var ex = Assert.Throws<UsageException>(() => ConfigFile.Parse("[1, 2]", "t.json"));
        Assert.StartsWith("--config: cannot parse 't.json'", ex.Message);
    }

    // ---- ApplyTo(options, values): fill-null merge, CLI wins ----

    [Fact]
    public void Empty_options_take_all_values()
    {
        var options = new CommandLineOptions();
        ConfigFile.ApplyTo(options, new ConfigValues
        {
            Chip = "STM32F103C8",
            SpeedKhz = 4000,
            Interface = RttInterface.Swd,
            RttAddress = 0x20000000,
            SerialNo = 7,
            Channel = 2,
            DllPath = "a.dll",
            Encoding = TextEncodingKind.Latin1,
            Eol = TextEol.CrLf,
            LogFile = "rtt.log",
            WaitMs = 500,
            ScriptTimeoutMs = 0,
        });
        Assert.Equal("STM32F103C8", options.Chip);
        Assert.Equal(4000, options.SpeedKhz);
        Assert.Equal(RttInterface.Swd, options.Interface);
        Assert.Equal(0x20000000u, options.RttAddress);
        Assert.Equal(7, options.SerialNo);
        Assert.Equal(2, options.Channel);
        Assert.Equal("a.dll", options.DllPath);
        Assert.Equal(TextEncodingKind.Latin1, options.Encoding);
        Assert.Equal(TextEol.CrLf, options.Eol);
        Assert.Equal("rtt.log", options.LogFile);
        Assert.Equal(500, options.WaitMs);
        Assert.Equal(0, options.ScriptTimeoutMs);
    }

    [Fact]
    public void Cli_set_values_are_kept()
    {
        var options = new CommandLineOptions { Chip = "FROM_CLI", Channel = 7 };
        ConfigFile.ApplyTo(options, new ConfigValues { Chip = "from_file", Channel = 1, SpeedKhz = 1000 });
        Assert.Equal("FROM_CLI", options.Chip);
        Assert.Equal(7, options.Channel);
        Assert.Equal(1000, options.SpeedKhz);   // unset members still fill
    }

    [Fact]
    public void Null_values_leave_options_untouched()
    {
        var options = new CommandLineOptions { Chip = "X" };
        ConfigFile.ApplyTo(options, new ConfigValues());
        Assert.Equal("X", options.Chip);
        Assert.Null(options.Channel);
    }

    [Fact]
    public void Partial_values_fill_only_their_members()
    {
        var options = new CommandLineOptions();
        ConfigFile.ApplyTo(options, new ConfigValues { Channel = 3 });
        Assert.Equal(3, options.Channel);
        Assert.Equal("", options.Chip);
        Assert.Null(options.SpeedKhz);
    }

    // ---- ApplyTo(options, path, baseDir): file resolution ----

    private static string TempJson(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"rtt-cli-cfg-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Explicit_path_is_loaded_and_merged()
    {
        string path = TempJson("""{ "channel": 3 }""");
        try
        {
            var options = new CommandLineOptions();
            ConfigFile.ApplyTo(options, path);
            Assert.Equal(3, options.Channel);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_explicit_path_is_a_usage_error()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rtt-cli-cfg-{Guid.NewGuid():N}.json");
        var ex = Assert.Throws<UsageException>(() => ConfigFile.ApplyTo(new CommandLineOptions(), path));
        Assert.Contains($"--config: file not found: {path}", ex.Message);
    }

    [Fact]
    public void Default_file_in_base_dir_is_loaded()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"rtt-cli-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ConfigFile.DefaultFileName), """{ "chip": "FROM_FILE" }""");
            var options = new CommandLineOptions();
            ConfigFile.ApplyTo(options, configPath: null, baseDir: dir);
            Assert.Equal("FROM_FILE", options.Chip);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Without_a_default_file_it_is_a_silent_no_op()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"rtt-cli-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var options = new CommandLineOptions();
            ConfigFile.ApplyTo(options, configPath: null, baseDir: dir);
            Assert.Equal("", options.Chip);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Explicit_path_beats_the_default_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"rtt-cli-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string explicitPath = Path.Combine(dir, "override.json");
        try
        {
            File.WriteAllText(Path.Combine(dir, ConfigFile.DefaultFileName), """{ "chip": "from_default" }""");
            File.WriteAllText(explicitPath, """{ "chip": "from_explicit" }""");
            var options = new CommandLineOptions();
            ConfigFile.ApplyTo(options, explicitPath, baseDir: dir);
            Assert.Equal("from_explicit", options.Chip);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Unreadable_config_file_becomes_a_usage_error()
    {
        string path = TempJson("""{ "chip": "X" }""");
        try
        {
            // FileShare.None stands in for antivirus/editor/ACL locks: a read failure must
            // surface as a usage error, never as an unhandled exception through Main
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            var ex = Assert.Throws<UsageException>(() => ConfigFile.ApplyTo(new CommandLineOptions(), path));
            Assert.Contains($"--config: cannot read '{path}'", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Empty_config_path_falls_back_to_the_default_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"rtt-cli-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // `-c ""` counts as "not provided": the default probe still applies
            File.WriteAllText(Path.Combine(dir, ConfigFile.DefaultFileName), """{ "chip": "FROM_FILE" }""");
            var options = new CommandLineOptions();
            ConfigFile.ApplyTo(options, configPath: "", baseDir: dir);
            Assert.Equal("FROM_FILE", options.Chip);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
