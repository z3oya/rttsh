using Toolbox.Core.Rtt;

namespace Toolbox.Tools.RttCli;

/// <summary>IRttTransport over the SEGGER J-Link DLL (mirrors the reference rtt-t2 tool), reading
/// and writing the RTT up/down channel pair selected by RttConnectionConfig.Channel (default 0).
/// Connection sequencing lives in JLinkConnection; this class adds RTT START/STOP, the poll thread
/// and the channel read/write paths.
///
/// Threading: the DLL is NOT thread-safe; every native call runs under _nativeLock. The poll thread
/// owns the failure path: on a negative read it performs the DLL teardown itself and then raises
/// Error - an external Close() that raced it just sees the link already closed (idempotent). Close()
/// stops the poll thread by clearing _open, joins it OUTSIDE the lock (it may be inside a read),
/// then tears down.</summary>
internal sealed class JLinkRttTransport : IRttTransport
{
    private const int PollBytes = 8192;
    private const int PollIdleMs = 2;
    /// <summary>Quiet polls (~2ms each) between core-halt checks on the idle path (~2s at 2ms).</summary>
    private const int IdlePollsPerHaltCheck = 1000;
    /// <summary>Zero-progress budget for Write: any partial write restarts it.</summary>
    private const int WriteRetryDeadlineMs = 1500;
    private const int WriteRetryDelayMs = 25;

    private readonly JLinkConnection _connection = new();
    private readonly object _nativeLock = new();
    private readonly byte[] _rxBuffer = new byte[PollBytes];
    private Thread? _pollThread;
    private volatile bool _open;

    /// <summary>RTT up/down channel pair served by this transport; set from the config in Open().</summary>
    public int Channel { get; private set; }

    public bool IsOpen => _open;

    /// <summary>Raised on a DLL thread for each J-Link log/error line (connection progress, RTT scans).</summary>
    public event Action<string>? LogLine;

    public event Action<byte[]>? DataReceived;
    public event Action<Exception>? Error;

    public JLinkRttTransport()
    {
        _connection.LogLine += line => LogLine?.Invoke(line);
    }

