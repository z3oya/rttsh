using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Toolbox.Tools.RttCli.Scripting;

/// <summary>P/Invoke surface of the Lua hosting crate (native/rttsh-lua →
/// rttsh_lua_native.dll, built by the BuildRttshNative MSBuild target and shipped
/// flat like the other native DLLs). Conventions from McpNative/ElfNative:
/// [LibraryImport] source generation, UTF-8, status-code errors, panics caught
/// inside the DLL. The Lua state is single-owner and script-thread-confined —
/// create, every DoString/FindEnd and Dispose stay on the one script thread.
/// Strings cross as UTF-8 through exactly-sized allocations handed over by
/// pointer (allocated by the producing side, freed by the consuming one after
/// conversion — the MCP dispatch-response discipline, mirrored both ways).</summary>
internal static unsafe partial class LuaNative
{
    private const string Library = "rttsh_lua_native";

    /// <summary>Status ladder — mirrors native/rttsh-lua/src/lib.rs.</summary>
    internal const int Ok = 0;
    internal const int ErrInvalidHandle = -1;
    internal const int ErrBadArgument = -2;
    internal const int ErrInternal = -3;

    /// <summary>DoString's kind out — the chunk ran, failed with a message, or
    /// unwound on the __rtt_exit= sentinel. Only meaningful when the call
    /// itself returned <see cref="Ok"/>.</summary>
    internal const int KindOk = 0;
    internal const int KindError = 1;
    internal const int KindExit = 2;

    /// <summary>Bumped on FFI shape changes; a mismatched DLL fails fast at Start.</summary>
    internal const int AbiVersion = 2;

    [LibraryImport(Library, EntryPoint = "rttsh_lua_abi_version")]
    internal static partial int ProbeAbiVersion();

    /// <summary>Allocates exactly <paramref name="len"/> bytes with the native
    /// crate's allocator, for handovers the Rust side frees after conversion.</summary>
    [LibraryImport(Library, EntryPoint = "rttsh_lua_alloc")]
    internal static unsafe partial byte* Alloc(nuint len);

    [LibraryImport(Library, EntryPoint = "rttsh_lua_free")]
    internal static unsafe partial void Free(byte* ptr, nuint len);

    [LibraryImport(Library, EntryPoint = "rttsh_lua_create")]
    internal static unsafe partial nuint Create(void* ctx, HostVTable* vtable);

    [LibraryImport(Library, EntryPoint = "rttsh_lua_destroy")]
    internal static partial int Destroy(nuint handle);

    /// <summary>Loads and runs one named chunk; kind/code/message report the
    /// outcome (the message is a native allocation the caller frees).</summary>
    [LibraryImport(Library, EntryPoint = "rttsh_lua_do_string")]
    internal static unsafe partial int DoString(
        nuint handle, byte[] src, nuint srcLen, byte[] chunk, nuint chunkLen,
        int* kindOut, long* codeOut, byte** msgOut, nuint* msgLenOut);

    /// <summary>string.find(region, pattern, 1, false) on the state; endOut is
    /// 0 for no match, else the 1-based inclusive end (== chars to consume).</summary>
    [LibraryImport(Library, EntryPoint = "rttsh_lua_find")]
    internal static unsafe partial int Find(
        nuint handle, byte[] region, nuint regionLen, byte[] pattern, nuint patternLen,
        int* endOut, byte** msgOut, nuint* msgLenOut);
}

