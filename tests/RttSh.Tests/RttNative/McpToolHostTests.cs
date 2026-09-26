using System.Text;
using System.Text.Json;
using RttSh.Core.Rtt;
using Toolbox.Tests.RttScripting;
using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttNative;

/// <summary>The MCP tool surface against in-memory fakes: all eight tools without the
/// J-Link DLL or hardware. Calls go straight through Dispatch (the Rust callback entry),
/// so the envelope contract and every tool's reuse of the real C# machinery is covered.</summary>
public class McpToolHostTests : IDisposable
{
    private readonly TestRttTransport _transport = new();
    private readonly TestTargetMemory _memory = new() { Data = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88] };
    private readonly McpToolHost _host;

    public McpToolHostTests()
    {
        _host = new McpToolHost(new McpToolHost.Options
        {
            TransportFactory = _ => _transport,
            ChipValidator = _ => { },
            LockDirectory = Path.Combine(Path.GetTempPath(), "rttsh-mcp-tests", Guid.NewGuid().ToString("N")),
            Memory = _memory,
        });
    }

    public void Dispose() => _host.Dispose();

    private (bool Ok, string Text) Call(string method, object? arguments = null)
    {
        byte[] request = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { method, @params = arguments ?? new { } }));
        McpEnvelope envelope = _host.Dispatch(1, method, request);
        return (envelope.Ok, envelope.Text);
    }

    private void Connect()
    {
        (bool ok, string text) = Call("connect", new { chip = "TESTCHIP" });
        Assert.True(ok, text);
        Assert.Contains("connected to TESTCHIP", text);
    }

    [Fact]
    public void Connect_Requires_A_Chip()
    {
        (bool ok, string text) = Call("connect", new { });
        Assert.False(ok);
        Assert.Contains("chip is required", text);
    }

    [Fact]
    public void Connect_Send_Expect_MemRead_Disconnect_RoundTrip()
    {
        Connect();

        (bool statusOk, string status) = Call("get_status");
        Assert.True(statusOk, status);
        Assert.Contains("connected: true, chip: TESTCHIP", status);

        (bool sendOk, string sent) = Call("send", new { text = "version" });
        Assert.True(sendOk, sent);
        Assert.Equal("version\n", Encoding.UTF8.GetString(_transport.Written[^1]));

        // Target output arrives between tool calls; expect scans everything unconsumed.
        _transport.Feed("boot READY\r\n"u8.ToArray());
        (bool expectOk, string matched) = Call("expect", new { pattern = "READY", timeout_ms = 1000 });
        Assert.True(expectOk, matched);
        Assert.Equal("boot READY", matched);

        _transport.Feed("more output\n"u8.ToArray());
        (bool readOk, string read) = Call("rtt_read", new { timeout_ms = 200 });
        Assert.True(readOk, read);
        // ReadAvailable returns everything not yet consumed - expect consumed through the
        // match end, so the "\r\n" it left behind leads the read.
        Assert.Equal("\r\nmore output\n", read);

        (bool memOk, string hexdump) = Call("mem_read", new { address = "0x0", count = 2, width = 32 });
        Assert.True(memOk, hexdump);
        Assert.Contains("0x44332211", hexdump);
        Assert.Contains("0x88776655", hexdump);

        (bool disconnectOk, string disconnected) = Call("disconnect");
        Assert.True(disconnectOk, disconnected);
        Assert.False(_transport.IsOpen);

        (bool afterOk, string after) = Call("get_status");
        Assert.True(afterOk, after);
        Assert.Contains("connected: false", after);
    }

    [Fact]
    public void Connect_Twice_Fails_Without_Tearing_Down_The_Session()
    {
        Connect();
        (bool ok, string text) = Call("connect", new { chip = "OTHER" });
        Assert.False(ok);
        Assert.Contains("already connected", text);
        (bool statusOk, string status) = Call("get_status");
        Assert.True(statusOk, status);
        Assert.Contains("connected: true, chip: TESTCHIP", status);
    }

    [Fact]
    public void Console_Tools_Fail_With_A_Not_Connected_Message_Before_Connect()
    {
        foreach (string method in new[] { "send", "rtt_read", "expect", "mem_read" })
        {
            (bool ok, string text) = Call(method, new { text = "x", pattern = "x", address = "0x0" });
            Assert.False(ok);
            Assert.Contains("not connected", text);
        }
    }

    [Fact]
    public void Disconnect_Is_Lenient_And_Unknown_Tools_Are_Reported()
    {
        (bool disconnectOk, string text) = Call("disconnect");
        Assert.True(disconnectOk, text);
        Assert.Equal("not connected", text);

        (bool ok, string unknown) = Call("no_such_tool");
        Assert.False(ok);
        Assert.Contains("unknown tool", unknown);
    }
}

/// <summary>The whole stack in one process: real MCP JSON-RPC over the native pump (feed →
/// in-DLL rmcp → dispatch callback → C# tool host → fake transport → back out). This is the
/// acceptance test for the architecture; it needs no hardware and no real console.</summary>
public class McpNativeE2ETests
{
    private static void FeedLine(McpNativeSession session, string line)
    {
        session.Feed(Encoding.UTF8.GetBytes(line + "\n"));   // MCP stdio framing
    }

    private static string DrainUntil(McpNativeSession session, string needle, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var accumulated = new StringBuilder();
        while (true)
        {
            if (session.Wait(50) > 0)
            {
                var buffer = new byte[4096];
                int read;
                while ((read = session.Drain(buffer)) > 0)
                    accumulated.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            if (accumulated.ToString().Contains(needle, StringComparison.Ordinal))
                return accumulated.ToString();
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {needle}; got: {accumulated}");
        }
    }

    [Fact]
    public void Client_Session_Over_The_Native_Pump()
    {
        var transport = new TestRttTransport();
        using var host = new McpToolHost(new McpToolHost.Options
        {
            TransportFactory = _ => transport,
            ChipValidator = _ => { },
            LockDirectory = Path.Combine(Path.GetTempPath(), "rttsh-mcp-tests", Guid.NewGuid().ToString("N")),
        });
        using var session = McpNativeSession.Start(host.Dispatch);

        FeedLine(session, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""");
        FeedLine(session, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        DrainUntil(session, "\"serverInfo\"", TimeSpan.FromSeconds(10));

        FeedLine(session, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"connect","arguments":{"chip":"TESTCHIP"}}}""");
        DrainUntil(session, "connected to TESTCHIP", TimeSpan.FromSeconds(10));

        FeedLine(session, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"send","arguments":{"text":"version"}}}""");
        DrainUntil(session, "\"sent\"", TimeSpan.FromSeconds(10));
        Assert.Equal("version\n", Encoding.UTF8.GetString(transport.Written[^1]));

        // Repeated calls are independent calls: a byte-identical send must reach the
        // target again (no cache sits in front of the tools).
        FeedLine(session, """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"send","arguments":{"text":"version"}}}""");
        DrainUntil(session, "\"sent\"", TimeSpan.FromSeconds(10));
        Assert.Equal(2, transport.Written.Count);

        transport.Feed("boot READY\r\n"u8.ToArray());
        FeedLine(session, """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"expect","arguments":{"pattern":"READY","timeout_ms":2000}}}""");
        string matched = DrainUntil(session, "boot READY", TimeSpan.FromSeconds(10));
        Assert.Contains("\"id\":4", matched);

        FeedLine(session, """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"disconnect","arguments":{}}}""");
        DrainUntil(session, "disconnected", TimeSpan.FromSeconds(10));
        Assert.False(transport.IsOpen);
    }
}
