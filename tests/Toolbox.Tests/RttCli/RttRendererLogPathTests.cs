using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

public class RttRendererLogPathTests
{
    // '|' is an illegal path character on Windows, so FileStream refuses the open. (On Unix it is
    // a legal file name and FileMode.Append would quietly create the file - not a case this
    // project targets, so there is no platform guard here on purpose.)
    [Fact]
    public void Bad_log_path_becomes_a_usage_error()
    {
        var options = new CommandLineOptions { LogFile = "bad|name.log" };   // Windows 非法字符 '|' → .NET Core 实测抛 IOException
        var ex = Assert.Throws<UsageException>(() =>
            new RttRenderer(options, TextWriter.Null, new object()));
        Assert.Contains("--log", ex.Message);
    }

    // A directory passed to --log: opening it as an append stream fails, reaching the same catch
    // arm as the illegal-character case above.
    [Fact]
    public void Log_path_that_is_a_directory_becomes_a_usage_error()
    {
        var options = new CommandLineOptions { LogFile = Path.GetTempPath() };
        var ex = Assert.Throws<UsageException>(() =>
            new RttRenderer(options, TextWriter.Null, new object()));
        Assert.Contains("--log", ex.Message);
    }

    [Fact]
    public void Missing_log_directory_becomes_a_usage_error()
    {
        string path = Path.Combine(Path.GetTempPath(), "no_such_dir_xyz", "f.log");   // 目录不存在 → DirectoryNotFoundException 臂
        var options = new CommandLineOptions { LogFile = path };
        var ex = Assert.Throws<UsageException>(() =>
            new RttRenderer(options, TextWriter.Null, new object()));
        Assert.Contains("--log", ex.Message);
    }
}
