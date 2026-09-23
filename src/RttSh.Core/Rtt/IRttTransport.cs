namespace RttSh.Core.Rtt;

/// <summary>Byte-level bidirectional RTT channel transport. Implementations: J-Link DLL (tool exe), test fakes.
/// DataReceived/Error are raised on a background poll thread; each payload is a fresh array (safe to store).
/// Implementations serialize every native call internally (the J-Link DLL is not thread-safe); callers only
/// need to serialize Open/Close against each other.</summary>
public interface IRttTransport : IDisposable
{
    bool IsOpen { get; }

    /// <summary>Loads the DLL, selects the probe, connects the target and starts RTT;
    /// throws IOException with an actionable message to the caller.</summary>
    void Open(RttConnectionConfig config);

    /// <summary>Idempotent; after an Error event the transport has already closed itself.</summary>
    void Close();

    /// <summary>Throws on failure. A full down-buffer is retried with a per-progress deadline:
    /// any partial write resets the budget, so payloads complete as long as the target keeps
    /// draining; zero progress for the whole budget (target not reading) throws rather than
    /// dropping the payload silently.</summary>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>Raised on a background thread when target bytes arrive.</summary>
    event Action<byte[]>? DataReceived;

    /// <summary>Raised on a background thread when the link failed (probe gone, RTT error); the transport has closed itself.</summary>
    event Action<Exception>? Error;
}
