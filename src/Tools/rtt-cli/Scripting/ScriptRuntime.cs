using System.Collections.Concurrent;
using System.Text;
using Toolbox.Core.Rtt;
using Toolbox.Core.SerialComm;

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
/// Construct before Open.</summary>
internal sealed class ScriptRuntime
{
    private readonly IRttTransport _transport;
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
        Action<string> logSink, int scriptTimeoutMs)
    {
        _transport = transport;
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
            throw new ScriptError($"expect: '{pattern}' not found within {timeoutMs} ms");
        }
        string result = region[..end.Value];
        _scanPos += end.Value;
        if (_scanPos >= 8192)
        {
            _pending.Remove(0, _scanPos);   // compact consumed history
            _scanPos = 0;
        }
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
