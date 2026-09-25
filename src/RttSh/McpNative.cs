using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Toolbox.Tools.RttCli;

/// <summary>P/Invoke surface of the Rust glue crate (native/rttsh-mcp → rttsh_mcp_native.dll,
/// built by the BuildRttshNative MSBuild target and shipped flat like lua54.dll). The crate
/// holds no MCP functionality yet: this is the C#↔Rust foundation the MCP server layer will
/// build on. Conventions from the interop study: [LibraryImport] source generation, UTF-8,
/// caller-allocated buffers, status-code errors, panics caught inside the DLL (never
/// Environment.Exit next to it — see Program's class doc).</summary>
internal static unsafe partial class McpNative
{
    private const string Library = "rttsh_mcp_native";

    /// <summary>Status ladder — mirrors native/rttsh-mcp/src/lib.rs.</summary>
    public const int Ok = 0;
    public const int ErrInvalidHandle = -1;
    public const int ErrBufferTooSmall = -2;
    public const int ErrBadArgument = -3;
    public const int ErrInternal = -4;   // panic caught inside the DLL

    /// <summary>Bumped on FFI shape changes; a mismatched DLL fails fast at Start.</summary>
    public const int AbiVersion = 1;

    [LibraryImport(Library, EntryPoint = "rttsh_mcp_abi_version")]
    internal static partial int RttshMcpAbiVersion();

    /// <summary>Type of the Rust→C# dispatch callback (mirrors <c>DispatchFn</c> in lib.rs):
    /// <c>extern "system"</c> on the Rust side, <c>unmanaged[Stdcall]</c> here.</summary>
    [LibraryImport(Library, EntryPoint = "rttsh_mcp_start")]
    internal static partial int RttshMcpStart(
        delegate* unmanaged[Stdcall]<int, byte*, uint, byte*, uint, byte*, uint, uint*, int> dispatch);

    [LibraryImport(Library, EntryPoint = "rttsh_mcp_stop")]
    internal static partial int RttshMcpStop(int handle);

    [LibraryImport(Library, EntryPoint = "rttsh_mcp_feed")]
    internal static partial int RttshMcpFeed(int handle, byte[] data, uint len);

    [LibraryImport(Library, EntryPoint = "rttsh_mcp_pump_step")]
    internal static partial int RttshMcpPumpStep(int handle);

    [LibraryImport(Library, EntryPoint = "rttsh_mcp_drain")]
    internal static partial int RttshMcpDrain(int handle, byte[] buffer, uint cap);

    [LibraryImport(Library, EntryPoint = "rttsh_mcp_dispatch_probe")]
    internal static partial int RttshMcpDispatchProbe(int handle, byte[] method, uint methodLen, byte[] request, uint requestLen);

    [LibraryImport(Library, EntryPoint = "rttsh_mcp_panic_probe")]
    internal static partial int RttshMcpPanicProbe();
}

/// <summary>Managed callback that executes one (future) tool call. The response buffer
/// follows the caller-buffer contract: write at most <paramref name="response"/>'s length
/// and report the written count via <paramref name="written"/>; when it does not fit,
/// return <see cref="McpNative.ErrBufferTooSmall"/> with <paramref name="written"/> set to
/// the required size — the Rust side retries with a grown buffer. Any exception thrown
/// here is caught by the trampoline and surfaces as <see cref="McpNative.ErrInternal"/>.</summary>
internal delegate int DispatchHandler(int handle, string method, ReadOnlySpan<byte> request, Span<byte> response, out int written);

