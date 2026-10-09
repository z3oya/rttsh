using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Toolbox.Tools.RttCli;

/// <summary>Native glue session for the Rust MCP crate (native/rttsh-mcp →
/// rttsh_mcp_native.dll); the declarations are the generated McpNativeGen.g.cs.
/// Conventions from the interop study: UTF-8, status-code errors, panics caught
/// inside the DLL (never Environment.Exit next to it - see Program's class doc).
/// Buffers are caller-allocated (feed/drain); the dispatch response is the one
/// handover - C# allocates it through <see cref="McpNativeGen.rttsh_mcp_alloc"/>
/// and Rust frees it.</summary>

/// <summary>Managed callback that executes one MCP tool call and returns its envelope.
/// Pure compute; the trampoline owns the dispatch gate, the envelope serialization, and
/// the native-allocation handover. One logical call crosses the boundary exactly once -
/// no retry, no caching - so non-idempotent tools (connect!) are safe by construction
/// and identical repeated calls always re-execute. Exceptions thrown here are caught by
/// the trampoline and surface as <see cref="McpNativeGen.ErrInternal"/>.</summary>
internal delegate McpEnvelope DispatchHandler(int handle, string method, ReadOnlySpan<byte> request);

/// <summary>The dispatch response envelope; serialized to {"ok":..,"text":..} for Rust.
/// ok=false makes rmcp report the text as an isError tool result - the message is the
/// evidence the model sees (Expect's timeout message carries the buffer tail, for one).</summary>
internal readonly record struct McpEnvelope(bool Ok, string Text);

/// <summary>One native glue session: the handle the Rust registry routes callbacks to, plus
/// the MCP byte pump (feed / wait / drain) between the process console and the in-DLL rmcp
/// server. The session lives exactly as long as this object is not disposed.</summary>
internal sealed class McpNativeSession : IDisposable
{
    /// <summary>Routes native callbacks to the owning session. Registered before any call
    /// that could dispatch (the Rust start spawns nothing yet; when the MCP layer gains a
    /// runtime, it must be started after registration — see Start).</summary>
    private static readonly ConcurrentDictionary<int, McpNativeSession> Registry = new();

    private readonly DispatchHandler _handler;
    private readonly byte[] _drainBuffer = new byte[64 * 1024];
    private readonly int _handle;
    private int _stopped;
    /// <summary>Serializes tool execution: the J-Link DLL is not thread-safe, and the host
    /// state (one transport, one runtime, one target lock) is single by design.</summary>
    private readonly object _dispatchGate = new();

    private McpNativeSession(int handle, DispatchHandler handler)
    {
        _handle = handle;
        _handler = handler;
    }

    /// <summary>Starts a native session; throws when the DLL is missing, ABI-mismatched,
    /// or the start call fails. <paramref name="handler"/> must be thread-safe once the
    /// MCP layer dispatches from its runtime threads.</summary>
    public static unsafe McpNativeSession Start(DispatchHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        int abi = McpNativeGen.rttsh_mcp_abi_version();
        if (abi != McpNativeGen.ABI_VERSION)
            throw new InvalidOperationException(
                $"rttsh_mcp_native.dll ABI mismatch: found v{abi}, expected v{McpNativeGen.ABI_VERSION} — rebuild native/ (cargo build --release)");

        int handle = McpNativeGen.rttsh_mcp_start(&DispatchTrampoline);
        if (handle <= 0)
            throw Error(handle, nameof(Start));
        var session = new McpNativeSession(handle, handler);
        Registry[handle] = session;
        return session;
    }

    public int Handle => _handle;

    /// <summary>Pushes bytes into the native inbound queue (client → server direction).</summary>
    public unsafe void Feed(byte[] data)
    {
        EnsureRunning();
        fixed (byte* dataPtr = NonNull(data))
            ThrowOnError(McpNativeGen.rttsh_mcp_feed(_handle, dataPtr, (uint)data.Length), nameof(Feed));
    }

    /// <summary>Single drain of up to <paramref name="buffer"/>'s length (server → client
    /// direction); returns the copied count, 0 when the queue is empty.</summary>
    public unsafe int Drain(byte[] buffer)
    {
        EnsureRunning();
        fixed (byte* bufPtr = NonNull(buffer))
        {
            int read = McpNativeGen.rttsh_mcp_drain(_handle, bufPtr, (uint)buffer.Length);
            if (read < 0)
                throw Error(read, nameof(Drain));
            return read;
        }
    }

