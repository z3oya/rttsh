using System.Globalization;

namespace Toolbox.Tools.RttCli;

/// <summary>The --log append stream, opened once per session. Shared by the renderer commands
/// (monitor/send) and by script, so the option behaves identically on all three: a bad path is a
/// usage error everywhere, and every command that accepts --log actually writes to it. --log is a
/// root option, so every command accepts it - anything that skips this helper silently ignores it.
/// A bare --log (no path) arrives as <see cref="AutoMarker"/> and resolves here to
/// .rttsh/log/<timestamp>.log under the working directory - resolved at open time, because the
/// timestamp must be the session's, and -C/--root (which moves the directory before any session
/// opens a file) then anchors the default home like every other .rttsh path.</summary>
internal static class RttLogFile
{
    /// <summary>The parse-level value of a bare --log. A token no command line can carry (argv
    /// strings cannot hold NUL), so a user-spelled path can never collide with it.</summary>
    public const string AutoMarker = "\0auto";

    /// <summary>Where a bare --log writes: a log/ subdirectory of the local-state directory
    /// (ConfigFile.DirName, the config file's home), one timestamped file per session - local,
    /// disposable capture in the spirit of tui-history.json.</summary>
    public static string DefaultPath() =>
        Path.Combine(Environment.CurrentDirectory, ConfigFile.DirName, "log",
            $"{DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss.fff", CultureInfo.InvariantCulture)}.log");

    /// <summary>Opens the append stream for <paramref name="path"/>, or null when --log was not
    /// given (or gave an empty value). Every failure here is a user input problem - a bad
    /// directory, access denied, illegal path characters, a directory instead of a file - so all
    /// of them surface as a usage error (exit 2) rather than a runtime failure. The auto form
    /// creates its .rttsh/log home first; an explicit path still gets no directories made for it.</summary>
    public static FileStream? Open(string? path)
    {
        if (path is not { Length: > 0 }) return null;
        bool auto = path == AutoMarker;
        string resolved = auto ? DefaultPath() : path;
        try
        {
            if (auto)
                Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
            return new FileStream(resolved, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4 * 1024);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException or ArgumentException)
        {
            throw new UsageException($"--log: cannot open '{resolved}': {ex.Message}");
        }
    }

    /// <summary>Appends one batch and flushes immediately: the capture must survive a hard kill,
    /// which one flushed write per poll batch gets cheaply. Called on the poll thread only, so no
    /// locking is needed - there is exactly one subscriber per session. A failed write is rethrown
    /// as an IOException naming --log; the transport turns any subscriber exception into a clean
    /// link failure, and this message is all the user sees.</summary>
    public static void Append(FileStream stream, byte[] data)
    {
        try
        {
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ObjectDisposedException)
        {
            throw new IOException($"--log: write failed: {ex.Message}", ex);
        }
    }
}
