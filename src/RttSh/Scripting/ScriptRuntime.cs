using System.Collections.Concurrent;
using System.Text;
using RttSh.Core.Rtt;
using RttSh.Core.SerialComm;

namespace Toolbox.Tools.RttCli.Scripting;

/// <summary>Engine-free state machine behind script automation: transport events arrive on any
/// thread; every rtt.* operation runs on the single script thread (NLua's state is not
/// thread-safe either - see LuaScriptHost). Internally the RX stream is viewed as Latin-1 text
/// (1 byte = 1 char) so pattern matching is byte-exact for ASCII; the NLua boundary re-encodes
/// strings, so scripts that must see raw bytes use WaitHex instead of Wait.
///
/// Semantics: Wait is a passive tap (returns the bytes that arrived during the call, consumes
/// nothing); Expect is the consuming matcher (scans everything since the last match, consumes
/// through the match end). Cancellation/link failure wake every pump; action APIs throw, Wait
/// returns what it has. _pending grows unbounded while a script never matches - scripts should
/// expect() promptly. Lifetime: exactly one runtime per transport; there is no unsubscribe.
/// Construct before Open.
///
/// The optional ITargetMemory backs rtt.mem_read/mem_write/is_halted/halt/resume; null (tests,
/// or a transport without memory access) makes those throw a clear ScriptError. Like every
/// rtt.* operation, mem/halt calls run on the single script thread only.</summary>
internal sealed class ScriptRuntime
{
    /// <summary>One mem_read/mem_write call moves at most 1 MiB - a runaway count must fail
    /// fast instead of hanging the link in a huge transfer.</summary>
    internal const int MaxMemBytes = 1024 * 1024;

    /// <summary>Consumed history is compacted once it grows past this many chars (Expect and
    /// ReadAvailable share the buffer).</summary>
    private const int CompactionThreshold = 8192;

    private readonly IRttTransport _transport;
    private readonly ITargetMemory? _memory;
    private readonly Encoding _sendEncoding;
    private readonly byte[] _eolBytes;
    private readonly Action<string> _logSink;
    private readonly int _scriptTimeoutMs;
    private readonly long _startedTicks = Environment.TickCount64;
    private readonly ConcurrentQueue<byte[]> _inbox = new();
    private readonly SemaphoreSlim _dataSignal = new(0);
    private readonly SemaphoreSlim _cancelSignal = new(0);
    private readonly WaitHandle[] _pumpHandles;
    private readonly StringBuilder _pending = new();
    private int _scanPos;
    private volatile bool _cancelRequested;
    private volatile Exception? _linkError;

    public ScriptRuntime(IRttTransport transport, Encoding sendEncoding, byte[] eolBytes,
        Action<string> logSink, int scriptTimeoutMs, ITargetMemory? memory = null)
    {
        _transport = transport;
        _memory = memory;
        _sendEncoding = sendEncoding;
        _eolBytes = eolBytes;
        _logSink = logSink;
        _scriptTimeoutMs = scriptTimeoutMs;
        _pumpHandles = [_dataSignal.AvailableWaitHandle, _cancelSignal.AvailableWaitHandle];
        // The runtime owns its wiring: callers never subscribe - a missed subscription would
        // surface as scripts that never see data, and a double one as duplicated chunks.
        transport.DataReceived += OnData;
        transport.Error += OnLinkError;
    }

    public int ExitCode { get; private set; }

    // ---- event side (any thread) ------------------------------------------------------

    public void OnData(byte[] data)
    {
        _inbox.Enqueue(data);
        _dataSignal.Release();
    }

    public void OnLinkError(Exception error)
    {
        _linkError = error;
        _cancelSignal.Release();
    }

    public void RequestCancel()
    {
        _cancelRequested = true;
        _cancelSignal.Release();
    }

    // ---- script side (the single script thread only) -----------------------------------

    public void Log(string message) => _logSink(message);

    public double Now() => Environment.TickCount64 - _startedTicks;

    public void Send(string text) => SendBytes([.. _sendEncoding.GetBytes(text), .. _eolBytes], "send");

