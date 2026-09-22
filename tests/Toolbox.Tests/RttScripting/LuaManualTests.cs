using Toolbox.Tools.RttCli.Scripting;

namespace Toolbox.Tests.RttScripting;

public class LuaManualTests
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
        Assert.Contains($"rtt.{name}(", LuaManual.Text);
    }

    [Fact]
    public void Manual_documents_exit_codes_timeout_and_cancellation()
    {
        Assert.Contains("0 ok", LuaManual.Text);
        Assert.Contains("--script-timeout", LuaManual.Text);
        Assert.Contains("Ctrl+C", LuaManual.Text);
        Assert.Contains("pcall", LuaManual.Text);
    }

    [Fact]
    public void Manual_documents_the_advancing_scan_model()
    {
        Assert.Contains("scans forward from the end of the last match", LuaManual.Text);
        Assert.Contains("consumed text is never re-matched", LuaManual.Text);
    }

    [Fact]
    public void Manual_warns_about_eol_none()
    {
        Assert.Contains("--eol none", LuaManual.Text);
    }
}