    /// <summary>Blocks up to <paramref name="timeoutMs"/> for server output; returns the
    /// pending byte count (0 = timed out with nothing to drain).</summary>
    public int Wait(int timeoutMs)
    {
        EnsureRunning();
        int pending = McpNativeGen.rttsh_mcp_wait(_handle, (uint)timeoutMs);
        if (pending < 0)
            throw Error(pending, nameof(Wait));
        return pending;
    }

    /// <summary>Drains until the outbound queue is empty.</summary>
    public byte[] DrainAll()
    {
        EnsureRunning();
        using var buffer = new MemoryStream();
        int read;
        while ((read = Drain(_drainBuffer)) > 0)
            buffer.Write(_drainBuffer, 0, read);
        return buffer.ToArray();
    }

    /// <summary>Exercises the full dispatch glue without MCP: invokes the registered
    /// handler through the native callback and returns the response it produced.</summary>
    public unsafe byte[] DispatchProbe(string method, byte[] request)
    {
        EnsureRunning();
        byte[] methodBytes = Encoding.UTF8.GetBytes(method);
        // Success returns the response length (>= 0); only negatives are the error ladder.
        fixed (byte* methodPtr = NonNull(methodBytes), requestPtr = NonNull(request))
        {
            int length = McpNativeGen.rttsh_mcp_dispatch_probe(_handle, methodPtr, (uint)methodBytes.Length, requestPtr, (uint)request.Length);
            if (length < 0)
                throw Error(length, nameof(DispatchProbe));
        }
        return DrainAll();
    }

    /// <summary>Destroys the native session; idempotent. Later operations throw.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;
        Registry.TryRemove(_handle, out _);
        McpNativeGen.rttsh_mcp_stop(_handle);
    }

    public void Dispose() => Stop();

    private void EnsureRunning()
    {
        if (Volatile.Read(ref _stopped) != 0)
            throw new InvalidOperationException("the native session is stopped");
    }

    /// <summary>`fixed` on an empty array yields null; the DLL rejects null even
    /// at len 0 (it short-circuits the 0 length before any read).</summary>
    private static byte[] NonNull(byte[] bytes) => bytes.Length == 0 ? [0] : bytes;

    private static void ThrowOnError(int rc, string operation)
    {
        if (rc != McpNativeGen.OK)
            throw Error(rc, operation);
    }

    private static InvalidOperationException Error(int rc, string operation) => new(
        rc switch
        {
            McpNativeGen.ErrInvalidHandle => $"{operation}: native session handle rejected (stopped or unknown)",
            McpNativeGen.ErrBadArgument => $"{operation}: native call rejected a null or oversized argument",
            McpNativeGen.ErrInternal => $"{operation}: native internal error (panic caught inside rttsh_mcp_native.dll)",
            _ => $"{operation}: unknown native status {rc} (status ladder mismatch — rebuild native/)",
        });

    /// <summary>The Rust→C# trampoline: a static function pointer (per the interop study —
    /// never a delegate, which the native side could collect) routing through the registry.
    /// Execution + envelope serialization happen under the dispatch gate; the response is
    /// allocated with the native allocator and handed over by pointer, so one logical call
    /// crosses the boundary exactly once. Must never throw; exceptions map to
    /// <see cref="McpNativeGen.ErrInternal"/>.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int DispatchTrampoline(
        int handle,
        byte* method, uint methodLen,
        byte* request, uint requestLen,
        byte** responseOut, uint* responseLen)
    {
        *responseLen = 0;
        *responseOut = null;
        try
        {
            if (!Registry.TryGetValue(handle, out McpNativeSession? session))
                return McpNativeGen.ErrInvalidHandle;
            string methodText = Encoding.UTF8.GetString(method, checked((int)methodLen));
            byte[] response = session.Answer(handle, methodText, new ReadOnlySpan<byte>(request, checked((int)requestLen)).ToArray());
            if (response.Length == 0)
                return McpNativeGen.OK;
            byte* buffer = (byte*)McpNativeGen.rttsh_mcp_alloc((uint)response.Length);
            if (buffer is null)
                return McpNativeGen.ErrInternal;
            response.AsSpan().CopyTo(new Span<byte>(buffer, response.Length));
            *responseLen = (uint)response.Length;
            *responseOut = buffer;
            return McpNativeGen.OK;
        }
        catch (Exception ex)
        {
            // Diagnostics go to stderr (stdout belongs to the MCP stream once wired).
            Console.Error.WriteLine($"rttsh: native dispatch trampoline failed: {ex}");
            return McpNativeGen.ErrInternal;
        }
    }

    private byte[] Answer(int handle, string method, byte[] request)
    {
        lock (_dispatchGate)
        {
            return Serialize(_handler(handle, method, request));
        }
    }

    private static byte[] Serialize(McpEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(new { ok = envelope.Ok, text = envelope.Text });
}
