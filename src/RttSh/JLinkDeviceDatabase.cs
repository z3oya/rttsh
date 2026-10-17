using System.Runtime.InteropServices;

namespace Toolbox.Tools.RttCli;

/// <summary>One record from the J-Link DLL device database (~10k entries). FlashBytes is the
/// primary on-chip flash (see JLinkDeviceDatabase.PrimaryFlashBytes); RamBytes is the DLL's
/// work RAM (flash-loader scratch), not total on-chip SRAM.</summary>
internal sealed record JLinkDeviceRecord(string Name, string Manufacturer, string Core, uint FlashBytes, uint RamBytes);

/// <summary>Enumerates the J-Link DLL's device database through an already-loaded JLinkLibrary
/// (list-devices is the only consumer).</summary>
internal static class JLinkDeviceDatabase
{
    /// <summary>Leading banks smaller than this are option/signature strays, not flash arrays
    /// (Infineon TLE98x keeps a 4-byte NVM at 0x10FFFFFC, CS32 16 option bytes at 0x06000000).</summary>
    private const uint TinyBankBytes = 64;
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
            records.Add(new JLinkDeviceRecord(name, manufacturer, core, PrimaryFlashBytes(info.FlashSize, info.FlashAreas), info.RamSize));
        }

        if (records.Count == 0)
        {
            error = "Could not read any device name from the DLL database";
            return false;
        }
        return true;
    }

    /// <summary>Byte size of a device's primary on-chip flash: the bank chain starting at the
    /// lowest real bank, merging following banks whose distance from the chain end is at most the
    /// running total. The DLL's FlashSize cannot be shown as-is - it sums every bank of the entry
    /// including external memory-mapped windows (an STM32H743ZI with 2MB on-chip reports 258MB
    /// because of its 256MB QSPI window). FlashAddress is no anchor either: some entries aim it at
    /// option strays (Renesas RA anchors a 512-byte option bank). The gap bound keeps dual-bank
    /// arrays together (the H743's 1MB variants split into two 512KB banks with a 512KB hole,
    /// 0x08080000..0x08100000) while external windows sit gigabytes out and never merge. Entries
    /// without bank data (old DLLs) fall back to the DLL total.</summary>
    public static uint PrimaryFlashBytes(uint flashSize, JLinkNative.FlashArea[] areas)
    {
        var banks = areas.Where(a => a.Size > 0).OrderBy(a => a.Address).ToArray();
        JLinkNative.FlashArea start = banks.FirstOrDefault(b => b.Size >= TinyBankBytes);
        if (start.Size == 0) return flashSize;

        uint total = start.Size;
        uint end = start.Address + start.Size;
        for (int merged = 1; merged < banks.Length; merged++)
        {
            JLinkNative.FlashArea next = banks.FirstOrDefault(b => b.Address >= end);
            if (next.Size == 0 || next.Address - end > total) break;
            total += next.Size;
            end = next.Address + next.Size;
        }
        return total;
    }
}