    public void SendHex(string hex)
    {
        if (!HexCodec.TryParse(hex, out byte[] bytes, out string? error))
            throw new ScriptError($"send_hex: {error}");
        SendBytes(bytes, "send_hex");
    }

    public string Wait(int timeoutMs)
    {
        ThrowLinkError();
        CheckWatchdog();
        int before = _pending.Length;
        PumpUntil(() => _pending.Length > before, timeoutMs);
        return _pending.ToString(before, _pending.Length - before);
    }

    /// <summary>Lossless binary variant of Wait: the same window of newly arrived bytes rendered
    /// as hex text. Hex crosses the NLua string boundary as pure ASCII, so this is the
    /// byte-exact path - Wait/Expect strings are re-encoded by the binding.</summary>
    public string WaitHex(int timeoutMs) => HexCodec.Format(Encoding.Latin1.GetBytes(Wait(timeoutMs)));

    /// <summary>The MCP read: everything received but not yet consumed (the region Expect
    /// scans), consumed through the end so the next read/expect starts after it. Terminal
    /// semantics: output already pending is returned at once (a quiet target must not tax
    /// every poll the full timeout); when nothing is pending the call waits up to timeoutMs
    /// for the first bytes, so send + read picks up the reply without polling. Returns ""
    /// when nothing arrived. The consuming sibling of Wait - Wait taps without consuming
    /// (the Lua semantic).</summary>
    public string ReadAvailable(int timeoutMs)
    {
        ThrowLinkError();
        CheckWatchdog();
        DrainInbox();   // ingest whatever the transport has already delivered
        if (_scanPos >= _pending.Length)
        {
            int before = _pending.Length;
            PumpUntil(() => _pending.Length > before, timeoutMs);
        }
        string text = Region();
        ConsumeThrough(_pending.Length);
        return text;
    }

    public string Expect(string pattern, int timeoutMs)
    {
        ThrowLinkError();
        CheckWatchdog();
        if (pattern.Length == 0) throw new ScriptError("expect: empty pattern");
        PumpUntil(() => MatchEnd(Region(), pattern) is not null, timeoutMs);
        string region = Region();
        int? end = MatchEnd(region, pattern);
        if (end is null)
        {
            if (_linkError is not null) throw new ScriptError($"link failed: {_linkError.Message}");
            if (_cancelRequested) throw new ScriptError($"expect: cancelled before '{pattern}' arrived");
            throw new ScriptError($"expect: '{pattern}' not found within {timeoutMs} ms; buffer tail: \"{DescribeTail()}\"");
        }
        string result = region[..end.Value];
        ConsumeThrough(_scanPos + end.Value);
        return result;
    }

    public void Sleep(int ms)
    {
        ThrowLinkError();
        CheckWatchdog();
        PumpUntil(() => false, ms);
        CheckWatchdog();   // Sleep is one long call - its return is the boundary that can observe the excess
    }

    public void Exit(int code)
    {
        ExitCode = code;
        throw new ScriptExitSignal(code);
    }

    // ---- memory & core control (the rtt.mem_* / halt API, single script thread only) ------
    // Widths follow the J-Link SDK convention in bytes (1/2/4); values cross into Lua as
    // plain numbers via LuaScriptHost, so no string-encoding concern applies here.

