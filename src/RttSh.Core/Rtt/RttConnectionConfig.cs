namespace RttSh.Core.Rtt;

/// <summary>Immutable J-Link RTT connection settings; the tool exe maps these onto DLL calls,
/// keeping native interop out of Core. An empty <see cref="Chip"/> is a caller validation error,
/// not a clamp (the J-Link DLL rejects unknown device names anyway).</summary>
public sealed record RttConnectionConfig
{
    /// <summary>Lower bound for <see cref="SpeedKhz"/> (kHz); matches the reference tool's slowest preset.</summary>
    public const int MinSpeedKhz = 1_000;
    /// <summary>Upper bound for <see cref="SpeedKhz"/> (kHz); matches the reference tool's fastest preset.</summary>
    public const int MaxSpeedKhz = 48_000;

    /// <summary>Device name as known to the J-Link DLL database, e.g. "STM32F407VG".</summary>
    public string Chip { get; init; } = "";
    /// <summary>Default interface speed (kHz); the single source for the 4000 default.</summary>
    public const int DefaultSpeedKhz = 4_000;
    /// <summary>Default target interface.</summary>
    public const RttInterface DefaultInterface = RttInterface.Swd;
    public int SpeedKhz { get; init; } = DefaultSpeedKhz;
    public RttInterface Interface { get; init; } = DefaultInterface;
    public bool ResetOnConnect { get; init; } = true;
    /// <summary>Known control-block address; 0 lets the SDK scan RAM for _SEGGER RTT.</summary>
    public uint RttAddress { get; init; }
    /// <summary>Byte range searched for the signature when <see cref="RttAddress"/> is set; 0 disables the manual scan.</summary>
    public uint RttRange { get; init; }
    /// <summary>Probe USB serial number; 0 selects the default (first) probe.</summary>
    public int SerialNo { get; init; }
    /// <summary>RTT up/down channel pair index (0-15). Applies to both directions: the transport
    /// reads this up-channel and writes the matching down-channel. 0 = default (SEGGER RTT spec
    /// reserves up to 16 buffers per direction).</summary>
    public int Channel { get; init; }
    /// <summary>Inclusive upper bound for <see cref="Channel"/> (RTT spec: 16 buffers per direction).</summary>
    public const int MaxChannel = 15;
    /// <summary>Explicit J-Link DLL path; empty auto-detects (bare name, then SEGGER install roots).</summary>
    public string DllPath { get; init; } = "";

    /// <summary>Returns a copy with SpeedKhz clamped to its valid range and Chip/DllPath trimmed.</summary>
    public RttConnectionConfig Clamped() => this with
    {
        Chip = Chip.Trim(),
        DllPath = DllPath.Trim(),
        SpeedKhz = Math.Clamp(SpeedKhz, MinSpeedKhz, MaxSpeedKhz),
    };
}
