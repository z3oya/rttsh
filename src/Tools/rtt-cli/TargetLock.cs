using System.Text;
using Toolbox.Core.Rtt;

namespace Toolbox.Tools.RttCli;

/// <summary>One rtt-cli instance per target+channel. The lock is an OS file handle, not the
/// file's existence: the kernel closes the handle whatever way the process dies (crash,
/// taskkill, power loss), so a leftover .lock file can never block the next instance - the
/// file on disk is only the holder's PID note. Channel is a plain parameter so a future
/// --channel just changes the call site.</summary>
internal sealed class TargetLock : IDisposable
{
    /// <summary>Locks live per user (LOCALAPPDATA), named after target+channel only.</summary>
    private static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rtt-cli", "locks");

    private const int SharingViolation = unchecked((int)0x80070020);

    private readonly FileStream _file;

    private TargetLock(FileStream file) => _file = file;

    /// <summary>Acquires the lock for config+channel, or - when already held - writes the
    /// warning (with the holder's PID, best effort) to <paramref name="error"/> and returns
    /// null. Unrelated IO failures never claim the target is held. <paramref name="lockDirectory"/>
    /// overrides the default folder (tests).</summary>
    public static TargetLock? TryAcquire(RttConnectionConfig config, int channel, TextWriter error, string? lockDirectory = null)
    {
        string dir = lockDirectory ?? DefaultDirectory;
        string path = Path.Combine(dir, LockName(config, channel));
        FileStream file;
        try
        {
            Directory.CreateDirectory(dir);
            // FileShare.Read keeps the lock exclusive for writers (a rival's read-write
            // open fails - that IS the detection) while still letting one read the PID note.
            file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        }
        catch (IOException ex) when (ex.HResult == SharingViolation)
        {
            error.WriteLine(
                $"rtt-cli: {config.Chip} channel {channel} ({Describe(config)}) is already held by another " +
                $"rtt-cli ({ReadHolderPid(path)}). Close that instance first, or pick a different probe (--sn).");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"rtt-cli: cannot create the instance lock file: {ex.Message}");
            return null;
        }

        file.SetLength(0);
        file.Write(Encoding.ASCII.GetBytes(Environment.ProcessId.ToString()));
        file.Flush();
        return new TargetLock(file);
    }

    public void Dispose() => _file.Dispose();

    /// <summary>"swd, probe 12345", or just "swd" when the first probe was taken implicitly.</summary>
    private static string Describe(RttConnectionConfig config) => config.SerialNo != 0
        ? $"{config.Interface.ToString().ToLowerInvariant()}, probe {config.SerialNo}"
        : config.Interface.ToString().ToLowerInvariant();

    /// <summary>The holder's PID from the note file, best effort: the note is written right
    /// after acquiring, and the holder may exit between our failed open and this read.</summary>
    private static string ReadHolderPid(string path)
    {
        try
        {
            // The holder allows read sharing on purpose - reading the note is the point.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string text = reader.ReadToEnd().Trim();
            return uint.TryParse(text, out _) ? $"PID {text}" : "PID unknown";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "PID unknown";
        }
    }

    /// <summary>chip@if#sn#chN, flattened into a file name: Windows forbids \ / : * ? " &lt; &gt; |
    /// and the chip name is the only free-form part.</summary>
    private static string LockName(RttConnectionConfig config, int channel)
    {
        string key = $"{config.Chip}@{config.Interface.ToString().ToLowerInvariant()}#{config.SerialNo}#ch{channel}";
        return new string(key.Select(c => char.IsAsciiLetterOrDigit(c) || "@#.-_".Contains(c) ? c : '_').ToArray()) + ".lock";
    }
}
