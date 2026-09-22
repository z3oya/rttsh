using System.Runtime.InteropServices;
using Toolbox.Core.Rtt;

namespace Toolbox.Tools.RttCli;

/// <summary>Shared J-Link connection sequence: probe select, DLL open, target connect, optional
/// reset+resume - plus the RTT native-call facade (RttStart/RttStop/RttRead/RttWrite/ReadMemory/
/// IsHalted), so the raw JLinkLibrary stays private and every DLL call funnels through this class.
/// RTT START is deliberately NOT part of Open - only the transport starts RTT.
/// Threading: the DLL is not thread-safe; the owner serializes all native calls. The log/error
/// thunks are instance fields to keep them alive for the DLL's lifetime.
/// The owner must call Open/Close/Dispose under the same lock that serializes the facade calls; the class
/// has no internal locking and no already-open guard - the owner enforces open/close sequencing.</summary>
internal sealed class JLinkConnection : IDisposable
{
    private readonly JLinkLibrary _lib = new();
    private JLinkNative.LogFn? _logThunk;
    private JLinkNative.LogFn? _errorThunk;
    private bool _open;

    private JLinkLibrary Library => _lib;

    /// <summary>Raised on a DLL thread for each J-Link log/error line (connection progress, RTT scans).</summary>
    public event Action<string>? LogLine;

    /// <summary>Loads the DLL, selects the probe, connects the target (no RTT). Throws IOException
    /// with an actionable message; on failure the DLL is closed again so a retry starts clean.</summary>
    public void Open(RttConnectionConfig config)
    {
        // Clamp BEFORE the chip check: Clamped() trims the chip name, so a whitespace-only
        // --chip fails right here with the message below instead of deep inside ExecCommand.
        config = config.Clamped();
        if (string.IsNullOrEmpty(config.Chip))
            throw new IOException("No target device specified.");

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
            {
                _lib.Reset!();
                // J-Link resets halt the core (vector catch: "Halt core after reset"). Resume it
                // so the firmware actually runs - a halted CPU produces no traffic at all.
                if (_lib.IsHalted?.Invoke() == 1)
                {
                    OnLog("Core is halted after reset - resuming via Go().", isError: false);
                    _lib.Go?.Invoke();
                }
            }
        }
        catch
        {
            if (dllOpened) _lib.Close!();   // reset the DLL so a retry starts clean
            throw;
        }

        LogDiagnostics();
        _open = true;
    }

    /// <summary>Idempotent. RTT STOP is the transport's job (it owns RTT); this only closes the DLL.
    /// The library stays loaded for a retry until Dispose.</summary>
    public void Close()
    {
        if (!_open) return;
        _open = false;
        _lib.Close!();
    }

    public void Dispose()
    {
        Close();
        _lib.Dispose();   // frees the DLL
        _logThunk = null;
        _errorThunk = null;
    }

    // ---- native-call facade -----------------------------------------------------------------
    // Every DLL call the transport makes funnels through these forwarders so the raw library
    // stays private. Pure forwarding - no locking (the owner's _nativeLock serializes everything,
    // including Open/Close/Dispose) and no open guard (the owner checks). RttStop/IsHalted stay
    // null-defensive (?.), the rest assume the export resolved (!).

    /// <summary>JLINK_RTTERMINAL(START). The config marshals as a ref struct; passing it by value
    /// copies the block once - the native layout is unchanged.</summary>
    public int RttStart(JLinkNative.RttStartConfig config) => Library.RttControlStart!(JLinkNative.RttCmdStart, ref config);

    /// <summary>JLINK_RTTERMINAL(STOP).</summary>
    public void RttStop() => Library.RttControlStop?.Invoke(JLinkNative.RttCmdStop, IntPtr.Zero);

    /// <summary>JLINK_RTTERMINAL_Read.</summary>
    public int RttRead(int channel, byte[] buffer, int maxLength) => Library.RttRead!(channel, buffer, maxLength);

    /// <summary>JLINK_RTTERMINAL_Write.</summary>
    public int RttWrite(int channel, byte[] buffer, int size) => Library.RttWrite!(channel, buffer, size);

    /// <summary>JLINKARM_ReadMemEx, fixed 8-bit access (RTT scans only ever read bytes).</summary>
    public int ReadMemory(uint address, uint numBytes, byte[] buffer) => Library.ReadMemEx!(address, numBytes, buffer, JLinkNative.Access8);

    /// <summary>JLINKARM_IsHalted; false when the export is missing (defensive, as before).</summary>
    public bool IsHalted() => Library.IsHalted?.Invoke() == 1;

    private void OnDllLog(string message) => OnLog(message, isError: false);
    private void OnDllError(string message) => OnLog(message, isError: true);

    /// <summary>Diagnostics via LogLine: which DLL actually loaded and its M.mmrr version.
    /// Runs unconditionally - the owning transport subscribes LogLine in its constructor, so
    /// there is no "no subscriber" case to guard. Whether the line is shown is the subscriber's
    /// call: Program only wires the transport's LogLine through under --verbose. Best-effort -
    /// diagnostics never fail the connect.</summary>
    private void LogDiagnostics()
    {
        try
        {
            string dllLine = Path.IsPathRooted(_lib.LoadedPath)
                ? _lib.LoadedPath
                : $"(bare-name load: {_lib.LoadedPath})";
            if (_lib.GetDllVersion is { } getDllVersion)
            {
                int v = getDllVersion();
                string rev = v % 100 == 0 ? "" : ((char)('a' + v % 100 - 1)).ToString();
                dllLine += $" (v{v / 10000}.{(v / 100) % 100:00}{rev})";
            }
            else
            {
                dllLine += " (version unknown)";   // export missing = very old DLL, distinct from a probe failure
            }
            OnLog(dllLine, isError: false);
        }
        catch
        {
            // diagnostics never fail the connect
        }
    }

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
