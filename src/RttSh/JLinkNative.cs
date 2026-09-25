using System.Runtime.InteropServices;

namespace Toolbox.Tools.RttCli;

/// <summary>Minimal self-declared subset of the SEGGER J-Link SDK: the official JLink.h is licensed
/// for redistribution and may not be installed. Signatures verified against pylink (square/pylink).
/// Note the RTT exports are prefixed JLINK_RTTERMINAL_* (not JLINK_RTTERM_*).</summary>
internal static class JLinkNative
{
    // Exported symbol names (JLinkARM.dll / JLink_x64.dll).
    public const string OpenEx = "JLINKARM_OpenEx";
    public const string EmuSelectByUsbSn = "JLINKARM_EMU_SelectByUSBSN";
    public const string SelectUsb = "JLINKARM_SelectUSB";
    public const string Close = "JLINKARM_Close";
    public const string TifSelect = "JLINKARM_TIF_Select";
    public const string SetSpeed = "JLINKARM_SetSpeed";
    public const string ExecCommand = "JLINKARM_ExecCommand";
    public const string Connect = "JLINKARM_Connect";
    public const string IsConnected = "JLINKARM_IsConnected";
    public const string Reset = "JLINKARM_Reset";
    public const string Go = "JLINKARM_Go";
    public const string IsHalted = "JLINKARM_IsHalted";
    public const string ReadMemEx = "JLINKARM_ReadMemEx";
    public const string RttControl = "JLINK_RTTERMINAL_Control";
    public const string RttRead = "JLINK_RTTERMINAL_Read";
    public const string RttWrite = "JLINK_RTTERMINAL_Write";
    public const string DeviceGetInfo = "JLINKARM_DEVICE_GetInfo";
    public const string Core2CoreName = "JLINKARM_Core2CoreName";
    public const string GetDllVersion = "JLINKARM_GetDLLVersion";

    // Memory / core-control and flash exports (resolved optional - an old DLL must still
    // connect for plain RTT; the mem_*/flash features report their absence per call).
    public const string Halt = "JLINKARM_Halt";
    public const string WriteMemEx = "JLINKARM_WriteMemEx";
    public const string DownloadFile = "JLINK_DownloadFile";
    public const string EraseChip = "JLINK_EraseChip";
    public const string SetFlashProgProgressCallback = "JLINK_SetFlashProgProgressCallback";

    public const int TifJtag = 0;
    public const int TifSwd = 1;

    public const int RttCmdStart = 0;
    public const int RttCmdStop = 1;

    /// <summary>ReadMemEx/WriteMemEx access width in bytes, per the J-Link SDK convention
    /// (1 = 8-bit, 2 = 16-bit, 4 = 32-bit - same unit mapping as pylink's nbits in jlink.py
    /// memory_read/memory_write). Only the 8-bit constant is referenced by name: the RTT
    /// signature scan reads byte-wise to stay alignment-free, while the script mem API derives
    /// the 2/4-byte widths from Lua's 16/32-bit width arguments in ScriptRuntime.MemUnit.</summary>
    public const uint Access8 = 1;

