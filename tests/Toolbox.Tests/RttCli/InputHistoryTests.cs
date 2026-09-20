using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

public class InputHistoryTests
{
    [Fact]
    public void TryOlder_recalls_the_most_recent_line_first()
    {
        var history = new InputHistory();
        history.Record("led");
        history.Record("tick");

        Assert.True(history.TryOlder("live", out string recalled));
        Assert.Equal("tick", recalled);
    }

    [Fact]
    public void TryOlder_walks_to_the_oldest_and_stops_there()
    {
        var history = new InputHistory();
        history.Record("a");
        history.Record("b");
        history.Record("c");

        history.TryOlder("live", out string first);
        history.TryOlder(first, out string second);
        history.TryOlder(second, out string third);
        bool clamped = history.TryOlder(third, out string _);

        Assert.Equal("c", first);
        Assert.Equal("b", second);
        Assert.Equal("a", third);
        Assert.False(clamped);
    }

    [Fact]
    public void TryOlder_on_empty_history_reports_nothing()
    {
        var history = new InputHistory();
        Assert.False(history.TryOlder("live", out string recalled));
        Assert.Equal("", recalled);
    }

    [Fact]
    public void TryNewer_at_the_live_position_reports_nothing()
    {
        var history = new InputHistory();
        history.Record("a");

        Assert.False(history.TryNewer(out string recalled));
        Assert.Equal("", recalled);
    }

    [Fact]
    public void Walking_the_full_range_terminates_exactly()
    {
        var history = new InputHistory();
        const int count = 200;
        for (int i = 0; i < count; i++)
            history.Record($"cmd {i}");

        for (int i = 0; i < count; i++)
        {
            Assert.True(history.TryOlder("typed", out string recalled));
            Assert.Equal($"cmd {count - 1 - i}", recalled);
        }
        Assert.False(history.TryOlder("typed", out string _));   // one past the oldest

        for (int i = 1; i < count; i++)
        {
            Assert.True(history.TryNewer(out string recalled));
            Assert.Equal($"cmd {i}", recalled);
        }
        Assert.True(history.TryNewer(out string draft));          // the last step restores the draft
        Assert.Equal("typed", draft);
        Assert.False(history.TryNewer(out string _));             // one past the newest
    }

    [Fact]
    public void Up_down_cycles_keep_the_draft()
    {
        var history = new InputHistory();
        history.Record("a");

        for (int cycle = 0; cycle < 3; cycle++)
        {
            Assert.True(history.TryOlder("typed", out string recalled));
            Assert.Equal("a", recalled);
            Assert.True(history.TryNewer(out string restored));
            Assert.Equal("typed", restored);
        }
    }

    [Fact]
    public void Duplicate_submit_after_navigation_resets_without_growing()
    {
        var history = new InputHistory();
        history.Record("x");
        history.Record("y");
        history.TryOlder("live", out string y);
        history.TryOlder(y, out string x);

        history.Record(y);   // re-sent the recalled newest: a duplicate

        Assert.False(history.TryNewer(out _));   // position reset to live anyway
        history.TryOlder("live", out string again);
        history.TryOlder(again, out string older);
        Assert.Equal("y", again);
        Assert.Equal("x", older);
        Assert.False(history.TryOlder(older, out _));   // and still exactly two entries
    }

    [Fact]
    public void Non_consecutive_duplicates_are_both_kept()
    {
        var history = new InputHistory();
        history.Record("tick");
        history.Record("led");
        history.Record("tick");

        history.TryOlder("live", out string newest);
        history.TryOlder(newest, out string middle);
        history.TryOlder(middle, out string oldest);
        bool past = history.TryOlder(oldest, out string _);

        Assert.Equal("tick", newest);
        Assert.Equal("led", middle);
        Assert.Equal("tick", oldest);
        Assert.False(past);   // exactly three entries
    }

    [Fact]
    public void Whitespace_only_lines_are_kept()
    {
        var history = new InputHistory();
        history.Record("tick  ");
        history.Record("   ");

        history.TryOlder("live", out string newest);
        history.TryOlder(newest, out string older);
        bool past = history.TryOlder(older, out string _);

        Assert.Equal("   ", newest);
        Assert.Equal("tick  ", older);
        Assert.False(past);
    }

    [Fact]
    public void Long_and_unicode_lines_are_recalled_exactly()
    {
        var history = new InputHistory();
        string line = new string('x', 4096) + "结束标记";
        history.Record(line);

        Assert.True(history.TryOlder("live", out string recalled));
        Assert.Equal(line, recalled);
        Assert.False(history.TryOlder(recalled, out _));
    }

    [Fact]
    public void TryNewer_walks_forward_and_restores_the_draft()
    {
        var history = new InputHistory();
        history.Record("a");
        history.Record("b");

        history.TryOlder("live", out string b);
        history.TryOlder(b, out string a);
        history.TryNewer(out string forward);
        history.TryNewer(out string draft);
        bool pastEnd = history.TryNewer(out string _);

        Assert.Equal("a", a);
        Assert.Equal("b", forward);
        Assert.Equal("live", draft);
        Assert.False(pastEnd);
    }

    [Fact]
    public void Draft_is_captured_only_when_leaving_the_live_position()
    {
        var history = new InputHistory();
        history.Record("a");
        history.Record("b");

        history.TryOlder("typed", out string b);
        history.TryOlder(b, out string a);
        history.TryNewer(out string forward);   // one step up from the oldest: "b"
        history.TryNewer(out string draft);     // past the newest: the saved draft

        Assert.Equal("a", a);
        Assert.Equal("b", forward);
        Assert.Equal("typed", draft);
    }

    [Fact]
    public void Record_skips_empty_lines()
    {
        var history = new InputHistory();
        history.Record("a");
        history.Record("");

        Assert.True(history.TryOlder("live", out string recalled));
        Assert.Equal("a", recalled);          // "" never became the newest entry
        Assert.False(history.TryOlder(recalled, out _));   // ...and nothing older exists
    }

    [Fact]
    public void Record_skips_consecutive_duplicates()
    {
        var history = new InputHistory();
        history.Record("tick");
        history.Record("tick");

        Assert.True(history.TryOlder("live", out string recalled));
        Assert.Equal("tick", recalled);
        Assert.False(history.TryOlder(recalled, out _));   // a stored duplicate would answer true
    }

    [Fact]
    public void Record_resets_the_navigation_position()
    {
        var history = new InputHistory();
        history.Record("a");
        history.Record("b");
        history.TryOlder("live", out string b);
        history.TryOlder(b, out _);

        history.Record("c");

        Assert.False(history.TryNewer(out _));
        Assert.True(history.TryOlder("live", out string newest));
        Assert.Equal("c", newest);
    }

    [Fact]
    public void Edits_to_a_recalled_line_give_way_to_the_saved_draft()
    {
        var history = new InputHistory();
        history.Record("x");

        history.TryOlder("draft", out string recalled);
        history.TryNewer(out string restored);       // user recalled "x", edited it, came back down
        history.TryOlder("draft", out string again);

        Assert.Equal("draft", restored);
        Assert.Equal("x", again);
    }
}