/// <summary>The C# host callbacks behind the shim's HOST_* globals — mirrors
/// <c>HostVTable</c> in native/rttsh-lua/src/lib.rs, field order included.
/// Every entry returns <see cref="HostVTable.CallbackOk"/>/<see cref="HostVTable.CallbackError"/>
/// (expect also <see cref="HostVTable.CallbackTimeout"/> for the soft timeout)
/// except Now (pure computation). Out-pairs carry either a value or, on
/// failure, the error message; the numeric args cross as 64-bit so the C#
/// side re-validates ranges (neither layer trusts the other's validation).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct HostVTable
{
    internal const int CallbackOk = 0;
    internal const int CallbackError = 1;

    /// <summary>The soft expect timeout: the out-pair carries the timeout
    /// message and the shim turns the pair into (nil, msg).</summary>
    internal const int CallbackTimeout = 2;

    internal void* Ctx;
    internal delegate* unmanaged[Stdcall]<void*, byte*, nuint, byte**, nuint*, int> Send;
    internal delegate* unmanaged[Stdcall]<void*, byte*, nuint, byte**, nuint*, int> SendHex;
    internal delegate* unmanaged[Stdcall]<void*, byte*, nuint, byte**, nuint*, int> Log;
    internal delegate* unmanaged[Stdcall]<void*, int, byte**, nuint*, int> Wait;
    internal delegate* unmanaged[Stdcall]<void*, int, byte**, nuint*, int> WaitHex;
    // flags: 0 = hard timeout (CB_ERR), 1 = soft timeout (CB_TIMEOUT with the message)
    internal delegate* unmanaged[Stdcall]<void*, byte*, nuint, int, int, byte**, nuint*, int> Expect;
    internal delegate* unmanaged[Stdcall]<void*, double> Now;
    internal delegate* unmanaged[Stdcall]<void*, int, byte**, nuint*, int> Sleep;
    internal delegate* unmanaged[Stdcall]<void*, long, byte**, nuint*, int> Exit;
    internal delegate* unmanaged[Stdcall]<void*, long, long, int, long**, nuint*, byte**, nuint*, int> MemRead;
    internal delegate* unmanaged[Stdcall]<void*, long, long, int, byte**, nuint*, int> MemWriteOne;
    internal delegate* unmanaged[Stdcall]<void*, long, long*, nuint, int, byte**, nuint*, int> MemWriteTable;
    internal delegate* unmanaged[Stdcall]<void*, int*, byte**, nuint*, int> IsHalted;
    internal delegate* unmanaged[Stdcall]<void*, byte**, nuint*, int> Halt;
    internal delegate* unmanaged[Stdcall]<void*, byte**, nuint*, int> Resume;
    internal delegate* unmanaged[Stdcall]<void*, byte**, nuint*, int> Flush;
    internal delegate* unmanaged[Stdcall]<void*, byte*, nuint, int, byte**, nuint*, int> ExpectAbsent;
    // patterns is `count` NativeByteSlices; index_out is the 0-based winner (i64 out like the mem channels)
    internal delegate* unmanaged[Stdcall]<void*, NativeByteSlice*, nuint, int, long*, byte**, nuint*, byte**, nuint*, int> ExpectAny;
}

/// <summary>One element of expect_any's pattern array ({ ptr, len } of a UTF-8
/// pattern), mirroring ByteSlice in native/rttsh-lua/src/lib.rs.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly unsafe struct NativeByteSlice
{
    internal readonly byte* Ptr;
    internal readonly nuint Len;
}

/// <summary>Per-run callback target: the runtime the rtt.* API drives, wrapped
/// so the static trampolines can route through the GCHandle registry. The mem
/// validation (Addr/Count/CheckUint) guards the i64 ABI channel here.</summary>
internal sealed class LuaHostContext
{
    /// <summary>Routes native callbacks to their session's context.</summary>
    private static readonly ConcurrentDictionary<IntPtr, LuaHostContext> Registry = new();

    internal ScriptRuntime Runtime { get; }
    internal nuint Handle { get; set; }
    private IntPtr Key { get; set; }

    private LuaHostContext(ScriptRuntime runtime) => Runtime = runtime;

    /// <summary>Registers the context and builds its vtable; call
    /// <see cref="Unregister"/> on teardown.</summary>
    internal static unsafe (LuaHostContext Context, HostVTable VTable) Register(ScriptRuntime runtime)
    {
        var context = new LuaHostContext(runtime);
        var gc = GCHandle.Alloc(context);
        IntPtr key = GCHandle.ToIntPtr(gc);
        Registry[key] = context;
        context.Key = key;
        return (context, BuildVTable((void*)key));
    }

    internal void Unregister()
    {
        Registry.TryRemove(Key, out _);
        ((GCHandle)Key).Free();
    }

    private static unsafe LuaHostContext Ctx(void* ctx) => Registry[(IntPtr)ctx];

    internal static unsafe HostVTable BuildVTable(void* ctx) => new()
    {
        Ctx = ctx,
        Send = &SendTrampoline,
        SendHex = &SendHexTrampoline,
        Log = &LogTrampoline,
        Wait = &WaitTrampoline,
        WaitHex = &WaitHexTrampoline,
        Expect = &ExpectTrampoline,
        Now = &NowTrampoline,
        Sleep = &SleepTrampoline,
        Exit = &ExitTrampoline,
        MemRead = &MemReadTrampoline,
        MemWriteOne = &MemWriteOneTrampoline,
        MemWriteTable = &MemWriteTableTrampoline,
        IsHalted = &IsHaltedTrampoline,
        Halt = &HaltTrampoline,
        Resume = &ResumeTrampoline,
        Flush = &FlushTrampoline,
        ExpectAbsent = &ExpectAbsentTrampoline,
        ExpectAny = &ExpectAnyTrampoline,
    };

