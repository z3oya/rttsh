using System.Text;
using System.Text.Json;
using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttNative;

/// <summary>Glue coverage for the Rust native crate (rttsh_mcp_native.dll): lifecycle, the
/// Rust→C# dispatch callback with its ownership-transfer response contract, and the
/// drain/wait pump. Handlers answer with envelopes (the wire format is {"ok":..,"text":..}
/// JSON); the MCP-protocol-level round trip lives in McpNativeE2ETests (the server is inside
/// the DLL since ABI v2). Pure in-process FFI — no hardware.</summary>
public class McpNativeGlueTests
{
    private static McpEnvelope ReverseHandler(int handle, string method, ReadOnlySpan<byte> request)
    {
        Assert.Equal("echo", method);
        byte[] reversed = request.ToArray();
        Array.Reverse(reversed);
        return new McpEnvelope(true, Encoding.UTF8.GetString(reversed));
    }

    private static McpEnvelope ThrowingHandler(int handle, string method, ReadOnlySpan<byte> request)
    {
        throw new InvalidOperationException("handler boom");
    }

    private static (bool Ok, string Text) ParseEnvelope(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        return (document.RootElement.GetProperty("ok").GetBoolean(), document.RootElement.GetProperty("text").GetString()!);
    }

    private static byte[] VariedBytes(int count) => Enumerable.Range(0, count).Select(i => (byte)('a' + i % 26)).ToArray();

    [Fact]
    public void AbiVersion_Matches()
    {
        Assert.Equal(McpNative.AbiVersion, McpNative.RttshMcpAbiVersion());
    }

    [Fact]
    public void DispatchProbe_RoundTrips_Through_The_Callback()
    {
        using var session = McpNativeSession.Start(ReverseHandler);
        // 100 varied bytes: a payload large enough that a trivial pass-through would not
        // pass for a real round trip.
        byte[] request = VariedBytes(100);
        byte[] response = session.DispatchProbe("echo", request);
        (bool ok, string text) = ParseEnvelope(response);
        Assert.True(ok, text);
        Assert.Equal(Encoding.UTF8.GetString(request.Reverse().ToArray()), text);
    }

    [Fact]
    public void Probe_Response_Drains_In_Chunks()
    {
        using var session = McpNativeSession.Start(ReverseHandler);
        byte[] request = VariedBytes(100);
        byte[] method = "echo"u8.ToArray();
        int length = McpNative.RttshMcpDispatchProbe(session.Handle, method, (uint)method.Length, request, (uint)request.Length);
        Assert.True(length > request.Length, "the envelope should exceed the raw request");
        Assert.Equal(length, session.Wait(1000));

        // The envelope crosses the drain in whatever chunks the caller's buffer allows.
        var buffer = new byte[4];
        Assert.Equal(4, session.Drain(buffer));
        var drained = new List<byte>(buffer);
        drained.AddRange(session.DrainAll());
        (bool ok, string text) = ParseEnvelope([.. drained]);
        Assert.True(ok, text);
        Assert.Equal(Encoding.UTF8.GetString(request.Reverse().ToArray()), text);
        Assert.Equal(0, session.Wait(30));
    }

    /// <summary>Execute-once is structural: the dispatch probe invokes the callback exactly
    /// once per logical call - there is no retry loop that could re-run the handler.</summary>
    [Fact]
    public void DispatchProbe_Executes_The_Handler_Exactly_Once()
    {
        int executions = 0;
        using var session = McpNativeSession.Start((handle, method, request) =>
        {
            executions++;
            return new McpEnvelope(true, Encoding.UTF8.GetString(request));   // echo
        });

        byte[] request = VariedBytes(100);
        byte[] method = "echo"u8.ToArray();
        int length = McpNative.RttshMcpDispatchProbe(session.Handle, method, (uint)method.Length, request, (uint)request.Length);
        Assert.True(length > 0);
        (bool ok, string text) = ParseEnvelope(session.DrainAll());
        Assert.True(ok, text);
        Assert.Equal(Encoding.UTF8.GetString(request), text);
        Assert.Equal(1, executions);
    }

    /// <summary>Byte-identical repeated calls are independent calls: with no cache and no
    /// retry in the dispatch path, each one must reach the handler (repeated rtt_read and
    /// get_status must never see a stale answer).</summary>
    [Fact]
    public void Identical_Repeated_Calls_Execute_Again()
    {
        int executions = 0;
        using var session = McpNativeSession.Start((handle, method, request) =>
        {
            executions++;
            return new McpEnvelope(true, "ok");
        });

        byte[] request = "{}"u8.ToArray();
        byte[] method = "echo"u8.ToArray();
        session.DispatchProbe("echo", request);
        session.DispatchProbe("echo", request);   // byte-identical, still two calls
        Assert.Equal(2, executions);
    }

    [Fact]
    public void DispatchProbe_Handler_Exception_Surfaces_As_ErrInternal()
    {
        using var session = McpNativeSession.Start(ThrowingHandler);
        var failure = Assert.Throws<InvalidOperationException>(() => session.DispatchProbe("echo", "abc"u8.ToArray()));
        Assert.Contains("native internal error", failure.Message);
    }

    [Fact]
    public void Native_Panic_Probe_Surfaces_As_ErrInternal()
    {
        Assert.Equal(McpNative.ErrInternal, McpNative.RttshMcpPanicProbe());
        // The panic was contained inside the DLL: the host process keeps working.
        using var session = McpNativeSession.Start(ReverseHandler);
        (bool ok, string text) = ParseEnvelope(session.DispatchProbe("echo", "abc"u8.ToArray()));
        Assert.True(ok, text);
        Assert.Equal("cba", text);
    }

    [Fact]
    public void Operations_After_Stop_Throw()
    {
        var session = McpNativeSession.Start(ReverseHandler);
        session.Stop();
        Assert.Throws<InvalidOperationException>(() => session.Feed([1]));
        Assert.Throws<InvalidOperationException>(() => session.Drain([]));
        Assert.Throws<InvalidOperationException>(() => session.Wait(10));
        Assert.Throws<InvalidOperationException>(() => session.DispatchProbe("echo", "abc"u8.ToArray()));
        session.Dispose();   // idempotent
    }
}