    public void Open(RttConnectionConfig config)
    {
        if (string.IsNullOrEmpty(config.Chip))
            throw new IOException("No target device specified.");
        config = config.Clamped();

        lock (_nativeLock)
        {
            if (_open) throw new IOException("The RTT link is already open.");

            try
            {
                Channel = config.Channel;
                _connection.Open(config);   // probe, DLL open, target connect (no RTT yet)
                StartRtt(config);
            }
            catch
            {
                _connection.Close();   // reset the DLL so a retry starts clean
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
        if (_connection.Library.RttControlStart!(JLinkNative.RttCmdStart, ref start) < 0)
            throw new IOException($"Failed to start RTT (addr=0x{address:X}). If the block is not auto-detected, pass --rtt-addr and --rtt-range.");
    }

    /// <summary>Reads the whole window in one call and locates the "SEGGER RTT" signature (pure-logic
    /// half lives in RttControlBlock.FindSignature and is unit-tested).</summary>
    private uint ScanRttAddress(uint start, uint range)
    {
        var dump = new byte[range];
        int got = _connection.Library.ReadMemEx!(start, range, dump, JLinkNative.Access8);
        if (got <= 0) return 0;
        int index = RttControlBlock.FindSignature(dump.AsSpan(0, got));
        return index < 0 ? 0 : start + (uint)index;
    }

    /// <summary>Polls the configured up-channel at a fixed ~2ms cadence (matching the reference tool).
    /// A negative read fails the link; a stretch of empty reads with a halted core does too - a
    /// silent stall would otherwise look exactly like a quiet target.</summary>
    private void PollLoop()
    {
        int idlePolls = 0;
        while (true)
        {
            int read;
            lock (_nativeLock)
            {
                if (!_open) return;   // external Close() in progress
                read = _connection.Library.RttRead!(Channel, _rxBuffer, PollBytes);
            }

            if (read < 0)
            {
                FailLink($"RTT read error (code={read}: {RttFailureContext(read)}).");
                return;
            }

            if (read > 0)
            {
                idlePolls = 0;
                var data = new byte[read];
                Array.Copy(_rxBuffer, data, read);
                DataReceived?.Invoke(data);
            }
            else if (++idlePolls >= IdlePollsPerHaltCheck)
            {
                idlePolls = 0;
                // Only on quiet links, ~1/second at most; IsHalted is a single DAP read.
                lock (_nativeLock)
                {
                    if (_open && _connection.Library.IsHalted?.Invoke() == 1)
                    {
                        FailLink("RTT stalled: the target core is halted (debugger attached or reset left it halted). Reconnect with --reset to resume it.");
                        return;
                    }
                }
            }
            Thread.Sleep(PollIdleMs);
        }
    }

    /// <summary>Closes the link from the poll thread (it owns the failure path) and raises Error.</summary>
    private void FailLink(string message)
    {
        lock (_nativeLock)
        {
            if (!_open) return;   // external Close() won the race; it does the teardown
            _open = false;
            TeardownDll();
        }
        Error?.Invoke(new IOException(message));
    }

    /// <summary>Human-readable context for a negative JLINK_RTTERMINAL result. The mapped codes follow
    /// pylink-square 2.0.1's JLinkGlobalErrors/JLinkRTTErrors (the J-Link SDK global error codes), but
    /// this table is only a subset of them, so unmapped codes stay honest - a contention hint, never
    /// a guessed meaning.</summary>
    internal static string RttFailureContext(int code) => code switch
    {
        -1 => "unspecified DLL error",
        -2 => "RTT control block not found (check --rtt-addr, or that the firmware runs and RTT is built in)",
        -256 => "no connection to the probe",
        -257 => "probe communication error",
        -258 => "DLL not open",
        -259 => "probe VCC/Vref failure",
        -261 => "no CPU found (wrong chip name, or the core is halted?)",
        -274 => "CPU in low-power mode (terminal output may be stalled)",
        _ => "code not in the verified table (possible probe contention - is another debugger or RTT tool attached?)",
    };

    /// <summary>Writes to the down channel, retrying while the DLL reports a full buffer: right
    /// after RTT START its buffer view has not settled yet (an immediate write reports 0
    /// written), and a stalled target drains the ring slowly. The deadline is per progress: any
    /// partial write restarts it, so large payloads take as long as the target needs to drain,
    /// while a target that reads nothing fails after WriteRetryDeadlineMs of zero progress with
    /// a message that names the actual fault.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        var buffer = data.ToArray();   // native delegates take managed arrays
        lock (_nativeLock)
        {
            if (!_open) throw new IOException("The RTT link is not open.");
            int written = 0;
            long deadline = Environment.TickCount64 + WriteRetryDeadlineMs;
            while (true)
            {
                var chunk = written == 0 ? buffer : buffer[written..];
                int n = _connection.Library.RttWrite!(Channel, chunk, chunk.Length);
                if (n < 0)
                    throw new IOException($"RTT write failed (code={n}: {RttFailureContext(n)}).");
                written += n;
                if (written >= buffer.Length) return;
                if (!_open) throw new IOException("The RTT link is not open.");
                if (n > 0) deadline = Environment.TickCount64 + WriteRetryDeadlineMs;   // progress buys a fresh budget
                if (Environment.TickCount64 >= deadline)
                    throw new IOException($"RTT down-buffer made no progress for {WriteRetryDeadlineMs} ms (wrote {written}/{buffer.Length} bytes) - is the target reading the down channel?");
                Thread.Sleep(WriteRetryDelayMs);
            }
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
        _connection.Library.RttControlStop?.Invoke(JLinkNative.RttCmdStop, IntPtr.Zero);
        _connection.Close();
    }

    public void Dispose()
    {
        Close();
        lock (_nativeLock)
        {
            _connection.Dispose();
        }
    }
}
