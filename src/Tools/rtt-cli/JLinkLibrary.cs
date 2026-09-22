using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Toolbox.Tools.RttCli;

/// <summary>One record from the J-Link DLL device database (~10k entries).</summary>
internal sealed record JLinkDeviceRecord(string Name, string Manufacturer, string Core, uint FlashBytes, uint RamBytes);

/// <summary>Runtime-loads the SEGGER J-Link DLL and resolves exports (the DLL is a proprietary runtime
/// dependency, never a build reference). Load order mirrors the reference tool: explicit --dll path,
/// bare name via the OS search path, then SEGGER install roots - extended to probe ANY subdirectory
/// that carries the DLL (Ozone ships one without a JLink* folder), JLink* version dirs newest-first.</summary>
internal sealed class JLinkLibrary : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public bool IsLoaded => _handle != 0;

    public JLinkNative.OpenExFn? OpenEx { get; private set; }
    public JLinkNative.EmuSelectByUsbSnFn? EmuSelectByUsbSn { get; private set; }
    public JLinkNative.SelectUsbFn? SelectUsb { get; private set; }
    public JLinkNative.CloseFn? Close { get; private set; }
    public JLinkNative.TifSelectFn? TifSelect { get; private set; }
    public JLinkNative.SetSpeedFn? SetSpeed { get; private set; }
    public JLinkNative.ExecCommandFn? ExecCommand { get; private set; }
    public JLinkNative.ConnectFn? Connect { get; private set; }
    public JLinkNative.IsConnectedFn? IsConnected { get; private set; }
    public JLinkNative.ResetFn? Reset { get; private set; }
    public JLinkNative.GoFn? Go { get; private set; }
    public JLinkNative.IsHaltedFn? IsHalted { get; private set; }
    public JLinkNative.ReadMemExFn? ReadMemEx { get; private set; }
    public JLinkNative.RttControlStartFn? RttControlStart { get; private set; }
    public JLinkNative.RttControlStopFn? RttControlStop { get; private set; }
    public JLinkNative.RttReadFn? RttRead { get; private set; }
    public JLinkNative.RttWriteFn? RttWrite { get; private set; }
    public JLinkNative.DeviceGetInfoCountFn? DeviceGetInfoCount { get; private set; }
    public JLinkNative.DeviceGetInfoFn? DeviceGetInfo { get; private set; }
    public JLinkNative.Core2CoreNameFn? Core2CoreName { get; private set; }
    public JLinkNative.GetDllVersionFn? GetDllVersion { get; private set; }

    /// <summary>Full path of the DLL file that was actually loaded; empty until a Load succeeds.
    /// Every candidate kind is resolved through the loaded module (see ResolveLoadedPath), so a
    /// bare-name or SEGGER-root load reports the real file rather than the search key.</summary>
    public string LoadedPath { get; private set; } = "";

    public static string DefaultBaseName => Environment.Is64BitProcess ? "JLink_x64" : "JLinkARM";

    /// <summary>Attempts explicit path, then the bare DLL name, then SEGGER install roots.
    /// Returns false with an actionable message in <paramref name="error"/> when nothing loads.</summary>
    public bool Load(string dllPath, out string error)
    {
        Unload();
        string lastError = "";

        if (!string.IsNullOrWhiteSpace(dllPath))
            TryLoadFrom(dllPath.Trim(), out lastError);

        if (!IsLoaded)
            TryLoadFrom(DefaultBaseName, out lastError);

        if (!IsLoaded)
        {
            foreach (string candidate in ProbeSeggerRoots())
            {
                if (TryLoadFrom(candidate, out lastError))
                    break;
            }
        }

        if (IsLoaded)
        {
            error = "";
            return true;
        }

        error = $"{DefaultBaseName}.dll not found (pass --dll <path> or install the SEGGER J-Link software). {lastError}".Trim();
        return false;
    }

    public void Unload()
    {
        if (_handle != 0)
        {
            NativeLibrary.Free(_handle);
            _handle = 0;
        }
        OpenEx = null; EmuSelectByUsbSn = null; SelectUsb = null; Close = null;
        TifSelect = null; SetSpeed = null; ExecCommand = null; Connect = null;
        IsConnected = null; Reset = null; Go = null; IsHalted = null; ReadMemEx = null;
        RttControlStart = null; RttControlStop = null; RttRead = null; RttWrite = null;
        DeviceGetInfoCount = null; DeviceGetInfo = null; Core2CoreName = null;
        GetDllVersion = null;
        LoadedPath = "";
    }

    /// <summary>Enumerates the DLL's device database. Requires the optional DEVICE_GetInfo export
    /// (very old DLLs lack it); returns false with a message when unavailable or unreadable.</summary>
    public bool TryEnumerateDevices(out List<JLinkDeviceRecord> records, out string error)
    {
        records = [];
        error = "";
        if (!IsLoaded)
        {
            error = "J-Link DLL not loaded";
            return false;
        }
        if (DeviceGetInfoCount is null || DeviceGetInfo is null)
        {
            error = "J-Link DLL lacks device enumeration (too old)";
            return false;
        }

        int total = DeviceGetInfoCount(-1, IntPtr.Zero);
        if (total <= 0)
        {
            error = "DLL device database is empty";
            return false;
        }

        var coreBuffer = new byte[64];
        // One info instance reused across the whole ~10k-entry database: the ref-marshal
        // round-trips it every call, so hoisting it (and its two ByValArrays) out of the
        // loop removes ~20k allocations.
        var info = new JLinkNative.DeviceInfo
        {
            SizeOfStruct = (uint)Marshal.SizeOf<JLinkNative.DeviceInfo>(),
            FlashAreas = new JLinkNative.FlashArea[32],
            RamAreas = new JLinkNative.FlashArea[32],
        };
        for (int i = 0; i < total; i++)
        {
            if (DeviceGetInfo(i, ref info) < 0) continue;
            if (info.Name == IntPtr.Zero) continue;

            string name = Marshal.PtrToStringAnsi(info.Name) ?? "";
            string manufacturer = info.Manufacturer == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(info.Manufacturer) ?? "";
            string core = "";
            if (Core2CoreName is not null)
            {
                Core2CoreName((int)info.Core, coreBuffer, coreBuffer.Length);
                core = TrimAtNul(coreBuffer);
            }
            records.Add(new JLinkDeviceRecord(name, manufacturer, core, info.FlashSize, info.RamSize));
        }

        if (records.Count == 0)
        {
            error = "Could not read any device name from the DLL database";
            return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unload();
    }

    private bool TryLoadFrom(string candidate, out string error)
    {
        error = "";
        try
        {
            _handle = NativeLibrary.Load(candidate);
        }
        catch (Exception ex)
        {
            error = $"{candidate}: {ex.Message}";
            return false;
        }

        try
        {
            OpenEx = Resolve<JLinkNative.OpenExFn>(JLinkNative.OpenEx);
            EmuSelectByUsbSn = Resolve<JLinkNative.EmuSelectByUsbSnFn>(JLinkNative.EmuSelectByUsbSn);
            SelectUsb = Resolve<JLinkNative.SelectUsbFn>(JLinkNative.SelectUsb);
            Close = Resolve<JLinkNative.CloseFn>(JLinkNative.Close);
            TifSelect = Resolve<JLinkNative.TifSelectFn>(JLinkNative.TifSelect);
            SetSpeed = Resolve<JLinkNative.SetSpeedFn>(JLinkNative.SetSpeed);
            ExecCommand = Resolve<JLinkNative.ExecCommandFn>(JLinkNative.ExecCommand);
            Connect = Resolve<JLinkNative.ConnectFn>(JLinkNative.Connect);
            IsConnected = Resolve<JLinkNative.IsConnectedFn>(JLinkNative.IsConnected);
            Reset = Resolve<JLinkNative.ResetFn>(JLinkNative.Reset);
            Go = Resolve<JLinkNative.GoFn>(JLinkNative.Go);
            IsHalted = Resolve<JLinkNative.IsHaltedFn>(JLinkNative.IsHalted);
            ReadMemEx = Resolve<JLinkNative.ReadMemExFn>(JLinkNative.ReadMemEx);
            RttControlStart = Resolve<JLinkNative.RttControlStartFn>(JLinkNative.RttControl);
            RttControlStop = Resolve<JLinkNative.RttControlStopFn>(JLinkNative.RttControl);
            RttRead = Resolve<JLinkNative.RttReadFn>(JLinkNative.RttRead);
            RttWrite = Resolve<JLinkNative.RttWriteFn>(JLinkNative.RttWrite);
        }
        catch (EntryPointNotFoundException)
        {
            error = $"{candidate}: J-Link DLL missing required exports (old version?)";
            Unload();
            return false;
        }

        // Optional exports; older DLLs still connect fine without them.
        DeviceGetInfoCount = ResolveOptional<JLinkNative.DeviceGetInfoCountFn>(JLinkNative.DeviceGetInfo);
        DeviceGetInfo = ResolveOptional<JLinkNative.DeviceGetInfoFn>(JLinkNative.DeviceGetInfo);
        Core2CoreName = ResolveOptional<JLinkNative.Core2CoreNameFn>(JLinkNative.Core2CoreName);
        GetDllVersion = ResolveOptional<JLinkNative.GetDllVersionFn>(JLinkNative.GetDllVersion);
        LoadedPath = ResolveLoadedPath(candidate);
        return true;
    }

    /// <summary>Turns a load candidate into the loaded file's full path. The handle
    /// NativeLibrary.Load returns IS the module's load address, so the module list is matched
    /// exactly rather than by guessing at the OS search order. That covers every candidate
    /// kind, including SEGGER-root ones - those are absolute but extension-less
    /// (ProbeSeggerRoots builds Path.Combine(dir, "JLink_x64")). Best-effort: when the module
    /// cannot be found, the candidate comes back with a .dll extension so a rooted path still
    /// names a file that exists on disk.</summary>
    private string ResolveLoadedPath(string candidate)
    {
        string leaf = Path.GetFileName(candidate);
        if (!leaf.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) leaf += ".dll";

        try
        {
            ProcessModule? byName = null;
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                if (module.BaseAddress == _handle) return module.FileName;
                if (byName is null && string.Equals(module.ModuleName, leaf, StringComparison.OrdinalIgnoreCase))
                    byName = module;
            }
            if (byName is not null) return byName.FileName;
        }
        catch
        {
            // Best-effort only - diagnostics must never fail a load.
        }

        return candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? candidate : candidate + ".dll";
    }

    private T Resolve<T>(string name) where T : Delegate
    {
        IntPtr symbol = NativeLibrary.GetExport(_handle, name);
        return Marshal.GetDelegateForFunctionPointer<T>(symbol);
    }

    private T? ResolveOptional<T>(string name) where T : class, Delegate
    {
        return NativeLibrary.TryGetExport(_handle, name, out IntPtr symbol)
            ? Marshal.GetDelegateForFunctionPointer<T>(symbol)
            : null;
    }

    /// <summary>Reads a DLL-written ANSI buffer as a string up to its first NUL.</summary>
    internal static string TrimAtNul(byte[] buffer)
    {
        int len = Array.IndexOf(buffer, (byte)0);
        if (len < 0) len = buffer.Length;
        return Encoding.ASCII.GetString(buffer, 0, len);
    }

    /// <summary>Candidate DLL paths under the SEGGER install roots: JLink* version directories
    /// newest-first (matching the reference tool), then any other subdirectory that ships the DLL.</summary>
    private static IEnumerable<string> ProbeSeggerRoots()
    {
        string baseName = DefaultBaseName;
        string[] roots =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SEGGER"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SEGGER"),
        ];

        foreach (string root in roots)
        {
            string[] entries;
            try { entries = Directory.GetDirectories(root); }
            catch { continue; }   // root absent or unreadable

            var versionDirs = new List<string>();
            var otherDirs = new List<string>();
            foreach (string dir in entries)
            {
                if (Path.GetFileName(dir).StartsWith("JLink", StringComparison.OrdinalIgnoreCase))
                    versionDirs.Add(dir);
                else
                    otherDirs.Add(dir);
            }
            versionDirs.Sort(StringComparer.OrdinalIgnoreCase);   // ascending; walk back = newest first
            for (int i = versionDirs.Count - 1; i >= 0; i--)
                yield return Path.Combine(versionDirs[i], baseName);
            foreach (string dir in otherDirs)
                yield return Path.Combine(dir, baseName);
        }
    }
}
