using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

public class ManualTests
{
    [Theory]
    [InlineData("send")]
    [InlineData("send_hex")]
    [InlineData("log")]
    [InlineData("wait")]
    [InlineData("wait_hex")]
    [InlineData("expect")]
    [InlineData("try_expect")]
    [InlineData("expect_absent")]
    [InlineData("expect_any")]
    [InlineData("read_line")]
    [InlineData("flush")]
    [InlineData("now")]
    [InlineData("sleep")]
    [InlineData("exit")]
    [InlineData("set_timeout")]
    [InlineData("mem_read")]
    [InlineData("mem_write")]
    [InlineData("is_halted")]
    [InlineData("halt")]
    [InlineData("resume")]
    public void Manual_documents_every_rtt_function(string name)
    {
        Assert.Contains($"rtt.{name}(", Manual.Text);
    }

    [Fact]
    public void Manual_documents_exit_codes_timeout_and_cancellation()
    {
        Assert.Contains("0 ok", Manual.Text);
        Assert.Contains("--script-timeout", Manual.Text);
        Assert.Contains("Ctrl+C", Manual.Text);
        Assert.Contains("pcall", Manual.Text);
    }

    [Fact]
    public void Manual_documents_the_advancing_scan_model()
    {
        Assert.Contains("scans forward", Manual.Text);
        Assert.Contains("consumed text is", Manual.Text);
        Assert.Contains("never re-matched", Manual.Text);
    }

    [Fact]
    public void Manual_warns_about_eol_none()
    {
        Assert.Contains("--eol none", Manual.Text);
    }

    [Fact]
    public void Manual_documents_the_config_file_entry_points()
    {
        Assert.Contains("--config", Manual.Text);
        Assert.Contains("--config / -c", Manual.Text);   // the real alias mention, not a substring accident
        Assert.Contains(".rttsh/config.json", Manual.Text);
    }

    [Fact]
    public void Manual_states_the_precedence_and_null_semantics()
    {
        Assert.Contains("command line > config file > built-in default", Manual.Text);
        Assert.Contains("null counts as unset", Manual.Text);
    }

    [Fact]
    public void Manual_lists_every_config_key()
    {
        string[] keys =
        [
            "chip", "speed", "interface", "rttAddr", "rttRange", "elf", "sn", "channel",
            "encoding", "eol", "log", "wait", "scriptTimeout",
        ];
        foreach (string key in keys)
            Assert.Contains(key, Manual.Text);
    }

    [Fact]
    public void Manual_documents_the_elf_lookup_policy()
    {
        Assert.Contains("thirteen", Manual.Text);
        Assert.Contains("--elf", Manual.Text);
        Assert.Contains("explicit rttAddr wins", Manual.Text);
    }

    [Fact]
    public void Manual_documents_the_settings_file()
    {
        Assert.Contains("~/.rttsh/settings.json", Manual.Text);
        Assert.Contains("fromelf", Manual.Text);
        Assert.Contains("command line > settings.json", Manual.Text);
        // the retired config.json key points at the new home
        Assert.Contains("\"dll\" key is rejected", Manual.Text);
    }

    [Fact]
    public void Manual_documents_the_elf_flash_conversion()
    {
        // why flash download refuses ELF without fromelf: the DLL loader flaw + the fix
        Assert.Contains("ELF images by section execution addresses", Manual.Text);
        Assert.Contains("erased flash", Manual.Text);
        Assert.Contains("--i32combined", Manual.Text);
    }

    [Fact]
    public void Manual_shows_elf_usage_examples()
    {
        Assert.Contains("--elf app.axf", Manual.Text);
        Assert.Contains("_SEGGER_RTT at 0x24000070 (from 'app.axf')", Manual.Text);
        Assert.Contains("--elf ignored", Manual.Text);
        Assert.Contains("down-buffer made no progress", Manual.Text);
        Assert.Contains("falling back to the SDK RAM scan", Manual.Text);
    }

    [Fact]
    public void Manual_pins_addresses_to_hex_strings()
    {
        Assert.Contains("\"rttAddr\": \"0x20000000\"", Manual.Text);
        Assert.Contains("fails the type check", Manual.Text);     // decimal number
        Assert.Contains("not even valid JSON", Manual.Text);      // bare 0x literal
    }

    [Fact]
    public void Manual_documents_the_empty_string_unset_rule()
    {
        Assert.Contains("\"\" = unset", Manual.Text);
    }

    [Fact]
    public void Manual_names_the_rejected_keys_and_the_error_prefix()
    {
        Assert.Contains("hex, tui, reset, filter, config", Manual.Text);
        Assert.Contains("--config: ", Manual.Text);
    }

    [Fact]
    public void Manual_documents_the_capture_idiom_and_its_pitfall()
    {
        Assert.Contains("local t, m, id = rtt.expect(", Manual.Text);
        Assert.Contains("tonumber(\"#18\") is nil", Manual.Text);
    }

    [Fact]
    public void Manual_shows_a_negative_assertion_with_expect_absent()
    {
        Assert.Contains("rtt.expect_absent(\"busy\", 400)", Manual.Text);
        Assert.Contains("rtt.expect(\"tick: off\", 500)", Manual.Text);
    }

    [Fact]
    public void Manual_shows_the_multi_branch_and_line_tailing_idioms()
    {
        Assert.Contains("rtt.expect_any(2000, \"ready\", \"err=(%d+)\")", Manual.Text);
        Assert.Contains("rtt.read_line(100)", Manual.Text);
    }
}
