using Toolbox.Core.Rtt;

namespace Toolbox.Tests.RttScripting;

/// <summary>In-memory IRttTransport fake: the test drives RX via Feed/Fail and inspects TX via
/// Written. Events fire synchronously on the caller's thread.</summary>
internal sealed class TestRttTransport : IRttTransport
{
    public bool IsOpen { get; private set; }
    public List<byte[]> Written { get; } = [];
    /// <summary>When set, Write throws this - simulates a dead link mid-session.</summary>
    public Exception? WriteFailure;

    public event Action<byte[]>? DataReceived;
    public event Action<Exception>? Error;

    public void Open(RttConnectionConfig config) => IsOpen = true;
    public void Close() => IsOpen = false;
    public void Dispose() => Close();

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        if (WriteFailure is not null) throw WriteFailure;
        Written.Add(data.ToArray());
    }

    public void Feed(params byte[][] chunks)
    {
        foreach (var chunk in chunks) DataReceived?.Invoke((byte[])chunk.Clone());
    }

    public void Fail(string message)
    {
        IsOpen = false;
        Error?.Invoke(new IOException(message));
    }
}
