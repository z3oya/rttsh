using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

/// <summary>Terminal cell widths: CJK/kana/hangul/fullwidth paint two cells, combining marks
/// and format chars none, and a surrogate pair totals two.</summary>
public class DisplayWidthTests
{
    [Theory]
    [InlineData('a', 1)]
    [InlineData('你', 2)]          // CJK unified ideograph
    [InlineData('あ', 2)]          // hiragana
    [InlineData('한', 2)]          // hangul syllable
    [InlineData('Ａ', 2)]          // fullwidth A
    [InlineData('、', 2)]          // CJK punctuation
    [InlineData('\u0301', 0)]      // combining acute accent
    [InlineData('\u200D', 0)]      // zero-width joiner (format char)
    [InlineData('\u115F', 2)]      // last wide Hangul jamo initial: the table's low boundary
    [InlineData('\u1160', 1)]      // jamo vowel (conjoining): coarse 1, documented
    [InlineData('\uFF60', 2)]      // last fullwidth form
    [InlineData('\uFF61', 1)]      // halfwidth ideographic comma
    public void Char_widths_follow_east_asian_width(char c, int width) =>
        Assert.Equal(width, DisplayWidth.Of(c));

    [Fact]
    public void A_surrogate_pair_totals_two_cells()
    {
        string emoji = "\U0001F600";
        Assert.Equal(2, DisplayWidth.OfRange(emoji, 0, emoji.Length));
    }

    [Fact]
    public void Ranges_sum_their_chars()
    {
        Assert.Equal(4, DisplayWidth.OfRange("a你b", 0, 3));   // 1 + 2 + 1
    }
}
