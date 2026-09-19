using System.Runtime.InteropServices;
using Toolbox.Core.Rtt;

namespace Toolbox.Tools.RttCli;

/// <summary>IRttTransport over the SEGGER J-Link DLL, RTT channel 0 (mirrors the reference rtt-t2 tool).
///
/// Threading: the DLL is NOT thread-safe; the reference tool gets serialization for free from Qt's single
/// UI thread. Here every native call runs under _nativeLock. The poll thread owns the failure path: on a
/// negative read it performs the DLL teardown itself and then raises Error - an external Close() that raced
/// it just sees the link already closed (idempotent). Close() stops the poll thread by clearing _open, joins
/// it OUTSIDE the lock (it may be inside a read), then tears down.</summary>
internal sealed class JLinkRttTransport : IRttTransport
{
    private const int ChannelIndex = 0;
    private const int PollBytes = 8192;
    private const int PollIdleMs = 2;

    private readonly JLinkLibrary _lib = new();
    private readonly object _nativeLock = new();
    private readonly byte[] _rxBuffer = new byte[PollBytes];
    private Thread? _pollThread;
    private volatile bool _open;

    // OpenEx invokes these line-by-line on DLL-internal threads; instance fields keep the delegates
    // alive for the DLL's lifetime and carry no process-global state (unlike the reference C++ thunk).
    private JLinkNative.LogFn? _logThunk;
    private JLinkNative.LogFn? _errorThunk;

    public bool IsOpen => _open;

    /// <summary>Raised on a DLL thread for each J-Link log/error line (connection progress, RTT scans).</summary>
    public event Action<string>? LogLine;

    public event Action<byte[]>? DataReceived;
    public event Action<Exception>? Error;

