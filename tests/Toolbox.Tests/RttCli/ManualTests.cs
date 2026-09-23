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
    [InlineData("now")]
    [InlineData("sleep")]
    [InlineData("exit")]
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
        Assert.Contains("scans forward from the end of the last match", Manual.Text);
        Assert.Contains("consumed text is never re-matched", Manual.Text);
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
        Assert.Contains(".rttsh.config.json", Manual.Text);
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
            "dll", "encoding", "eol", "log", "wait", "scriptTimeout",
        ];
        foreach (string key in keys)
            Assert.Contains(key, Manual.Text);
    }

    [Fact]
    public void Manual_documents_the_elf_lookup_policy()
    {
        Assert.Contains("fourteen", Manual.Text);
        Assert.Contains("--elf", Manual.Text);
        Assert.Contains("explicit rttAddr wins", Manual.Text);
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
        Assert.Contains("r:match(\"#(%d+)\")", Manual.Text);
        Assert.Contains("tonumber(\"#18\") is nil", Manual.Text);
    }

    [Fact]
    public void Manual_shows_a_negative_assertion_with_pcall()
    {
        Assert.Contains("pcall(function() rtt.expect(", Manual.Text);
        Assert.Contains("assert(not printed", Manual.Text);
    }
}