    /// <summary>Reads <paramref name="count"/> units of <paramref name="width"/> bits at
    /// <paramref name="address"/>, little-endian-decoded. One call moves at most
    /// <see cref="MaxMemBytes"/> bytes; width-sensitive access requires an aligned address.</summary>
    public uint[] MemRead(uint address, int count, int width)
    {
        int unit = MemUnit(width, "mem_read");
        if (count < 0) throw new ScriptError("mem_read: count must be >= 0");
        if ((long)count * unit > MaxMemBytes)
            throw new ScriptError($"mem_read: {count} units of {unit} byte(s) exceed the {MaxMemBytes}-byte cap - split the read");
        if (unit > 1 && address % (uint)unit != 0)
            throw new ScriptError($"mem_read: address 0x{address:X} is not {unit}-byte aligned (width {width})");
        if (count == 0) return [];

        ITargetMemory memory = MemoryOrThrow("mem_read");
        var buffer = new byte[count * unit];
        int got = CallMemory(() => memory.ReadMemory(address, (uint)buffer.Length, buffer, (uint)unit), "mem_read", address);
        if (got < buffer.Length)
            throw new ScriptError($"mem_read: only {got} of {buffer.Length} bytes read at 0x{address:X}");
        var values = new uint[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = unit switch
            {
                1 => buffer[i],
                2 => (uint)(buffer[2 * i] | buffer[2 * i + 1] << 8),
                _ => (uint)(buffer[4 * i] | buffer[4 * i + 1] << 8 | buffer[4 * i + 2] << 16 | buffer[4 * i + 3] << 24),
            };
        }
        return values;
    }

    /// <summary>Writes <paramref name="values"/> as units of <paramref name="width"/> bits,
    /// little-endian-encoded; every value must fit the width.</summary>
    public void MemWrite(uint address, uint[] values, int width)
    {
        int unit = MemUnit(width, "mem_write");
        if (values.Length == 0) return;
        if ((long)values.Length * unit > MaxMemBytes)
            throw new ScriptError($"mem_write: {values.Length} units of {unit} byte(s) exceed the {MaxMemBytes}-byte cap - split the write");
        if (unit > 1 && address % (uint)unit != 0)
            throw new ScriptError($"mem_write: address 0x{address:X} is not {unit}-byte aligned (width {width})");
        uint max = unit switch { 1 => byte.MaxValue, 2 => ushort.MaxValue, _ => uint.MaxValue };
        foreach (uint value in values)
        {
            if (value > max)
                throw new ScriptError($"mem_write: value 0x{value:X} does not fit {width} bits");
        }

        ITargetMemory memory = MemoryOrThrow("mem_write");
        var buffer = new byte[values.Length * unit];
        for (int i = 0; i < values.Length; i++)
        {
            uint value = values[i];
            for (int b = 0; b < unit; b++)
                buffer[i * unit + b] = (byte)(value >> (8 * b));
        }
        int written = CallMemory(() => memory.WriteMemory(address, (uint)buffer.Length, buffer, (uint)unit), "mem_write", address);
        if (written < values.Length)
            throw new ScriptError($"mem_write: only {written} of {values.Length} units written at 0x{address:X}");
    }

    public bool IsHalted()
    {
        ITargetMemory memory = MemoryOrThrow("is_halted");
        return RunControl(memory.IsHalted, "is_halted");
    }

    public void Halt()
    {
        ITargetMemory memory = MemoryOrThrow("halt");
        RunControl(memory.Halt, "halt");
    }

    public void Resume()
    {
        ITargetMemory memory = MemoryOrThrow("resume");
        RunControl(memory.Resume, "resume");
    }

    /// <summary>The access width in bytes for a bit width, or a ScriptError naming the API.</summary>
    private static int MemUnit(int width, string what) => width switch
    {
        8 => 1,
        16 => 2,
        32 => 4,
        _ => throw new ScriptError($"{what}: width must be 8, 16 or 32 bits, got {width}"),
    };

    private ITargetMemory MemoryOrThrow(string what) =>
        _memory ?? throw new ScriptError($"{what}: this session has no memory access (mem_*/halt need the J-Link transport)");

