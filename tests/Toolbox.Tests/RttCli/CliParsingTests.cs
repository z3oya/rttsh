using Toolbox.Core.SerialComm;
using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

/// <summary>Parses the CLI surface that System.CommandLine now drives: aliases kept from the
/// old hand-rolled parser, the exit-code contract, and the legacy error wording.</summary>
public class CliParsingTests
{
    [Fact]
    public void No_args_defaults_to_monitor()
    {
        Assert.IsType<MonitorCommand>(CommandLine.Parse([]));
    }

    [Fact]
    public void Options_only_default_to_monitor()
    {
        var command = Assert.IsType<MonitorCommand>(CommandLine.Parse(["--chip", "X"]));
        Assert.Equal("X", command.Options.Chip);
    }

    [Fact]
    public void Bare_help_and_version_words_still_work()
    {
        Assert.IsType<HelpCommand>(CommandLine.Parse(["help"]));
        Assert.IsType<VersionCommand>(CommandLine.Parse(["version"]));
    }

    [Fact]
    public void Short_help_and_version_flags_still_work()
    {
        Assert.IsType<HelpCommand>(CommandLine.Parse(["-h"]));
        Assert.IsType<VersionCommand>(CommandLine.Parse(["-v"]));
    }

    [Fact]
    public void Help_text_carries_the_exit_code_contract()
    {
        var command = Assert.IsType<HelpCommand>(CommandLine.Parse(["--help"]));
        Assert.Contains("Usage:", command.Text);
        Assert.Contains("Exit codes: 0 ok, 1 runtime failure, 2 usage error.", command.Text);
    }

    [Fact]
    public void Help_anywhere_beats_the_rest_of_the_arguments()
    {
        Assert.IsType<HelpCommand>(CommandLine.Parse(["--chip", "X", "--help"]));
    }

    [Fact]
    public void Subcommand_help_renders_that_command()
    {
        var command = Assert.IsType<HelpCommand>(CommandLine.Parse(["send", "--help"]));
        Assert.Contains("text to send", command.Text);
        Assert.Contains("send -- --value", command.Text);
    }

    [Fact]
    public void Tui_accepts_both_spellings()
    {
        Assert.True(Assert.IsType<MonitorCommand>(CommandLine.Parse(["-tui"])).Options.Tui);
        Assert.True(Assert.IsType<MonitorCommand>(CommandLine.Parse(["--tui"])).Options.Tui);
    }

    [Fact]
    public void Tui_rejects_an_explicit_value()
    {
        Assert.Throws<UsageException>(() => CommandLine.Parse(["--tui", "false"]));
    }

    [Fact]
    public void Hex_flag_sets_the_hex_option()
    {
        Assert.True(Assert.IsType<MonitorCommand>(CommandLine.Parse(["--hex"])).Options.Hex);
    }

    [Fact]
    public void Rtt_addr_accepts_the_0x_prefix()
    {
        var command = Assert.IsType<MonitorCommand>(CommandLine.Parse(["--rtt-addr", "0x20000000"]));
        Assert.Equal(0x20000000u, command.Options.RttAddress);
    }

    [Fact]
    public void Reset_flags_form_a_tri_state()
    {
        Assert.True(Assert.IsType<MonitorCommand>(CommandLine.Parse(["--reset"])).Options.ResetOnConnect);
        Assert.False(Assert.IsType<MonitorCommand>(CommandLine.Parse(["--no-reset"])).Options.ResetOnConnect);
        Assert.Null(Assert.IsType<MonitorCommand>(CommandLine.Parse(["--chip", "X"])).Options.ResetOnConnect);
    }

    [Fact]
    public void Speed_rejects_non_numbers_with_the_legacy_message()
    {
        var ex = Assert.Throws<UsageException>(() => CommandLine.Parse(["--speed", "abc"]));
        Assert.Contains("--speed: 'abc' is not a number", ex.Message);
    }

    [Fact]
    public void If_rejects_bad_values_with_the_legacy_message()
    {
        var ex = Assert.Throws<UsageException>(() => CommandLine.Parse(["--if", "xyz"]));
        Assert.Contains("--if: expected swd or jtag, got 'xyz'", ex.Message);
    }

    [Fact]
    public void Option_without_a_value_keeps_the_legacy_message()
    {
        var ex = Assert.Throws<UsageException>(() => CommandLine.Parse(["--chip"]));
        Assert.Contains("--chip: missing device name value", ex.Message);
    }

    [Fact]
    public void Eol_and_encoding_parse_into_their_enums()
    {
        var command = Assert.IsType<MonitorCommand>(CommandLine.Parse(["--eol", "crlf", "--encoding", "latin1"]));
        Assert.Equal(TextEol.CrLf, command.Options.Eol);
        Assert.Equal(TextEncodingKind.Latin1, command.Options.Encoding);
    }

    [Fact]
    public void Monitor_accepts_foreign_options()
    {
        var command = Assert.IsType<MonitorCommand>(CommandLine.Parse(["--chip", "X", "--filter", "st"]));
        Assert.Equal("st", command.Options.DeviceFilter);
    }

    [Fact]
    public void Unknown_command_reports_the_legacy_message()
    {
        var ex = Assert.Throws<UsageException>(() => CommandLine.Parse(["frobnicate"]));
        Assert.Contains("unknown command 'frobnicate'", ex.Message);
    }

