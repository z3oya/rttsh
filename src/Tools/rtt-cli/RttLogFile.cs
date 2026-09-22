namespace Toolbox.Tools.RttCli;

/// <summary>The --log append stream, opened once per session. Shared by the renderer commands
/// (monitor/send) and by script, so the option behaves identically on all three: a bad path is a
/// usage error everywhere, and every command that accepts --log actually writes to it. --log is a
/// root option, so every command accepts it - anything that skips this helper silently ignores it.</summary>
internal static class RttLogFile
{
    /// <summary>Opens the append stream for <paramref name="path"/>, or null when --log was not
    /// given. Every failure here is a user input problem - a bad directory, access denied, illegal
    /// path characters, a directory instead of a file - so all of them surface as a usage error
    /// (exit 2) rather than a runtime failure.</summary>
    public static FileStream? Open(string? path)
    {
        if (path is not { Length: > 0 }) return null;
        try
        {
            return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4 * 1024);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException or ArgumentException)
        {
            throw new UsageException($"--log: cannot open '{path}': {ex.Message}");
        }
    }

    /// <summary>Appends one batch and flushes immediately: the capture must survive a hard kill,
    /// which one flushed write per poll batch gets cheaply. Called on the poll thread only, so no
    /// locking is needed - there is exactly one subscriber per session.</summary>
    public static void Append(FileStream stream, byte[] data)
    {
        stream.Write(data, 0, data.Length);
        stream.Flush();
    }
}
