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
}