/// <summary>One native glue session: the handle the Rust registry routes callbacks to, plus
/// the byte-pump calls (feed / pump-step / drain) the future MCP transport will drive. The
/// skeleton keeps a session alive only as long as this object is not disposed.</summary>
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
        int abi = McpNative.RttshMcpAbiVersion();
        if (abi != McpNative.AbiVersion)
            throw new InvalidOperationException(
                $"rttsh_mcp_native.dll ABI mismatch: found v{abi}, expected v{McpNative.AbiVersion} — rebuild native/ (cargo build --release)");

        int handle = McpNative.RttshMcpStart(&DispatchTrampoline);
        if (handle <= 0)
            throw Error(handle, nameof(Start));
        var session = new McpNativeSession(handle, handler);
        Registry[handle] = session;
        return session;
    }

    public int Handle => _handle;

    /// <summary>Pushes bytes into the native inbound queue (client → server direction).</summary>
    public void Feed(byte[] data)
    {
        EnsureRunning();
        ThrowOnError(McpNative.RttshMcpFeed(_handle, data, (uint)data.Length), nameof(Feed));
    }

    /// <summary>Moves the inbound queue to the outbound queue (the skeleton's transport
    /// stand-in); returns the moved byte count.</summary>
    public int PumpStep()
    {
        EnsureRunning();
        int moved = McpNative.RttshMcpPumpStep(_handle);
        if (moved < 0)
            throw Error(moved, nameof(PumpStep));
        return moved;
    }

    /// <summary>Single drain of up to <paramref name="buffer"/>'s length (server → client
    /// direction); returns the copied count, 0 when the queue is empty.</summary>
    public int Drain(byte[] buffer)
    {
        EnsureRunning();
        int read = McpNative.RttshMcpDrain(_handle, buffer, (uint)buffer.Length);
        if (read < 0)
            throw Error(read, nameof(Drain));
        return read;
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
    public byte[] DispatchProbe(string method, byte[] request)
    {
        EnsureRunning();
        byte[] methodBytes = Encoding.UTF8.GetBytes(method);
        // Success returns the response length (>= 0); only negatives are the error ladder.
        int length = McpNative.RttshMcpDispatchProbe(_handle, methodBytes, (uint)methodBytes.Length, request, (uint)request.Length);
        if (length < 0)
            throw Error(length, nameof(DispatchProbe));
        return DrainAll();
    }

    /// <summary>Destroys the native session; idempotent. Later operations throw.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;
        Registry.TryRemove(_handle, out _);
        McpNative.RttshMcpStop(_handle);
    }

    public void Dispose() => Stop();

    private void EnsureRunning()
    {
        if (Volatile.Read(ref _stopped) != 0)
            throw new InvalidOperationException("the native session is stopped");
    }

    private static void ThrowOnError(int rc, string operation)
    {
        if (rc != McpNative.Ok)
            throw Error(rc, operation);
    }

    private static InvalidOperationException Error(int rc, string operation) => new(
        rc switch
        {
            McpNative.ErrInvalidHandle => $"{operation}: native session handle rejected (stopped or unknown)",
            McpNative.ErrBufferTooSmall => $"{operation}: native response exceeds the size cap",
            McpNative.ErrBadArgument => $"{operation}: native call rejected a null or oversized argument",
            McpNative.ErrInternal => $"{operation}: native internal error (panic caught inside rttsh_mcp_native.dll)",
            _ => $"{operation}: unknown native status {rc} (status ladder mismatch — rebuild native/)",
        });

    /// <summary>The Rust→C# trampoline: a static function pointer (per the interop study —
    /// never a delegate, which the native side could collect) routing through the registry.
    /// Must stay allocation-light and never throw; exceptions map to ErrInternal.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int DispatchTrampoline(
        int handle,
        byte* method, uint methodLen,
        byte* request, uint requestLen,
        byte* resp, uint respCap, uint* respLen)
    {
        *respLen = 0;
        try
        {
            if (!Registry.TryGetValue(handle, out McpNativeSession? session))
                return McpNative.ErrInvalidHandle;
            int rc = session._handler(
                handle,
                Encoding.UTF8.GetString(method, checked((int)methodLen)),
                new ReadOnlySpan<byte>(request, checked((int)requestLen)),
                new Span<byte>(resp, checked((int)respCap)),
                out int written);
            if (rc == McpNative.Ok || rc == McpNative.ErrBufferTooSmall)
                *respLen = (uint)written;
            return rc;
        }
        catch (Exception ex)
        {
            // Diagnostics go to stderr (stdout belongs to the MCP stream once wired).
            Console.Error.WriteLine($"rttsh: native dispatch trampoline failed: {ex}");
            return McpNative.ErrInternal;
        }
    }
}