    [Fact]
    public void Unknown_option_reports_the_legacy_message()
    {
        var ex = Assert.Throws<UsageException>(() => CommandLine.Parse(["--bogus"]));
        Assert.Contains("unknown option '--bogus'", ex.Message);
    }

    [Fact]
    public void Send_requires_the_payload_immediately()
    {
        var error = Assert.IsType<UsageErrorCommand>(CommandLine.Parse(["send", "--chip", "X"]));
        Assert.Contains("send", error.Message);
    }

    [Fact]
    public void Send_parses_payload_and_trailing_options()
    {
        var command = Assert.IsType<SendCommand>(CommandLine.Parse(["send", "reboot", "--chip", "X"]));
        Assert.Equal("reboot", command.Payload);
        Assert.Equal("X", command.Options.Chip);
    }

    [Fact]
    public void Send_takes_a_single_dash_payload_like_the_old_parser()
    {
        var command = Assert.IsType<SendCommand>(CommandLine.Parse(["send", "-5"]));
        Assert.Equal("-5", command.Payload);
    }

    [Fact]
    public void End_of_options_separator_allows_a_dash_leading_payload()
    {
        var command = Assert.IsType<SendCommand>(CommandLine.Parse(["send", "--", "-5"]));
        Assert.Equal("-5", command.Payload);
    }

    [Fact]
    public void Script_takes_a_single_dash_path_like_the_old_parser()
    {
        var command = Assert.IsType<ScriptCommand>(CommandLine.Parse(["script", "-x.lua"]));
        Assert.Equal("-x.lua", command.ScriptPath);
    }

    [Fact]
    public void Script_parses_the_file_path_and_trailing_options()
    {
        var command = Assert.IsType<ScriptCommand>(CommandLine.Parse(["script", "run.lua", "--chip", "X"]));
        Assert.Equal("run.lua", command.ScriptPath);
        Assert.Null(command.EvalSource);
        Assert.Equal("X", command.Options.Chip);
    }

    [Fact]
    public void Script_eval_runs_inline_code()
    {
        var command = Assert.IsType<ScriptCommand>(CommandLine.Parse(["script", "--eval", "rtt.log('x')"]));
        Assert.Null(command.ScriptPath);
        Assert.Equal("rtt.log('x')", command.EvalSource);
    }

    [Fact]
    public void Eval_needs_its_lua_source()
    {
        var error = Assert.IsType<UsageErrorCommand>(CommandLine.Parse(["script", "--eval"]));
        Assert.Contains("script: --eval needs the lua source text", error.Message);
    }

    [Fact]
    public void Eval_cannot_be_combined_with_a_script_file()
    {
        var ex = Assert.Throws<UsageException>(() => CommandLine.Parse(["script", "run.lua", "--eval", "x"]));
        Assert.Contains("script: --eval cannot be combined with a script file ('run.lua')", ex.Message);
    }

    [Fact]
    public void Number_and_path_options_parse()
    {
        var command = Assert.IsType<MonitorCommand>(CommandLine.Parse(
        [
            "--rtt-range", "0x1000", "--sn", "42", "--wait", "500", "--script-timeout", "1000",
            "--log", "out.bin", "--dll", @"C:\JLink\JLink_x64.dll",
        ]));
        Assert.Equal(0x1000u, command.Options.RttRange);
        Assert.Equal(42, command.Options.SerialNo);
        Assert.Equal(500, command.Options.WaitMs);
        Assert.Equal(1000, command.Options.ScriptTimeoutMs);
        Assert.Equal("out.bin", command.Options.LogFile);
        Assert.Equal(@"C:\JLink\JLink_x64.dll", command.Options.DllPath);
    }

    [Fact]
    public void List_devices_parses_the_filter()
    {
        var command = Assert.IsType<ListDevicesCommand>(CommandLine.Parse(["list-devices", "--filter", "stm32"]));
        Assert.Equal("stm32", command.Filter);
    }

    [Fact]
    public void Channel_option_parses_into_config()
    {
        var command = Assert.IsType<MonitorCommand>(CommandLine.Parse(["--chip", "X", "--channel", "3"]));
        Assert.Equal(3, command.Options.ToConnectionConfig(resetDefault: false).Channel);
    }

    [Fact]
    public void Channel_defaults_to_zero()
    {
        var command = Assert.IsType<MonitorCommand>(CommandLine.Parse(["--chip", "X"]));
        Assert.Equal(0, command.Options.ToConnectionConfig(resetDefault: false).Channel);
    }

    [Theory]
    [InlineData("16")]
    [InlineData("-1")]
    public void Channel_out_of_range_is_a_usage_error(string channel)
    {
        var command = Assert.IsType<MonitorCommand>(CommandLine.Parse(["--chip", "X", "--channel", channel]));
        var ex = Assert.Throws<UsageException>(() => command.Options.ToConnectionConfig(resetDefault: false));
        if (channel == "16")   // pin the wording once; -1 anchors the lower-bound branch
            Assert.Contains("--channel: expected 0-15, got 16", ex.Message);
    }

    [Fact]
    public void Channel_without_value_is_a_usage_error()
    {
        Assert.Throws<UsageException>(() => CommandLine.Parse(["--chip", "X", "--channel"]));
    }
}
