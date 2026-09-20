using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

public class ConsoleInputTests
{
    /// <summary>Records every editor draw/commit so tests can pin what the user would see.</summary>
    private sealed class FakeInputSurface : IInputSurface
    {
        public List<string> Redraws { get; } = [];
        public List<string> Committed { get; } = [];
        public List<string> LogWrites { get; } = [];
        /// <summary>false simulates a row that ran out of room: the append must be rejected.</summary>
        public bool AcceptAppends { get; set; } = true;

        public void WriteLog(string text) => LogWrites.Add(text);
        public bool TryAppendInputChar(char c) => AcceptAppends;
        public void RedrawInput(string buffer) => Redraws.Add(buffer);
        public void CommitInput(string line) => Committed.Add(line);
    }

    /// <summary>Feeds a fixed key stream; over-reading past the end throws, which fails the test.</summary>
    private static Func<ConsoleKeyInfo> Pump(params ConsoleKeyInfo[] keys)
    {
        int index = 0;
        return () => keys[index++];
    }

    private static ConsoleKeyInfo Char(char c) => new(c, ConsoleKey.None, false, false, false);
    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);
    // ctor order is (keyChar, key, shift, alt, control) - alt before control.
    private static ConsoleKeyInfo CtrlC() => new('\u0003', ConsoleKey.C, false, false, true);

    [Fact]
    public void Up_recalls_and_typing_continues_from_the_recalled_line()
    {
        var surface = new FakeInputSurface();
        var history = new InputHistory();
        history.Record("old");

        bool done = ConsoleInput.TryReadLine(surface, history, Pump(
            Char('n'), Char('e'), Key(ConsoleKey.UpArrow), Char('w'), Key(ConsoleKey.Enter)),
            out string line);

        Assert.True(done);
        Assert.Equal("oldw", line);
        Assert.Equal(["old"], surface.Redraws);       // one redraw: the recall itself
        Assert.Equal(["oldw"], surface.Committed);
    }

    [Fact]
    public void Up_passes_the_typed_text_as_the_draft()
    {
        var surface = new FakeInputSurface();
        var history = new InputHistory();
        history.Record("old");

        bool done = ConsoleInput.TryReadLine(surface, history, Pump(
            Char('a'), Char('b'), Key(ConsoleKey.UpArrow), CtrlC()), out string _);

        Assert.False(done);
        history.TryNewer(out string draft);   // returning past the newest restores what was typed
        Assert.Equal("ab", draft);
    }

    [Fact]
    public void Second_up_at_the_oldest_changes_nothing()
    {
        var surface = new FakeInputSurface();
        var history = new InputHistory();
        history.Record("only");

        ConsoleInput.TryReadLine(surface, history, Pump(
            Key(ConsoleKey.UpArrow), Key(ConsoleKey.UpArrow), Key(ConsoleKey.Enter)), out string line);

        Assert.Equal("only", line);
        Assert.Equal(["only"], surface.Redraws);   // the second Up drew nothing
    }

    [Fact]
    public void Down_past_the_newest_restores_the_draft_on_screen()
    {
        var surface = new FakeInputSurface();
        var history = new InputHistory();
        history.Record("old");

        ConsoleInput.TryReadLine(surface, history, Pump(
            Char('d'), Key(ConsoleKey.UpArrow), Key(ConsoleKey.DownArrow), Key(ConsoleKey.Enter)),
            out string line);

        Assert.Equal("d", line);
        Assert.Equal(["old", "d"], surface.Redraws);
    }

    [Fact]
    public void Backspace_after_recall_edits_the_recalled_line()
    {
        var surface = new FakeInputSurface();
        var history = new InputHistory();
        history.Record("old");

        ConsoleInput.TryReadLine(surface, history, Pump(
            Char('a'), Char('b'), Key(ConsoleKey.UpArrow), Key(ConsoleKey.Backspace), Key(ConsoleKey.Enter)),
            out string line);

        Assert.Equal("ol", line);
        Assert.Equal(["old", "ol"], surface.Redraws);
    }

    [Fact]
    public void Arrows_on_empty_history_do_nothing()
    {
        var surface = new FakeInputSurface();
        var history = new InputHistory();

        ConsoleInput.TryReadLine(surface, history, Pump(
            Key(ConsoleKey.UpArrow), Key(ConsoleKey.DownArrow), Char('x'), Key(ConsoleKey.Enter)),
            out string line);

        Assert.Equal("x", line);
        Assert.Empty(surface.Redraws);
    }

    [Fact]
    public void Plain_mode_ignores_arrow_keys()
    {
        var history = new InputHistory();
        history.Record("old");

        bool done = ConsoleInput.TryReadLine(null, history, Pump(
            Key(ConsoleKey.UpArrow), Key(ConsoleKey.DownArrow), CtrlC()), out string _);

        Assert.False(done);   // no throw, no early return: the arrows were swallowed
    }

    [Fact]
    public void Ctrl_c_reports_the_caret_to_the_log_surface()
    {
        var surface = new FakeInputSurface();
        var history = new InputHistory();

        bool done = ConsoleInput.TryReadLine(surface, history, Pump(CtrlC()), out string line);

        Assert.False(done);
        Assert.Equal("", line);
        Assert.Equal(["^C\r\n"], surface.LogWrites);
    }

    [Fact]
    public void Rejected_append_falls_back_to_a_full_redraw()
    {
        var surface = new FakeInputSurface { AcceptAppends = false };
        var history = new InputHistory();

        ConsoleInput.TryReadLine(surface, history, Pump(
            Char('a'), Key(ConsoleKey.Enter)), out string line);

        Assert.Equal("a", line);
        Assert.Equal(["a"], surface.Redraws);
    }
}
