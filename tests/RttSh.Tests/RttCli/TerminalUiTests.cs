using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

/// <summary>The input-row layout math (TerminalUi.ComputeWindow / FastAppendFits - pure, no
/// console): cell budgets hold for CJK double-width chars, the window slides minimally, and
/// stale inputs self-heal.
///
/// Deliberately out of reach here: TerminalUi's state maintenance around those pure functions
/// (_lastCaret/_windowStart upkeep, the caret rides on fast appends, log-burst redraws,
/// resize) and the VT sequences themselves - they need a real console. Manual smoke checklist
/// after touching TerminalUi: type to a full row in ASCII and in 中文 and keep typing past
/// it; trigger RX output while typing and while the caret sits mid-line; walk a long line
/// with Left/Right/Home/End; recall a long line with Up; resize the window mid-session;
/// confirm Enter still echoes into the log and clears the row.</summary>
public class TerminalUiTests
{
    [Fact]
    public void A_fitting_buffer_shows_whole_with_the_caret_after_the_last_char()
    {
        Assert.Equal((0, "ab cd", 8), TerminalUi.ComputeWindow(20, "ab cd", 5, 0));
    }

    [Fact]
    public void The_caret_column_counts_a_wide_char_as_two_cells()
    {
        // "你a好" paints 2+1+2 cells; caret between 'a' and '好' sits in column 6
        Assert.Equal((0, "你a好", 6), TerminalUi.ComputeWindow(20, "你a好", 2, 0));
    }

    [Fact]
    public void An_astral_emoji_counts_two_cells()
    {
        string emoji = "\U0001F600";   // one surrogate pair = two chars, two cells
        Assert.Equal((0, emoji, 5), TerminalUi.ComputeWindow(20, emoji, 2, 0));
    }

    [Fact]
    public void Combining_marks_are_zero_width()
    {
        // e + combining acute paints one cell; the caret after it sits in column 4
        Assert.Equal((0, "e\u0301", 4), TerminalUi.ComputeWindow(20, "e\u0301", 2, 0));
    }

    [Fact]
    public void The_tail_window_slides_minimally()
    {
        const int width = 10;   // usable 7 cells

        Assert.Equal((1, "bcdefgh", 10), TerminalUi.ComputeWindow(width, "abcdefgh", 8, 0));
        Assert.Equal((1, "bcdefgh", 9), TerminalUi.ComputeWindow(width, "abcdefgh", 7, 1));   // caret inside: no move
        Assert.Equal((0, "abcdefg", 3), TerminalUi.ComputeWindow(width, "abcdefgh", 0, 1));   // caret left: slide left
    }

    [Fact]
    public void The_window_slides_rather_than_splitting_a_wide_char()
    {
        // usable 6 cells: "abc你" fits, the second 你 cannot straddle the edge - the 'a' goes instead
        Assert.Equal((1, "bc你你", 9), TerminalUi.ComputeWindow(9, "abc你你", 5, 0));
    }

    [Fact]
    public void A_stale_window_start_snaps_back_after_the_buffer_shrank()
    {
        Assert.Equal((0, "ab", 5), TerminalUi.ComputeWindow(20, "ab", 2, 50));
    }

    [Fact]
    public void An_out_of_range_caret_is_clamped()
    {
        Assert.Equal((0, "ab", 5), TerminalUi.ComputeWindow(20, "ab", 99, 0));
    }

    [Fact]
    public void A_degenerate_row_stays_in_bounds_with_wide_chars()
    {
        // usable clamps to 1 cell: the wide char cannot be shown, the caret stays in row bounds
        Assert.Equal((1, "", 3), TerminalUi.ComputeWindow(4, "你", 1, 0));
        Assert.Equal((0, "a", 4), TerminalUi.ComputeWindow(4, "a", 1, 0));   // narrow fits; caret on the last column
    }

    [Fact]
    public void Fast_append_fits_only_within_the_cell_budget()
    {
        Assert.True(TerminalUi.FastAppendFits(10, "abcdef", 0, 'g'));      // 6 cells + 1 = 7 = usable
        Assert.False(TerminalUi.FastAppendFits(10, "abcdefg", 0, 'h'));    // the row is full in cells
        Assert.False(TerminalUi.FastAppendFits(10, "abcdefgh", 0, 'x'));   // full AND the window would slide
    }

    [Fact]
    public void Fast_append_counts_a_wide_char_as_two_cells()
    {
        Assert.True(TerminalUi.FastAppendFits(10, "abc你", 0, '你'));       // 5 + 2 = 7 = usable
        Assert.False(TerminalUi.FastAppendFits(10, "abc你你", 0, 'a'));    // full in cells before full in chars
    }
}
