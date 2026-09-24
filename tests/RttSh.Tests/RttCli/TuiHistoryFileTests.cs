using System.Text.Json.Nodes;
using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

/// <summary>The .rttsh/tui-history.json mechanics, over real temp files. A round-trip keeps
/// order and recall semantics; anything wrong in the file - absent, corrupt, null-carrying -
/// degrades to an empty history, never an error; and the save-time cap keeps the file
/// bounded.</summary>
public class TuiHistoryFileTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rttsh-tui-history-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string HistPath(string dir) => Path.Combine(dir, TuiHistoryFile.FileName);

    private static InputHistory HistoryWith(params string[] lines)
    {
        var history = new InputHistory();
        foreach (string line in lines)
            history.Record(line);
        return history;
    }

    [Fact]
    public void Save_then_Load_round_trips_order_and_recall()
    {
        string dir = TempDir();
        TuiHistoryFile.Save(HistPath(dir), HistoryWith("led", "tick", "reboot"));

        InputHistory loaded = TuiHistoryFile.Load(HistPath(dir));

        Assert.True(loaded.TryOlder("live", out string first));
        Assert.Equal("reboot", first);          // the newest line recalls first
        loaded.TryOlder(first, out string second);
        loaded.TryOlder(second, out string third);
        Assert.Equal("tick", second);
        Assert.Equal("led", third);
        Assert.False(loaded.TryOlder(third, out _));
    }

    [Fact]
    public void A_missing_file_loads_as_empty()
    {
        InputHistory loaded = TuiHistoryFile.Load(HistPath(TempDir()));

        Assert.False(loaded.TryOlder("live", out _));
    }

    [Fact]
    public void A_corrupt_file_loads_as_empty()
    {
        string dir = TempDir();
        File.WriteAllText(HistPath(dir), "{ not json");

        InputHistory loaded = TuiHistoryFile.Load(HistPath(dir));

        Assert.False(loaded.TryOlder("live", out _));
    }

    [Fact]
    public void A_file_with_null_entries_loads_as_empty()
    {
        string dir = TempDir();
        File.WriteAllText(HistPath(dir), new JsonArray { "led", null }.ToJsonString());

        InputHistory loaded = TuiHistoryFile.Load(HistPath(dir));

        Assert.False(loaded.TryOlder("live", out _));
    }

    [Fact]
    public void A_json_null_root_loads_as_empty()
    {
        string dir = TempDir();
        File.WriteAllText(HistPath(dir), "null");

        Assert.False(TuiHistoryFile.Load(HistPath(dir)).TryOlder("live", out _));
    }

    [Fact]
    public void A_numeric_array_loads_as_empty()
    {
        string dir = TempDir();
        File.WriteAllText(HistPath(dir), "[1,2,3]");

        Assert.False(TuiHistoryFile.Load(HistPath(dir)).TryOlder("live", out _));
    }

    [Fact]
    public void An_empty_history_round_trips()
    {
        string dir = TempDir();
        TuiHistoryFile.Save(HistPath(dir), new InputHistory());

        Assert.False(TuiHistoryFile.Load(HistPath(dir)).TryOlder("live", out _));
    }

    [Fact]
    public void Chinese_and_emoji_lines_survive_the_json_round_trip()
    {
        string dir = TempDir();
        string[] lines = ["led r on", "设置 波特率 115200", "刺猬\U0001F994"];
        TuiHistoryFile.Save(HistPath(dir), HistoryWith(lines));

        InputHistory loaded = TuiHistoryFile.Load(HistPath(dir));

        Assert.Equal(lines, loaded.Entries);   // escaping is lossless, the astral emoji included
    }

    [Fact]
    public void Lines_with_control_chars_are_dropped_at_load()
    {
        string dir = TempDir();
        File.WriteAllText(HistPath(dir), """["ok", "a\nb", "\u001b[2Jbad", "fine"]""");

        InputHistory loaded = TuiHistoryFile.Load(HistPath(dir));

        Assert.Equal(["ok", "fine"], loaded.Entries);   // a recalled line must not paint control chars
    }

    [Fact]
    public void Seeded_lines_go_through_record()
    {
        string dir = TempDir();
        File.WriteAllText(HistPath(dir), """["a", "", "a", "b"]""");

        InputHistory loaded = TuiHistoryFile.Load(HistPath(dir));

        loaded.TryOlder("live", out string newest);
        loaded.TryOlder(newest, out string older);
        bool past = loaded.TryOlder(older, out _);
        Assert.Equal("b", newest);              // "" dropped, the repeated "a" collapsed
        Assert.Equal("a", older);
        Assert.False(past);                     // exactly two entries
    }

    [Fact]
    public void Save_keeps_only_the_newest_500()
    {
        string dir = TempDir();
        var history = new InputHistory();
        for (int i = 0; i < TuiHistoryFile.MaxEntries + 10; i++)
            history.Record($"cmd {i}");
        TuiHistoryFile.Save(HistPath(dir), history);

        InputHistory loaded = TuiHistoryFile.Load(HistPath(dir));

        Assert.True(loaded.TryOlder("live", out string newest));
        Assert.Equal($"cmd {TuiHistoryFile.MaxEntries + 9}", newest);
        for (int i = 0; i < TuiHistoryFile.MaxEntries - 1; i++)
            Assert.True(loaded.TryOlder("live", out _));
        Assert.False(loaded.TryOlder("live", out _));   // exactly MaxEntries survive
    }

    [Fact]
    public void Save_creates_a_missing_directory()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, ".rttsh", TuiHistoryFile.FileName);

        TuiHistoryFile.Save(path, HistoryWith("led"));

        Assert.True(File.Exists(path));
        Assert.Equal("led", TuiHistoryFile.Load(path).Entries.Single());
    }
}
