using System.Text.Json;

namespace Toolbox.Tools.RttCli;

/// <summary>Persistent input history for -tui monitor sessions: ./.rttsh/tui-history.json,
/// seeded into the session's InputHistory at startup and rewritten on every exit path, so
/// Up/Down recall spans runs. The file is a bare JSON array of lines - no schema, no
/// metadata; it is disposable state, and the tolerant reader below already turns any future
/// format drift into an empty history rather than an error.
///
/// Same degradation ladder as ElfImageCache: a missing, corrupt, wrongly-typed or
/// null-carrying file is a miss (a fresh empty history, rewritten at save), and an
/// unwritable location silently skips the write - persistence can only change recall, never
/// behavior. Lines holding chars a session could never have produced (control chars, DEL) are
/// dropped at load: a recalled line is painted onto the input row and its committed bytes go
/// to the target, so a smuggled newline or ESC must not survive a hand-edited file. The
/// newest <see cref="MaxEntries"/> lines survive; older ones fall off,
/// HISTSIZE-style. Two sessions on different probes can share one .rttsh directory, so the
/// write is a uniquely-named temp file plus an atomic move: a concurrent reader sees the
/// previous or the new file, never a torn one (last writer wins the content).</summary>
internal static class TuiHistoryFile
{
    /// <summary>Lives next to the config file and the elf cache (ConfigFile.DirName), so
    /// -C/--root anchors it the same way.</summary>
    public const string FileName = "tui-history.json";

    /// <summary>Applied once at save time; a hand-grown file still loads whole and is cut
    /// back on the next exit.</summary>
    public const int MaxEntries = 500;

    public static InputHistory Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new InputHistory();
            string[]? entries = JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(path));
            if (entries is null || entries.Any(e => e is null))
                return new InputHistory();
            return new InputHistory(entries.Where(IsRecallable));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new InputHistory();
        }
    }

    /// <summary>A recalled line is painted onto the input row, so a control char (newline, ESC)
    /// would tear the layout, and its committed bytes go to the target. Chars the editor can
    /// never produce (below space, DEL) mark a hand-edited or corrupted line: that line is
    /// dropped, the rest of the file still loads.</summary>
    private static bool IsRecallable(string line)
    {
        foreach (char c in line)
            if (c < ' ' || c == '\u007f')
                return false;
        return true;
    }

    public static void Save(string path, InputHistory history)
    {
        IReadOnlyList<string> entries = history.Entries;
        string[] newest = [.. entries.Skip(Math.Max(0, entries.Count - MaxEntries))];
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(newest, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // unwritable location: the session ran fine, it just keeps no history
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };   // the file is human-readable
}