    public void Open(RttConnectionConfig config)
    {
        if (string.IsNullOrEmpty(config.Chip))
            throw new IOException("No target device specified.");
        config = config.Clamped();

        lock (_nativeLock)
        {
            if (_open) throw new IOException("The RTT link is already open.");

            if (!_lib.Load(config.DllPath, out string loadError))
                throw new IOException(loadError);

            bool dllOpened = false;
            try
            {
                // Probe selection first, then OpenEx - same order as the reference tool.
                if (config.SerialNo > 0)
                {
                    if (_lib.EmuSelectByUsbSn!(config.SerialNo) < 0)
                        throw new IOException($"J-Link with serial {config.SerialNo} not found.");
                }
                else if (_lib.SelectUsb!(0) != 0)
                {
                    throw new IOException("No default J-Link probe found.");
                }

                _logThunk = OnDllLog;
                _errorThunk = OnDllError;
                IntPtr openError = _lib.OpenEx!(_logThunk, _errorThunk);
                if (openError != IntPtr.Zero)
                    throw new IOException($"JLINKARM_OpenEx: {Marshal.PtrToStringAnsi(openError) ?? "unknown error"}");
                dllOpened = true;

                _lib.TifSelect!(config.Interface == RttInterface.Jtag ? JLinkNative.TifJtag : JLinkNative.TifSwd);
                _lib.SetSpeed!(config.SpeedKhz);

                var execError = new byte[256];
                _lib.ExecCommand!($"device = {config.Chip}", execError, execError.Length);
                string execMessage = JLinkLibrary.TrimAtNul(execError);
                if (execMessage.Length > 0)
                    throw new IOException($"ExecCommand(device = {config.Chip}): {execMessage}");

                if (_lib.IsConnected!() == 0 && _lib.Connect!() < 0)
                    throw new IOException($"Failed to connect target (chip={config.Chip}).");

                if (config.ResetOnConnect)
                    _lib.Reset!();

                StartRtt(config);
            }
            catch
            {
                if (dllOpened) _lib.Close!();   // reset the DLL so a retry starts clean
                throw;
            }

            _open = true;
            _pollThread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "rtt-poll",
            };
            _pollThread.Start();
        }
    }

    /// <summary>RTT START with either the SDK auto-scan or the manually located control block.</summary>
    private void StartRtt(RttConnectionConfig config)
    {
        uint address = config.RttAddress;
        if (address != 0 && config.RttRange > 0)
        {
            uint hit = ScanRttAddress(address, config.RttRange);
            if (hit != 0) address = hit;
        }

        var start = new JLinkNative.RttStartConfig(address);   // 0 = SDK scans RAM for _SEGGER RTT
        if (_lib.RttControlStart!(JLinkNative.RttCmdStart, ref start) < 0)
            throw new IOException($"Failed to start RTT (addr=0x{address:X}). If the block is not auto-detected, pass --rtt-addr and --rtt-range.");
    }

    /// <summary>Reads the whole window in one call and locates the "SEGGER RTT" signature (pure-logic
    /// half lives in RttControlBlock.FindSignature and is unit-tested).</summary>
    private uint ScanRttAddress(uint start, uint range)
    {
        var dump = new byte[range];
        int got = _lib.ReadMemEx!(start, range, dump, JLinkNative.Access8);
        if (got <= 0) return 0;
        int index = RttControlBlock.FindSignature(dump.AsSpan(0, got));
        return index < 0 ? 0 : start + (uint)index;
    }

    /// <summary>Polls channel 0. A positive read loops immediately (no timer jitter at high rates);
    /// an empty read idles ~2ms; a negative read fails the link.</summary>
    private void PollLoop()
    {
        while (true)
        {
            int read;
            lock (_nativeLock)
            {
                if (!_open) return;   // external Close() in progress
                read = _lib.RttRead!(ChannelIndex, _rxBuffer, PollBytes);
            }

            if (read > 0)
            {
                var data = new byte[read];
                Array.Copy(_rxBuffer, data, read);
                DataReceived?.Invoke(data);
            }
            else if (read < 0)
            {
                lock (_nativeLock)
                {
                    if (!_open) return;   // external Close() won the race; it does the teardown
                    _open = false;
                    TeardownDll();
                }
                Error?.Invoke(new IOException($"RTT read error (code={read})."));
                return;
            }
            else
            {
                Thread.Sleep(PollIdleMs);
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        var buffer = data.ToArray();   // native delegates take managed arrays
        lock (_nativeLock)
        {
            if (!_open) throw new IOException("The RTT link is not open.");
            int written = _lib.RttWrite!(ChannelIndex, buffer, buffer.Length);
            if (written < 0)
                throw new IOException($"RTT write failed (code={written}).");
            if (written < buffer.Length)
                OnLog($"RTT down-buffer full: wrote {written}/{buffer.Length} bytes", isError: true);
        }
    }

    /// <summary>Idempotent. Clears the flag, joins the poll thread outside the lock (it may be inside a
    /// read), then stops RTT and closes the DLL.</summary>
    public void Close()
    {
        Thread? thread;
        lock (_nativeLock)
        {
            if (!_open) return;
            _open = false;
            thread = _pollThread;
        }
        thread?.Join();
        lock (_nativeLock)
        {
            TeardownDll();
        }
    }

    private void TeardownDll()
    {
        // Reference tool order: RTT STOP, then JLINKARM_Close. The library stays loaded for a retry.
        _lib.RttControlStop?.Invoke(JLinkNative.RttCmdStop, IntPtr.Zero);
        _lib.Close?.Invoke();
    }

    public void Dispose()
    {
        Close();
        lock (_nativeLock)
        {
            _lib.Dispose();   // frees the DLL
            _logThunk = null;
            _errorThunk = null;
        }
    }

    private void OnDllLog(string message) => OnLog(message, isError: false);
    private void OnDllError(string message) => OnLog(message, isError: true);

    private void OnLog(string message, bool isError)
    {
        try
        {
            message = message.Trim();
            if (message.Length > 0)
                LogLine?.Invoke(isError ? $"LOG: [ERR] {message}" : $"LOG: {message}");
        }
        catch
        {
            // Never leak an exception into a native frame.
        }
    }
}
