using System.Globalization;

namespace Toolbox.Tools.RttCli;

/// <summary>Terminal cell width per character. The console is cell-addressed, not
/// character-addressed: a CJK ideograph, kana or fullwidth form paints two cells (East Asian
/// Width W/F), a combining accent paints none - so caret columns and row budgets must count
/// cells, not chars. The editor keeps moving by chars; only the rendering side consults this.
///
/// Model: 0 for invisible chars (Mn/Me combining marks, Cf format chars like ZWJ), 2 for the
/// wide/fullwidth ranges below, 1 otherwise. A surrogate pair totals 2 (each half scores 1),
/// which is right for the common astral emoji. The CJK/fullwidth table is exact; the emoji
/// coverage is deliberately coarse (a few wide plane ranges) because terminals disagree among
/// themselves about emoji presentation, and ZWJ sequences render as several cells - good
/// enough for a typed command line, exactly right for Chinese input.</summary>
internal static class DisplayWidth
{
    /// <summary>Cell width of one char.</summary>
    public static int Of(char c)
    {
        switch (char.GetUnicodeCategory(c))
        {
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.EnclosingMark:
            case UnicodeCategory.SpacingCombiningMark:
            case UnicodeCategory.Format:
                return 0;                       // accents, ZWJ/ZWNJ, BOM, ...
            case UnicodeCategory.Surrogate:
                return 1;                       // half an astral point; a pair totals 2
        }
        return c >= 0x1100 && InWideRange(c) ? 2 : 1;
    }

    /// <summary>Cell width of <paramref name="text"/>[<paramref name="start"/>..<paramref name="end"/>).</summary>
    public static int OfRange(string text, int start, int end)
    {
        int cells = 0;
        for (int i = start; i < end; i++)
            cells += Of(text[i]);
        return cells;
    }

    private static bool InWideRange(char c)
    {
        foreach ((int lo, int hi) in WideRanges)
        {
            if (c < lo) return false;           // sorted ascending: past is narrow
            if (c <= hi) return true;
        }
        return false;
    }

    /// <summary>East Asian Width W+F, sorted ascending. CJK blocks, kana, hangul, compatibility
    /// and fullwidth forms are exact; the emoji entries are a coarse "these planes paint wide"
    /// cut, not per-codepoint truth.</summary>
    private static readonly (int Lo, int Hi)[] WideRanges =
    [
        (0x1100, 0x115F),                                           // Hangul jamo initials
        (0x231A, 0x231B), (0x2329, 0x232A),                         // watch, angle brackets
        (0x23E9, 0x23EC), (0x23F0, 0x23F0), (0x23F3, 0x23F3),
        (0x25FD, 0x25FE),
        (0x2614, 0x2615), (0x2648, 0x2653), (0x267F, 0x267F), (0x2693, 0x2693),
        (0x26A1, 0x26A1), (0x26AA, 0x26AB), (0x26BD, 0x26BE), (0x26C4, 0x26C5),
        (0x26CE, 0x26CE), (0x26D4, 0x26D4), (0x26EA, 0x26EA), (0x26F2, 0x26F3),
        (0x26F5, 0x26F5), (0x26FA, 0x26FA), (0x26FD, 0x26FD),
        (0x2705, 0x2705), (0x270A, 0x270B), (0x2728, 0x2728),
        (0x274C, 0x274C), (0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757),
        (0x2795, 0x2797), (0x27B0, 0x27B0), (0x27BF, 0x27BF),
        (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55),
        (0x2E80, 0x2E99), (0x2E9B, 0x2EF3),                         // CJK radicals
        (0x2F00, 0x2FD5), (0x2FF0, 0x2FFB),                         // Kangxi radicals, ideographic descriptors
        (0x3000, 0x303E),                                           // CJK symbols and punctuation, ideographic space
        (0x3041, 0x3096), (0x3099, 0x30FF),                         // hiragana, katakana
        (0x3105, 0x312F), (0x3131, 0x318E), (0x3190, 0x31E3),       // bopomofo, jamo compat, kanbun
        (0x31F0, 0x321E), (0x3220, 0x3247), (0x3250, 0x4DBF),       // kana ext, enclosed, CJK ext A + compat
        (0x4E00, 0xA48C),                                           // CJK unified ideographs through Yi
        (0xA490, 0xA4C6), (0xA960, 0xA97C),                         // Yi compat, Hangul jamo ext-A
        (0xAC00, 0xD7A3),                                           // Hangul syllables
        (0xF900, 0xFAFF),                                           // CJK compatibility ideographs
        (0xFE10, 0xFE19), (0xFE30, 0xFE52), (0xFE54, 0xFE66), (0xFE68, 0xFE6B),   // vertical/compat forms
        (0xFF00, 0xFF60), (0xFFE0, 0xFFE6),                         // fullwidth forms
        (0x16FE0, 0x16FE4), (0x16FF0, 0x16FF1),                     // Tangut, Nushu signs
        (0x17000, 0x187F7), (0x18800, 0x18CD5), (0x18D00, 0x18D08), // Tangut ideographs
        (0x1B000, 0x1B152), (0x1B164, 0x1B167), (0x1B170, 0x1B2FB), // kana supplement, Nushu
        (0x1F004, 0x1F004), (0x1F0CF, 0x1F0CF), (0x1F18E, 0x1F18E), // emoji: singles, then coarse planes
        (0x1F191, 0x1F19A), (0x1F200, 0x1F64F), (0x1F680, 0x1F6FF),
        (0x1F7E0, 0x1F7EB), (0x1F900, 0x1FAFF),
        (0x20000, 0x2FFFD), (0x30000, 0x3FFFD),                     // CJK extensions B+
    ];
}
