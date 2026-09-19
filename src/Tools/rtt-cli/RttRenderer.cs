using System.Text;
using Toolbox.Core.SerialComm;

namespace Toolbox.Tools.RttCli;

/// <summary>Writes RTT up-channel bytes to stdout: streaming text through a stateful decoder (a UTF-8
/// sequence split across poll batches stays intact) or a 16-byte-per-line hex dump (HexCodec.Format).
/// All output goes through the caller's console lock so it never interleaves with prompts or
/// diagnostics; --log keeps ONE append stream open for the session instead of reopening the file
/// on every poll batch.</summary>
internal sealed class RttRenderer : IDisposable
{
    private readonly TextWriter _data;
    private readonly object _consoleLock;
    private readonly Decoder _decoder;
    private readonly FileStream? _logStream;
    private readonly TerminalUi? _ui;
    private readonly bool _hex;
    private readonly char[] _charBuffer = new char[8192];
    private readonly byte[] _hexLine = new byte[16];
    private int _hexPending;
    private bool _disposed;

    public RttRenderer(CommandLineOptions options, TextWriter data, object consoleLock, TerminalUi? ui = null)
    {
        _data = data;
        _consoleLock = consoleLock;
        _decoder = TextCodec.Resolve(options.EffectiveEncoding).GetDecoder();
        _ui = ui;
        _hex = options.Hex;
        if (options.LogFile is { Length: > 0 } path)
            _logStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4 * 1024);
    }

    /// <summary>Called on the transport's poll thread.</summary>
    public void OnData(byte[] data)
    {
        lock (_consoleLock)
        {
            if (data.Length == 0) return;
            if (_logStream is not null)
            {
                // Flush each batch: the capture must survive a hard kill, and one WriteFile
                // per batch is still far cheaper than the old reopen-per-batch.
                _logStream.Write(data, 0, data.Length);
                _logStream.Flush();
            }
            if (_hex) RenderHex(data);
            else RenderText(data);
        }
    }

    private void RenderText(byte[] data)
    {
        // Char output never exceeds byte count for utf8/ascii/latin1 and a batch is at most
        // 8192 bytes (PollBytes), so the fixed buffer always fits.
        int written = _decoder.GetChars(data, 0, data.Length, _charBuffer, 0, flush: false);
        if (_ui is not null)
        {
            _ui.WriteLog(new string(_charBuffer, 0, written));   // into the scrolling log region
        }
        else
        {
            _data.Write(_charBuffer, 0, written);
            _data.Flush();
        }
    }

    private void RenderHex(byte[] data)
    {
        foreach (byte b in data)
        {
            _hexLine[_hexPending++] = b;
            if (_hexPending == _hexLine.Length) FlushHexLine();
        }
        _data.Flush();
        // A trailing partial line (<16 bytes) stays buffered for the next batch; Dispose
        // flushes it on an orderly shutdown.
    }

    private void FlushHexLine()
    {
        string line = HexCodec.Format(_hexLine.AsSpan(0, _hexPending));
        if (_ui is not null) _ui.WriteLog(line + "\r\n");
        else _data.WriteLine(line);
        _hexPending = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_consoleLock)
        {
            if (_hex && _hexPending > 0) FlushHexLine();   // best-effort tail on orderly teardown
        }
        _logStream?.Dispose();
    }
}
