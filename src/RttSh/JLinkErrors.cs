namespace Toolbox.Tools.RttCli;

/// <summary>Human-readable context for a negative J-Link SDK return code on the memory and flash
/// paths (JLINKARM_WriteMemEx/Halt/DownloadFile/EraseChip). The mapped codes and wordings follow
/// pylink-square's JLinkGlobalErrors/JLinkFlashErrors/JLinkEraseErrors (enums.py + errors.py);
/// this table is deliberately separate from RttFailureContext, whose entries carry RTT-specific
/// hints, so unmapped codes stay honest - a contention hint, never a guessed meaning.</summary>
internal static class JLinkErrors
{
    public static string Describe(int code) => code switch
    {
        -1 => "unspecified DLL error",
        -2 => "programmed data differs from source data (compare failed)",
        -3 => "error during the program/erase phase",
        -4 => "error verifying programmed data",
        -5 => "sector cannot be erased here (or memory zone not found)",
        -256 => "no connection to the probe",
        -257 => "probe communication error",
        -258 => "DLL not open",
        -259 => "target system has no power (VCC/Vref failure)",
        -260 => "given file/memory handle is invalid",
        -261 => "could not find supported CPU (wrong chip name, or the core is halted?)",
        -262 => "probe does not support the selected feature",
        -263 => "probe out of memory",
        -264 => "target interface error",
        -265 => "programmed data differs from source data (compare failed)",
        -266 => "programming error",
        -267 => "error verifying programmed data",
        -268 => "specified file could not be opened",
        -269 => "file format is not supported",
        -270 => "could not write target memory",
        -271 => "feature not supported by connected device",
        -272 => "DLL parameters configured incorrectly",
        -273 => "no target device selected",
        -274 => "target CPU is in low-power mode",
        _ => "code not in the verified table (possible probe contention - is another debugger or RTT tool attached?)",
    };
}
