using System.Runtime.InteropServices;

namespace Toolbox.Tools.RttCli;

/// <summary>One record from the J-Link DLL device database (~10k entries).</summary>
internal sealed record JLinkDeviceRecord(string Name, string Manufacturer, string Core, uint FlashBytes, uint RamBytes);

/// <summary>Enumerates the J-Link DLL's device database through an already-loaded JLinkLibrary
/// (list-devices is the only consumer).</summary>
internal static class JLinkDeviceDatabase
{
    /// <summary>Enumerates the DLL's device database. Requires the optional DEVICE_GetInfo export
    /// (very old DLLs lack it); returns false with a message when unavailable or unreadable.</summary>
    public static bool TryEnumerate(JLinkLibrary library, out List<JLinkDeviceRecord> records, out string error)
    {
        records = [];
        error = "";
        if (!library.IsLoaded)
        {
            error = "J-Link DLL not loaded";
            return false;
        }
        if (library.DeviceGetInfoCount is null || library.DeviceGetInfo is null)
        {
            error = "J-Link DLL lacks device enumeration (too old)";
            return false;
        }

        int total = library.DeviceGetInfoCount(-1, IntPtr.Zero);
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
            if (library.DeviceGetInfo(i, ref info) < 0) continue;
            if (info.Name == IntPtr.Zero) continue;

            string name = Marshal.PtrToStringAnsi(info.Name) ?? "";
            string manufacturer = info.Manufacturer == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(info.Manufacturer) ?? "";
            string core = "";
            if (library.Core2CoreName is not null)
            {
                library.Core2CoreName((int)info.Core, coreBuffer, coreBuffer.Length);
                core = JLinkLibrary.TrimAtNul(coreBuffer);
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
}
