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

    // A write that fails mid-session (disk full, removed drive, disposed stream) must come back as
    // an IOException naming --log: the transport fails the link on it, and that message is all the
    // user sees. A disposed stream is the deterministically testable stand-in for disk-full.
    [Fact]
    public void Failed_append_names_the_log_option()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rtt-cli-test-{Guid.NewGuid():N}.log");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Dispose();   // a write to it now throws ObjectDisposedException → the --log wrap
        try
        {
            var ex = Assert.Throws<IOException>(() => RttLogFile.Append(stream, [1, 2, 3]));
            Assert.Contains("--log", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_returns_null_without_a_path()
    {
        Assert.Null(RttLogFile.Open(null));
        Assert.Null(RttLogFile.Open(""));
    }

    // The happy path: --log actually captures, batches in order, and the bytes are on disk once
    // the renderer (and with it the stream) is disposed.
    [Fact]
    public void Log_captures_batches_in_order_until_dispose()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rtt-cli-test-{Guid.NewGuid():N}.log");
        try
        {
            using (var renderer = new RttRenderer(new CommandLineOptions { LogFile = path }, TextWriter.Null, new object()))
            {
                renderer.OnData("hello "u8.ToArray());
                renderer.OnData("rtt"u8.ToArray());
            }
            Assert.Equal("hello rtt"u8.ToArray(), File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
