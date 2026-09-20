using Toolbox.Core.Rtt;

namespace Toolbox.Tests.RttScripting;

public class TestRttTransportTests
{
    [Fact]
    public void Feed_dispatches_chunks_through_DataReceived()
    {
        var transport = new TestRttTransport();
        var seen = new List<byte[]>();
        transport.DataReceived += seen.Add;
        transport.Feed([1, 2], [3]);
        Assert.Equal(2, seen.Count);
        Assert.Equal([1, 2], seen[0]);
    }

    [Fact]
    public void Write_records_payloads_and_WriteFailure_throws()
    {
        var transport = new TestRttTransport();
        transport.Write([9, 8]);
        Assert.Single(transport.Written);
        Assert.Equal([9, 8], transport.Written[0]);
        transport.WriteFailure = new IOException("down");
        Assert.Throws<IOException>(() => transport.Write([1]));
    }

    [Fact]
    public void Fail_raises_Error_and_closes_the_link()
    {
        var transport = new TestRttTransport();
        transport.Open(new RttConnectionConfig());
        Exception? seen = null;
        transport.Error += e => seen = e;
        transport.Fail("probe gone");
        Assert.False(transport.IsOpen);
        Assert.IsType<IOException>(seen);
    }
}
