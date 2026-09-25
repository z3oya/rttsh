using System.Text;
using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttNative;

/// <summary>Glue coverage for the Rust native crate (rttsh_mcp_native.dll): lifecycle,
/// byte pump, and the Rust→C# dispatch callback with its caller-buffer retry contract.
/// Pure in-process FFI — no hardware, no MCP.</summary>
public class McpNativeGlueTests
{
    private static int ReverseHandler(int handle, string method, ReadOnlySpan<byte> request, Span<byte> response, out int written)
    {
        Assert.Equal("echo", method);
        byte[] reversed = request.ToArray();
        Array.Reverse(reversed);
        if (reversed.Length > response.Length)
        {
            written = reversed.Length;
            return McpNative.ErrBufferTooSmall;
        }
        reversed.CopyTo(response);
        written = reversed.Length;
        return McpNative.Ok;
    }

    private static int ThrowingHandler(int handle, string method, ReadOnlySpan<byte> request, Span<byte> response, out int written)
    {
        throw new InvalidOperationException("handler boom");
    }

    [Fact]
    public void AbiVersion_Matches()
    {
        Assert.Equal(McpNative.AbiVersion, McpNative.RttshMcpAbiVersion());
    }

    [Fact]
    public void Pump_RoundTrips_Fed_Bytes()
    {
        using var session = McpNativeSession.Start(ReverseHandler);
        session.Feed("hello"u8.ToArray());
        Assert.Equal(5, session.PumpStep());
        Assert.Equal("hello", Encoding.UTF8.GetString(session.DrainAll()));
        Assert.Equal(0, session.PumpStep());
    }

    [Fact]
    public void Drain_Copies_At_Most_The_Buffer_Size()
    {
        using var session = McpNativeSession.Start(ReverseHandler);
        session.Feed("0123456789"u8.ToArray());
        Assert.Equal(10, session.PumpStep());
        var buffer = new byte[4];
        Assert.Equal(4, session.Drain(buffer));
        Assert.Equal("0123", Encoding.UTF8.GetString(buffer));
        Assert.Equal("456789", Encoding.UTF8.GetString(session.DrainAll()));
    }

    [Fact]
    public void DispatchProbe_RoundTrips_Through_The_Callback_And_Retries()
    {
        using var session = McpNativeSession.Start(ReverseHandler);
        // 100 bytes: larger than the native probe's initial 64-byte response buffer, so
        // the callback must take the ErrBufferTooSmall / needed-size path and the Rust
        // side must retry with a grown buffer before the response comes back intact.
        byte[] request = Encoding.UTF8.GetBytes(new string('x', 100));
        byte[] response = session.DispatchProbe("echo", request);
        Array.Reverse(request);
        Assert.Equal(request, response);
    }

    [Fact]
    public void DispatchProbe_Handler_Exception_Surfaces_As_ErrInternal()
    {
        using var session = McpNativeSession.Start(ThrowingHandler);
        var failure = Assert.Throws<InvalidOperationException>(() => session.DispatchProbe("echo", [1]));
        Assert.Contains("native internal error", failure.Message);
    }

    [Fact]
    public void Native_Panic_Probe_Surfaces_As_ErrInternal()
    {
        Assert.Equal(McpNative.ErrInternal, McpNative.RttshMcpPanicProbe());
        // The panic was contained inside the DLL: the host process keeps working.
        using var session = McpNativeSession.Start(ReverseHandler);
        session.Feed([1, 2, 3]);
        Assert.Equal(3, session.PumpStep());
    }

    [Fact]
    public void Operations_After_Stop_Throw()
    {
        var session = McpNativeSession.Start(ReverseHandler);
        session.Stop();
        Assert.Throws<InvalidOperationException>(() => session.Feed([1]));
        Assert.Throws<InvalidOperationException>(() => session.PumpStep());
        Assert.Throws<InvalidOperationException>(() => session.Drain([]));
        Assert.Throws<InvalidOperationException>(() => session.DispatchProbe("echo", [1]));
        session.Dispose();   // idempotent
    }
}
