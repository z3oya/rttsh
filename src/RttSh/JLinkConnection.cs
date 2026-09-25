using System.Runtime.InteropServices;
using RttSh.Core.Rtt;

namespace Toolbox.Tools.RttCli;

/// <summary>Shared J-Link connection sequence: probe select, DLL open, target connect, optional
/// reset+resume - plus the native-call facade (RTT: RttStart/RttStop/RttRead/RttWrite/ReadMemory/
/// IsHalted; memory/core: WriteMemory/ReadMemory(width)/Halt/Resume; flash: SetFlashProgressCallback/
/// DownloadFile/EraseChip), so the raw JLinkLibrary stays private and every DLL call funnels through
/// this class. RTT START is deliberately NOT part of Open - only the transport starts RTT, which is
/// what lets the flash commands run against the same connection without a link.
/// Threading: the DLL is not thread-safe; the owner serializes all native calls. The log/error
/// thunks are instance fields to keep them alive for the DLL's lifetime.
/// The owner must call Open/Close/Dispose under the same lock that serializes the facade calls; the class
/// has no internal locking and no already-open guard - the owner enforces open/close sequencing.</summary>
internal sealed class JLinkConnection : IDisposable
{
    private readonly JLinkLibrary _lib = new();
    private JLinkNative.LogFn? _logThunk;
    private JLinkNative.LogFn? _errorThunk;
    private JLinkNative.FlashProgressFn? _progressThunk;   // native callback keep-alive (see SetFlashProgressCallback)
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
                ResetAndResume();
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
        _progressThunk = null;
    }

    // ---- native-call facade -----------------------------------------------------------------
    // Every DLL call the transport makes funnels through these forwarders so the raw library
    // stays private. Pure forwarding - no locking (the owner's _nativeLock serializes everything,
    // including Open/Close/Dispose) and no open guard (the owner checks). Null handling follows
    // the export's age: RttStop/IsHalted stay null-defensive (?.), the original connect-path
    // exports assume the export resolved (!), and the optional feature exports (Halt/
    // WriteMemory/SetFlashProgressCallback/DownloadFile/EraseChip) check-and-throw on absence.

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

    /// <summary>Resets the target and leaves the core halted (J-Link resets halt via vector
    /// catch) - the state J-Link Commander's erase creates with its "implicit reset &amp; halt".
    /// A plain halt is not enough: on-target testing found the DLL's erase paths silently
    /// no-op while the core had been executing from the bank. Throws when the core did not
    /// end up halted, so a failed preparation is loud instead of a silent no-op erase.</summary>
    public void ResetHalt()
    {
        _lib.Reset!();
        if (!IsHalted())
            throw new IOException("Core did not halt after reset - refusing to run destructive flash work in this state.");
    }

    /// <summary>Resets the target and resumes a core the reset left halted (J-Link resets halt via
    /// vector catch - a halted CPU produces no traffic at all). Shared by the connect path and
    /// flash's --reset, which want the exact same sequence and log line.</summary>
    public void ResetAndResume()
    {
        _lib.Reset!();
        if (IsHalted())
        {
            OnLog("Core is halted after reset - resuming via Go().", isError: false);
            _lib.Go?.Invoke();
        }
    }

    /// <summary>JLINKARM_Halt. Throws with the code + table wording on failure; throws when the
    /// export is missing (optional export - an old DLL still serves plain RTT).</summary>
    public void Halt()
    {
        if (Library.Halt is not { } halt)
            throw new IOException("This J-Link DLL lacks JLINKARM_Halt (a very old version?); the core cannot be halted.");
        int code = halt();
        if (code < 0)
            throw new IOException($"JLINKARM_Halt failed (code={code}: {JLinkErrors.Describe(code)}).");
    }

    /// <summary>Resumes a halted core (JLINKARM_Go; its return value is unused - ABI-safe).</summary>
    public void Resume() => Library.Go?.Invoke();

    /// <summary>JLINKARM_ReadMemEx with an explicit access width (bytes: 1/2/4). Negative return
    /// is the DLL's error code - the caller maps it through JLinkErrors with its own context.</summary>
    public int ReadMemory(uint address, uint numBytes, byte[] buffer, uint access) =>
        Library.ReadMemEx!(address, numBytes, buffer, access);

    /// <summary>JLINKARM_WriteMemEx: numBytes is total bytes, access the unit width in bytes
    /// (1/2/4); returns units written, negative = error code for the caller to map.</summary>
    public int WriteMemory(uint address, uint numBytes, byte[] buffer, uint access)
    {
        if (Library.WriteMemEx is not { } write)
            throw new IOException("This J-Link DLL lacks JLINKARM_WriteMemEx (a very old version?); memory writes are unavailable.");
        return write(address, numBytes, buffer, access);
    }

    /// <summary>Installs (or clears, with null) the flash progress callback. The delegate is kept
    /// in a field for the DLL's lifetime - a collected thunk would crash inside a native frame.</summary>
    public void SetFlashProgressCallback(JLinkNative.FlashProgressFn? callback)
    {
        if (Library.SetFlashProgProgressCallback is not { } set)
            throw new IOException("This J-Link DLL lacks JLINK_SetFlashProgProgressCallback (a very old version?); flash progress is unavailable.");
        _progressThunk = callback;
        set(callback);
    }

    /// <summary>JLINK_DownloadFile: erase+program+verify from an image file inside the DLL
    /// (hex/elf/mot carry their own addresses; address is only used for raw binary).</summary>
    public int DownloadFile(string path, uint address)
    {
        if (Library.DownloadFile is not { } download)
            throw new IOException("This J-Link DLL lacks JLINK_DownloadFile (a very old version?); flash download is unavailable.");
        return download(path, address);
    }

    /// <summary>Erases the whole chip's flash: ExecCommand("EnableEraseAllFlashBanks") +
    /// JLINK_EraseChip, after a reset-halt (FlashOnce does the reset-halt). All three steps are
    /// on-target findings (STM32H743 + DLL v7.98a): without the flag, JLINK_EraseChip is a
    /// silent no-op here - returns 0, never fires the progress callback, content untouched
    /// (J-Link Commander's own erase command no-ops the same way); ExecCommand("erase") is not
    /// an alternative (returns OK with "ERROR: Unknown command" in its buffer); and the core
    /// must be reset-halted, not just running. With the flag the erase really runs - seconds
    /// for the programmed bank, minutes for the whole 2MB. The Unknown-command trap is also
    /// why this method treats a non-empty ExecCommand buffer as failure even on a non-negative
    /// return code: on this path the return value alone has a history of reporting success
    /// for work that never happened.</summary>
    public void EraseChip()
    {
        var error = new byte[256];
        int flag = Library.ExecCommand!("EnableEraseAllFlashBanks", error, error.Length);
        // The return code is not a reliable error channel: an unrecognized command returns OK
        // with "ERROR: Unknown command" in the buffer (same check as Open's device = path) -
        // and JLINK_EraseChip silently no-ops without the flag actually being set.
        string message = JLinkLibrary.TrimAtNul(error);
        if (flag < 0 || message.Length > 0)
            throw new IOException($"ExecCommand(EnableEraseAllFlashBanks) failed (code={flag}{(message.Length > 0 ? $", {message}" : "")}).");

        if (Library.EraseChip is not { } erase)
            throw new IOException("This J-Link DLL lacks JLINK_EraseChip (a very old version?); chip erase is unavailable.");
        int erased = erase();
        if (erased < 0)
            throw new IOException($"JLINK_EraseChip failed (code={erased}: {JLinkErrors.Describe(erased)}).");
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
