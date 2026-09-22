using System.Runtime.InteropServices;
using Toolbox.Core.Rtt;

namespace Toolbox.Tools.RttCli;

/// <summary>Shared J-Link connection sequence: probe select, DLL open, target connect, optional
/// reset+resume. Extracted from JLinkRttTransport so the connect sequence has one owner;
/// RTT START is deliberately NOT part of this - only the transport starts RTT.
/// Threading: the DLL is not thread-safe; the owner serializes all Library calls. The log/error
/// thunks are instance fields to keep them alive for the DLL's lifetime.
/// The owner must call Open/Close/Dispose under the same lock that serializes Library calls; the class
/// has no internal locking and no already-open guard - the owner enforces open/close sequencing.</summary>
internal sealed class JLinkConnection : IDisposable
{
    private readonly JLinkLibrary _lib = new();
    private JLinkNative.LogFn? _logThunk;
    private JLinkNative.LogFn? _errorThunk;
    private bool _open;

    public JLinkLibrary Library => _lib;

    /// <summary>Raised on a DLL thread for each J-Link log/error line (connection progress, RTT scans).</summary>
    public event Action<string>? LogLine;

    /// <summary>Loads the DLL, selects the probe, connects the target (no RTT). Throws IOException
    /// with an actionable message; on failure the DLL is closed again so a retry starts clean.</summary>
    public void Open(RttConnectionConfig config)
    {
        if (string.IsNullOrEmpty(config.Chip))
            throw new IOException("No target device specified.");
        config = config.Clamped();

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
