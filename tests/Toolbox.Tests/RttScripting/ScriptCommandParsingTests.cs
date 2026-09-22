using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttScripting;

public class ScriptCommandParsingTests
{
    [Fact]
    public void Script_command_parses_path_and_options()
    {
        var command = CommandLine.Parse(["script", "auto.lua", "--chip", "X", "--script-timeout", "5000"]);
        var script = Assert.IsType<ScriptCommand>(command);
        Assert.Equal("auto.lua", script.ScriptPath);
        Assert.Equal(5000, script.Options.ScriptTimeoutMs);
    }

    [Fact]
    public void Script_without_file_is_a_usage_error()
    {
        var command = CommandLine.Parse(["script"]);
        var error = Assert.IsType<UsageErrorCommand>(command);
        Assert.Contains("script", error.Message);
    }

    [Fact]
    public void Script_with_option_as_first_argument_is_a_usage_error()
    {
        var command = CommandLine.Parse(["script", "--chip", "X"]);
        Assert.IsType<UsageErrorCommand>(command);
    }

    [Fact]
    public void Unknown_option_after_script_throws_usage_exception()
    {
        Assert.Throws<UsageException>(() => CommandLine.Parse(["script", "a.lua", "--bogus"]));
    }

    [Fact]
    public void Script_eval_parses_source_and_options()
    {
        var command = CommandLine.Parse(["script", "--eval", "rtt.log('x')", "--script-timeout", "5000"]);
        var script = Assert.IsType<ScriptCommand>(command);
        Assert.Null(script.ScriptPath);
        Assert.Equal("rtt.log('x')", script.EvalSource);
        Assert.Equal(5000, script.Options.ScriptTimeoutMs);
    }

    [Fact]
    public void Script_eval_without_source_is_a_usage_error()
    {
        var command = CommandLine.Parse(["script", "--eval"]);
        var error = Assert.IsType<UsageErrorCommand>(command);
        Assert.Contains("script", error.Message);
    }

    [Fact]
    public void Script_eval_with_stray_file_is_a_usage_error()
    {
        Assert.Throws<UsageException>(() => CommandLine.Parse(["script", "--eval", "x", "a.lua"]));
    }

    [Fact]
    public void Script_manual_option_hints_at_its_replacement()
    {
        // the reference moved to its own subcommand; the retired flag says where
        var first = Assert.IsType<UsageErrorCommand>(CommandLine.Parse(["script", "--manual"]));
        var second = Assert.IsType<UsageErrorCommand>(CommandLine.Parse(["script", "a.lua", "--manual"]));
        Assert.Contains("--manual was removed", first.Message);
        Assert.Contains("rtt-cli manual", second.Message);
    }
}
