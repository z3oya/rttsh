namespace RttSh.Core.Rtt;

/// <summary>Debugger-style target memory and core-control access, kept off <see cref="IRttTransport"/>
/// on purpose: the RTT interface is a byte channel with events, this one is synchronous request/response
/// for the rtt.mem_*/halt scripting API. Implementations: J-Link transport (tool exe, sharing the
/// transport's native lock), test fakes. All members require an open link; implementations throw
/// IOException on a closed one.
///
/// Error convention: ReadMemory/WriteMemory return the raw DLL code (negative = error, the caller
/// maps it with its own address context; >= 0 = units transferred), while Halt/Resume throw - they
/// are control operations whose failure the caller surfaces verbatim. Access widths follow the
/// J-Link SDK convention in bytes: 1 = 8-bit, 2 = 16-bit, 4 = 32-bit (width-sensitive access needs
/// an address aligned to the unit).</summary>
public interface ITargetMemory
{
    /// <summary>Reads numBytes at address into buffer; returns bytes read, negative = DLL error code.</summary>
    int ReadMemory(uint address, uint numBytes, byte[] buffer, uint access);

    /// <summary>Writes numBytes from buffer; returns units written, negative = DLL error code.</summary>
    int WriteMemory(uint address, uint numBytes, byte[] buffer, uint access);

    /// <summary>True when the core is halted; false when the link is down or the export is missing.</summary>
    bool IsHalted();

    /// <summary>Halts the core. A script-driven halt must suspend the RTT poll's idle
    /// stalled-core detection (a halted core cannot produce traffic) until <see cref="Resume"/>.
    /// Throws on failure or a missing export.</summary>
    void Halt();

    /// <summary>Resumes a halted core (JLINKARM_Go) and re-enables the idle detection.
    /// Throws on failure.</summary>
    void Resume();
}