    /// <summary>JLINK_RTTERMINAL_Control(START) payload; ConfigBlockAddress 0 makes the SDK scan RAM
    /// for _SEGGER RTT. Layout fixed by the DLL ABI (16 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RttStartConfig
    {
        public uint ConfigBlockAddress;
        private readonly uint _reserved0;
        private readonly uint _reserved1;
        private readonly uint _reserved2;

        public RttStartConfig(uint configBlockAddress)
        {
            ConfigBlockAddress = configBlockAddress;
            _reserved0 = _reserved1 = _reserved2 = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FlashArea
    {
        public uint Address;
        public uint Size;
    }

    /// <summary>JLINKARM_DEVICE_GetInfo layout (per pylink structs.py): field order is ABI-fixed and the
    /// DLL validates SizeOfStruct. char* fields are the DLL's own strings - copy out immediately.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct DeviceInfo
    {
        public uint SizeOfStruct;
        public IntPtr Name;
        public uint CoreId;
        public uint FlashAddress;
        public uint RamAddress;
        public byte EndianMode;   // followed by 3 bytes of alignment padding
        public uint FlashSize;
        public uint RamSize;
        public IntPtr Manufacturer;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public FlashArea[] FlashAreas;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public FlashArea[] RamAreas;
        public uint Core;
    }

    public delegate void LogFn([MarshalAs(UnmanagedType.LPStr)] string message);
    public delegate IntPtr OpenExFn(LogFn log, LogFn error);   // IntPtr.Zero = success, else error string
    public delegate int EmuSelectByUsbSnFn(int serialNumber);  // <0 = not found
    public delegate int SelectUsbFn(int index);                // 0 = success
    public delegate void CloseFn();
    public delegate int TifSelectFn(int interfaceCode);
    public delegate void SetSpeedFn(int speedKhz);
    public delegate int ExecCommandFn([MarshalAs(UnmanagedType.LPStr)] string command, byte[] errorBuffer, int bufferSize);
    public delegate int ConnectFn();                            // <0 = failure
    public delegate int IsConnectedFn();
    public delegate int ResetFn();
    public delegate void GoFn();                                // resume a halted core (return value, if any, is unused - ABI-safe)
    public delegate int IsHaltedFn();                           // 1 = core halted
    public delegate int ReadMemExFn(uint address, uint size, byte[] buffer, uint access);
    public delegate int RttControlStartFn(int command, ref RttStartConfig config);
    public delegate int RttControlStopFn(int command, IntPtr config);   // STOP ignores config; NULL matches the reference tool
    public delegate int RttReadFn(int bufferIndex, byte[] buffer, int numBytes);
    public delegate int RttWriteFn(int bufferIndex, byte[] buffer, int numBytes);
    public delegate int DeviceGetInfoCountFn(int index, IntPtr info);   // index=-1, info=null -> device count
    public delegate int DeviceGetInfoFn(int index, ref DeviceInfo info);
    public delegate int Core2CoreNameFn(int core, byte[] buffer, int bufferSize);
    public delegate int GetDllVersionFn();   // Mmmrr: major=v/10000, minor=(v/100)%100, rev=v%100

    public delegate int HaltFn();   // >= 0 = ok (SDK: negative = error; pylink jlink.py halt treats 0 as success)

    /// <summary>JLINKARM_WriteMemEx: size is total BYTES, access is the unit width in bytes
    /// (1/2/4, 0 = DLL's choice); returns units written, negative = error (JLinkWriteErrors).</summary>
    public delegate int WriteMemExFn(uint address, uint numBytes, byte[] buffer, uint access);

    /// <summary>JLINK_DownloadFile: the DLL runs erase+program+verify from the image (hex/elf/mot
    /// carry addresses; addr is only used for raw binary); negative = error (JLinkFlashErrors).</summary>
    public delegate int DownloadFileFn([MarshalAs(UnmanagedType.LPStr)] string path, uint address);

    /// <summary>JLINK_EraseChip: >= 0 = ok, negative = error (JLinkEraseErrors). Only trustworthy
    /// after a reset-halt - see JLinkConnection.EraseChip.</summary>
    public delegate int EraseChipFn();

    /// <summary>Flash progress callback the DLL invokes during download: action is one of
    /// "Compare"/"Erase"/"Program"/"Verify" (FLASH_PROGRESS_PROTOTYPE in pylink's enums.py).</summary>
    public delegate void FlashProgressFn([MarshalAs(UnmanagedType.LPStr)] string action, [MarshalAs(UnmanagedType.LPStr)] string progress, int percentage);

    /// <summary>Pass null to clear a previously installed callback (pylink passes 0).</summary>
    public delegate void SetFlashProgProgressCallbackFn(FlashProgressFn? callback);
}