    /// <summary>Runs a core-control call and wraps transport-level failures as a ScriptError
    /// naming the API - the no-code sibling of <see cref="CallMemory"/>'s try/catch.</summary>
    private static T RunControl<T>(Func<T> call, string what)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            throw new ScriptError($"{what} failed: {ex.Message}");
        }
    }

    private static void RunControl(Action call, string what) =>
        RunControl<object?>(() => { call(); return null; }, what);

    private static int CallMemory(Func<int> call, string what, uint address)
    {
        int code;
        try
        {
            code = call();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            throw new ScriptError($"{what} failed at 0x{address:X}: {ex.Message}");
        }
        if (code < 0)
            throw new ScriptError($"{what} failed at 0x{address:X} (code={code}: {JLinkErrors.Describe(code)})");
        return code;
    }

    // ---- internals ---------------------------------------------------------------------

    private void SendBytes(byte[] payload, string what)
    {
        ThrowLinkError();
        CheckWatchdog();
        try
        {
            _transport.Write(payload);
        }
        catch (Exception ex)
        {
            throw new ScriptError($"{what} failed: {ex.Message}");
        }
    }

    private string Region() => _pending.ToString(_scanPos, _pending.Length - _scanPos);

    /// <summary>Marks everything through absolute offset <paramref name="end"/> consumed,
    /// compacting the buffer once the consumed history grows past CompactionThreshold
    /// (the one compaction rule, shared by Expect and ReadAvailable).</summary>
    private void ConsumeThrough(int end)
    {
        _scanPos = end;
        if (_scanPos >= CompactionThreshold)
        {
            _pending.Remove(0, _scanPos);   // compact consumed history
            _scanPos = 0;
        }
    }

    /// <summary>Last ≤80 chars of the pending buffer, control characters and backslashes escaped -
    /// escaping the introducer too keeps this invertible, so a literal "\n" can never be mistaken
    /// for a real newline. "What did the pending buffer actually contain" is the first thing a
    /// failing pattern needs.</summary>
    private string DescribeTail()
    {
        int start = Math.Max(0, _pending.Length - 80);
        string tail = _pending.ToString(start, _pending.Length - start);
        var sb = new StringBuilder(tail.Length + 16);
        foreach (char c in tail)
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;   // the escape introducer itself, handled first
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '"': sb.Append("\\\""); break;
                default:
                    if (c < ' ' || c == '\x7f') sb.Append("\\x").Append(((int)c).ToString("x2"));
                    else sb.Append(c);
                    break;
            }
        return sb.ToString();
    }

    /// <summary>Locates <paramref name="pattern"/> in the scan region. null = the default
    /// (engine-free) literal ordinal search; LuaScriptHost installs Lua's own matcher here so
    /// expect() honors full Lua pattern syntax. Only touched from the script thread - same
    /// thread that owns the Lua state (see LuaScriptHost).</summary>
    public Func<string, string, int?>? PatternMatcher { get; set; }

    /// <summary>The 0-based end of the match (== chars to consume), or null when absent.</summary>
    private int? MatchEnd(string region, string pattern)
    {
        if (PatternMatcher is null)
        {
            int idx = region.IndexOf(pattern, StringComparison.Ordinal);
            return idx < 0 ? null : idx + pattern.Length;
        }
        return PatternMatcher(region, pattern);
    }

    /// <summary>Pumps events until the predicate holds, the timeout elapses, the link fails or
    /// cancellation is requested. Wakes on data or cancel signals.</summary>
    private bool PumpUntil(Func<bool> done, int timeoutMs)
    {
        long start = Environment.TickCount64;
        while (true)
        {
            DrainInbox();
            if (done()) return true;
            if (_cancelRequested || _linkError is not null) return false;
            int elapsed = (int)(Environment.TickCount64 - start);
            if (elapsed >= timeoutMs) return false;
            int slice = Math.Min(timeoutMs - elapsed, 20);
            WaitHandle.WaitAny(_pumpHandles, slice);
        }
    }

    private void DrainInbox()
    {
        while (_inbox.TryDequeue(out byte[]? chunk))
            _pending.Append(Encoding.Latin1.GetString(chunk));
    }

    private void ThrowLinkError()
    {
        if (_linkError is not null)
            throw new ScriptError($"link failed: {_linkError.Message}");
    }

    private void CheckWatchdog()
    {
        if (_scriptTimeoutMs > 0 && Environment.TickCount64 - _startedTicks > _scriptTimeoutMs)
            throw new ScriptTimeoutError($"script exceeded {_scriptTimeoutMs} ms (--script-timeout)");
    }
}