    // ---- trampolines -------------------------------------------------------------------
    // Static function pointers (never delegates — the native side could collect them);
    // each pre-clears its out-pairs and maps CLR exceptions onto CallbackError with the
    // exception message handed over, so nothing throws across the FFI boundary.

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int SendTrampoline(void* ctx, byte* text, nuint len, byte** msgOut, nuint* msgLenOut)
    {
        *msgOut = null;
        *msgLenOut = 0;
        try
        {
            Ctx(ctx).Runtime.Send(Decode(text, len));
            return HostVTable.CallbackOk;
        }
        catch (Exception ex) { return Fail(msgOut, msgLenOut, ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int SendHexTrampoline(void* ctx, byte* hex, nuint len, byte** msgOut, nuint* msgLenOut)
    {
        *msgOut = null;
        *msgLenOut = 0;
        try
        {
            Ctx(ctx).Runtime.SendHex(Decode(hex, len));
            return HostVTable.CallbackOk;
        }
        catch (Exception ex) { return Fail(msgOut, msgLenOut, ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int LogTrampoline(void* ctx, byte* line, nuint len, byte** msgOut, nuint* msgLenOut)
    {
        *msgOut = null;
        *msgLenOut = 0;
        try
        {
            Ctx(ctx).Runtime.Log(Decode(line, len));
            return HostVTable.CallbackOk;
        }
        catch (Exception ex) { return Fail(msgOut, msgLenOut, ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int WaitTrampoline(void* ctx, int timeoutMs, byte** outPtr, nuint* outLen)
    {
        *outPtr = null;
        *outLen = 0;
        try
        {
            return Handover(outPtr, outLen, Ctx(ctx).Runtime.Wait(timeoutMs));
        }
        catch (Exception ex) { return Fail(outPtr, outLen, ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int WaitHexTrampoline(void* ctx, int timeoutMs, byte** outPtr, nuint* outLen)
    {
        *outPtr = null;
        *outLen = 0;
        try
        {
            return Handover(outPtr, outLen, Ctx(ctx).Runtime.WaitHex(timeoutMs));
        }
        catch (Exception ex) { return Fail(outPtr, outLen, ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int ExpectTrampoline(void* ctx, byte* pattern, nuint patternLen, int timeoutMs, int flags, byte** outPtr, nuint* outLen)
    {
        *outPtr = null;
        *outLen = 0;
        // the flags bit names the caller (expect vs try_expect) for messages
        string what = flags == 0 ? "expect" : "try_expect";
        try
        {
            return Handover(outPtr, outLen, Ctx(ctx).Runtime.Expect(Decode(pattern, patternLen), timeoutMs, throwOnTimeout: flags == 0, what));
        }
        catch (ScriptSoftTimeout ex) { return HandoverMessage(outPtr, outLen, ex.Message, HostVTable.CallbackTimeout); }
        catch (Exception ex) { return Fail(outPtr, outLen, ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int FlushTrampoline(void* ctx, byte** msgOut, nuint* msgLenOut)
    {
        *msgOut = null;
        *msgLenOut = 0;
        try
        {
            Ctx(ctx).Runtime.Flush();
            return HostVTable.CallbackOk;
        }
        catch (Exception ex) { return Fail(msgOut, msgLenOut, ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int ExpectAbsentTrampoline(void* ctx, byte* pattern, nuint patternLen, int timeoutMs, byte** msgOut, nuint* msgLenOut)
    {
        *msgOut = null;
        *msgLenOut = 0;
        try
        {
            Ctx(ctx).Runtime.ExpectAbsent(Decode(pattern, patternLen), timeoutMs);
            return HostVTable.CallbackOk;
        }
        catch (Exception ex) { return Fail(msgOut, msgLenOut, ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int ExpectAnyTrampoline(
        void* ctx, NativeByteSlice* patterns, nuint count, int timeoutMs, long* indexOut, byte** outPtr, nuint* outLen, byte** msgOut, nuint* msgLenOut)
    {
        *indexOut = -1;
        *outPtr = null;
        *outLen = 0;
        *msgOut = null;
        *msgLenOut = 0;
        try
        {
            int n = Count((long)count, "expect_any");
            var parsed = new string[n];
            ReadOnlySpan<NativeByteSlice> span = n > 0 ? new ReadOnlySpan<NativeByteSlice>(patterns, n) : default;
            for (int i = 0; i < n; i++)
                parsed[i] = Decode(span[i].Ptr, span[i].Len);
            (int index, string text) = Ctx(ctx).Runtime.ExpectAny(parsed, timeoutMs);
            *indexOut = index;
            return Handover(outPtr, outLen, text);
        }
        catch (Exception ex) { return Fail(msgOut, msgLenOut, ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe double NowTrampoline(void* ctx)
    {
        try
        {
            return Ctx(ctx).Runtime.Now();
        }
        catch
        {
            return 0; // Now is pure computation; the ladder has no failure shape here
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int SleepTrampoline(void* ctx, int ms, byte** msgOut, nuint* msgLenOut)
    {
        *msgOut = null;
        *msgLenOut = 0;
        try
        {
            Ctx(ctx).Runtime.Sleep(ms);
            return HostVTable.CallbackOk;
        }
        catch (Exception ex) { return Fail(msgOut, msgLenOut, ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int ExitTrampoline(void* ctx, long code, byte** msgOut, nuint* msgLenOut)
    {
        *msgOut = null;
        *msgLenOut = 0;
        try
        {
            Ctx(ctx).Runtime.Exit(checked((int)code));
            return HostVTable.CallbackOk; // unreachable: Exit always throws
        }
        catch (ScriptExitSignal)
        {
            // expected control flow: ExitCode is recorded, the shim raises the
            // __rtt_exit= sentinel right after this returns
            return HostVTable.CallbackOk;
        }
        catch (Exception ex) { return Fail(msgOut, msgLenOut, ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int MemReadTrampoline(
        void* ctx, long addr, long count, int width, long** outPtr, nuint* outLen, byte** msgOut, nuint* msgLenOut)
    {
        *outPtr = null;
        *outLen = 0;
        *msgOut = null;
        *msgLenOut = 0;
        try
        {
            uint[] values = Ctx(ctx).Runtime.MemRead(Addr(addr, "mem_read"), Count(count, "mem_read"), width);
            if (values.Length == 0)
                return HostVTable.CallbackOk;
            byte* buffer = LuaNative.Alloc((nuint)values.Length * 8);
            if (buffer is null)
                return Fail(msgOut, msgLenOut, new ScriptError("mem_read: failed to allocate the value handover"));
            var span = new Span<long>(buffer, values.Length);
            for (int i = 0; i < values.Length; i++)
                span[i] = values[i];   // Lua is 1-based; the table is built Rust-side
            *outPtr = (long*)buffer;
            *outLen = (nuint)values.Length;
            return HostVTable.CallbackOk;
        }
        catch (Exception ex) { return Fail(msgOut, msgLenOut, ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int MemWriteOneTrampoline(void* ctx, long addr, long value, int width, byte** msgOut, nuint* msgLenOut)
    {
        *msgOut = null;
        *msgLenOut = 0;
        try
        {
            Ctx(ctx).Runtime.MemWrite(Addr(addr, "mem_write"), [CheckUint(value, "mem_write", 1)], width);
            return HostVTable.CallbackOk;
        }
        catch (Exception ex) { return Fail(msgOut, msgLenOut, ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int MemWriteTableTrampoline(
        void* ctx, long addr, long* values, nuint count, int width, byte** msgOut, nuint* msgLenOut)
    {
        *msgOut = null;
        *msgLenOut = 0;
        try
        {
            int n = Count((long)count, "mem_write");
            var parsed = new uint[n];
            ReadOnlySpan<long> span = n > 0 ? new ReadOnlySpan<long>(values, n) : default;
            // The shim floor-built this copy and the Rust side handed it over as
            // i64s, so integer-ness is guaranteed by the channel; what remains is
            // the 32-bit range check C# owns.
            for (int i = 0; i < n; i++)
                parsed[i] = CheckUint(span[i], "mem_write", i + 1);
            Ctx(ctx).Runtime.MemWrite(Addr(addr, "mem_write"), parsed, width);
            return HostVTable.CallbackOk;
        }
        catch (Exception ex) { return Fail(msgOut, msgLenOut, ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int IsHaltedTrampoline(void* ctx, int* outHalted, byte** msgOut, nuint* msgLenOut)
    {
        *outHalted = 0;
        *msgOut = null;
        *msgLenOut = 0;
        try
        {
            *outHalted = Ctx(ctx).Runtime.IsHalted() ? 1 : 0;
            return HostVTable.CallbackOk;
        }
        catch (Exception ex) { return Fail(msgOut, msgLenOut, ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int HaltTrampoline(void* ctx, byte** msgOut, nuint* msgLenOut)
    {
        *msgOut = null;
        *msgLenOut = 0;
        try
        {
            Ctx(ctx).Runtime.Halt();
            return HostVTable.CallbackOk;
        }
        catch (Exception ex) { return Fail(msgOut, msgLenOut, ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int ResumeTrampoline(void* ctx, byte** msgOut, nuint* msgLenOut)
    {
        *msgOut = null;
        *msgLenOut = 0;
        try
        {
            Ctx(ctx).Runtime.Resume();
            return HostVTable.CallbackOk;
        }
        catch (Exception ex) { return Fail(msgOut, msgLenOut, ex); }
    }

    // ---- trampoline helpers -------------------------------------------------------------

    private static unsafe string Decode(byte* ptr, nuint len) => ptr is null
        ? string.Empty
        : Encoding.UTF8.GetString(ptr, checked((int)len));

    /// <summary>Hands a string value over (empty hands over null/0, which the
    /// Rust side converts back to the Lua empty string) and reports CallbackOk.</summary>
    private static unsafe int Handover(byte** outPtr, nuint* outLen, string text)
    {
        if (text.Length == 0)
            return HostVTable.CallbackOk;
        int len = Encoding.UTF8.GetByteCount(text);
        byte* buffer = LuaNative.Alloc((nuint)len);
        if (buffer is null)
            return HostVTable.CallbackError; // degenerate to an empty value
        CopyUtf8(buffer, text, len);
        *outPtr = buffer;
        *outLen = (nuint)len;
        return HostVTable.CallbackOk;
    }

    /// <summary>Hands the exception message over as the callback's failure
    /// message and reports CallbackError.</summary>
    private static unsafe int Fail(byte** msgOut, nuint* msgLenOut, Exception error) =>
        HandoverMessage(msgOut, msgLenOut, error.Message, HostVTable.CallbackError);

    /// <summary>Hands a message over with an explicit status - the failure shape
    /// (CallbackError) and the soft-timeout shape (CallbackTimeout, whose out-pair
    /// carries the timeout message) share this path.</summary>
    private static unsafe int HandoverMessage(byte** outPtr, nuint* outLen, string message, int status)
    {
        int len = Encoding.UTF8.GetByteCount(message);
        byte* buffer = len == 0 ? null : LuaNative.Alloc((nuint)len);
        if (buffer is null)
            return status; // the message degrades to empty
        CopyUtf8(buffer, message, len);
        *outPtr = buffer;
        *outLen = (nuint)len;
        return status;
    }

    private static unsafe void CopyUtf8(byte* buffer, string text, int len)
    {
        fixed (char* chars = text)
        {
            Encoding.UTF8.GetBytes(chars, text.Length, buffer, len);
        }
    }

    // ---- the 32-bit range checks ---------------------------------------------------------

    private static uint Addr(long value, string what) =>
        value is >= 0 and <= uint.MaxValue
            ? (uint)value
            : throw new ScriptError($"{what}: address {value} is out of the 32-bit range");

    private static int Count(long value, string what) =>
        value is >= 0 and <= int.MaxValue
            ? (int)value
            : throw new ScriptError($"{what}: count must be between 0 and {int.MaxValue}");

    private static uint CheckUint(long value, string what, long index) =>
        value is >= 0 and <= uint.MaxValue
            ? (uint)value
            : throw new ScriptError($"{what}: value #{index} ({value}) is not an unsigned 32-bit integer");
}

/// <summary>One native Lua session: registers the callback context, builds the
/// vtable and creates the state. The shim and the script run through
/// <see cref="DoString"/> (shim first, script second — one call each, per
/// Run); <see cref="FindEnd"/> is the PatternMatcher entry. Dispose destroys
/// the state and unregisters the context; idempotent, later calls throw.</summary>
internal sealed unsafe class LuaNativeSession : IDisposable
{
    private readonly LuaHostContext _context;
    private readonly HostVTable* _vtable;   // in native memory: the Rust side keeps this pointer for the state's lifetime
    private int _disposed;

    private LuaNativeSession(LuaHostContext context, HostVTable* vtable)
    {
        _context = context;
        _vtable = vtable;
    }

    /// <summary>Creates the session; throws when the DLL is missing, ABI-mismatched,
    /// or the create call fails.</summary>
    public static LuaNativeSession Start(ScriptRuntime runtime)
    {
        int abi = LuaNative.ProbeAbiVersion();
        if (abi != LuaNative.AbiVersion)
            throw new InvalidOperationException(
                $"rttsh_lua_native.dll ABI mismatch: found v{abi}, expected v{LuaNative.AbiVersion} — rebuild native/ (cargo build --release)");

        (var context, var vtable) = LuaHostContext.Register(runtime);
        // The Rust closures keep the vtable pointer for the state's lifetime, so the
        // struct lives in native memory - a stack or GC copy would move or die under it.
        nuint size = (nuint)sizeof(HostVTable);
        HostVTable* native = (HostVTable*)LuaNative.Alloc(size);
        if (native is null)
        {
            context.Unregister();
            throw new InvalidOperationException("rttsh_lua_create failed: could not allocate the vtable handover");
        }
        *native = vtable;
        nuint handle = LuaNative.Create(native->Ctx, native);
        if (handle == 0)
        {
            LuaNative.Free((byte*)native, size);
            context.Unregister();
            throw new InvalidOperationException("rttsh_lua_create failed (state creation or trampoline registration error inside rttsh_lua_native.dll)");
        }
        context.Handle = handle;
        return new LuaNativeSession(context, native);
    }

    public nuint Handle => _context.Handle;

    /// <summary>Runs one named chunk ("@path" / "=eval" conventions) and
    /// returns (kind, exit code, message). The message is non-null only for
    /// failures; the exit sentinel reports its code through <paramref name="code"/>.</summary>
    public unsafe (int Kind, long Code, string? Message) DoString(string source, string chunkName)
    {
        EnsureAlive();
        byte[] src = Encoding.UTF8.GetBytes(source);
        byte[] chunk = Encoding.UTF8.GetBytes(chunkName);
        int kind = 0;
        long code = 0;
        byte* msg = null;
        nuint msgLen = 0;
        int rc = LuaNative.DoString(_context.Handle, src, (nuint)src.Length, chunk, (nuint)chunk.Length, &kind, &code, &msg, &msgLen);
        if (rc != LuaNative.Ok)
            throw new InvalidOperationException($"rttsh_lua_do_string failed (status {rc})");
        return (kind, code, msg is null ? null : TakeStringAndFree(msg, msgLen));
    }

    /// <summary>The PatternMatcher entry: the 0-based end of the match (==
    /// chars to consume), or null when absent. what names the calling rtt.*
    /// entry so a malformed pattern's error points at the name the script wrote.</summary>
    public unsafe int? FindEnd(string region, string pattern, string what)
    {
        EnsureAlive();
        byte[] regionBytes = Encoding.UTF8.GetBytes(region);
        byte[] patternBytes = Encoding.UTF8.GetBytes(pattern);
        int end = 0;
        byte* msg = null;
        nuint msgLen = 0;
        int rc = LuaNative.Find(_context.Handle, regionBytes, (nuint)regionBytes.Length, patternBytes, (nuint)patternBytes.Length, &end, &msg, &msgLen);
        if (rc == HostVTable.CallbackError)
            throw new ScriptError($"{what}: {TakeStringAndFree(msg, msgLen)}");
        if (rc != LuaNative.Ok)
            throw new InvalidOperationException($"rttsh_lua_find failed (status {rc})");
        return end <= 0 ? null : end;
    }

    /// <summary>Destroys the state; idempotent. Later operations throw.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_context.Handle != 0)
            LuaNative.Destroy(_context.Handle);
        LuaNative.Free((byte*)_vtable, (nuint)sizeof(HostVTable));
        _context.Unregister();
    }

    private void EnsureAlive()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new InvalidOperationException("the native Lua session is disposed");
    }

    private static unsafe string TakeStringAndFree(byte* ptr, nuint len)
    {
        string text = ptr is null ? string.Empty : Encoding.UTF8.GetString(ptr, checked((int)len));
        LuaNative.Free(ptr, len);
        return text;
    }
}
