using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

public class RttFailureContextTests
{
    [Fact]
    public void Mapped_codes_get_their_documented_meaning()
    {
        Assert.Contains("control block not found", JLinkRttTransport.RttFailureContext(-2));
        Assert.Contains("no connection", JLinkRttTransport.RttFailureContext(-256));
        Assert.Contains("low-power", JLinkRttTransport.RttFailureContext(-274));
    }

    [Fact]
    public void Unmapped_codes_admit_ignorance_and_hint_at_contention()
    {
        string hint = JLinkRttTransport.RttFailureContext(-11);   // 真机观察到的未文档化码
        Assert.Contains("not in the verified table", hint);
        Assert.Contains("another debugger", hint);
    }
}
