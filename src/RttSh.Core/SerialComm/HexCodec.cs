namespace RttSh.Core.SerialComm;

/// <summary>Tolerant hex byte parsing/formatting for user-facing text boxes.</summary>
public static class HexCodec
{
    /// <summary>Collects hex digits case-insensitively; whitespace/punctuation are separators; "0x"/"0X" is consumed
    /// as a prefix only at a byte boundary. A letter that is not a hex digit fails (likely typo); an odd digit count
    /// fails rather than silently padding. Empty/separator-only input parses to an empty array.</summary>
    public static bool TryParse(string text, out byte[] bytes, out string? error)
    {
        var parsed = new List<byte>();
        int nibbles = 0;
        int current = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            int digit = HexDigit(c);
            if (digit >= 0)
            {
                current = (current << 4) | digit;
                if (++nibbles == 2)
                {
                    parsed.Add((byte)current);
                    nibbles = 0;
                }
                continue;
            }
            // "0x" prefix: either the '0' is the single pending nibble (undo it) or a byte boundary follows it.
            if ((c == 'x' || c == 'X') && i > 0 && text[i - 1] == '0' && nibbles <= 1)
            {
                nibbles = 0;
                continue;
            }
            if (char.IsLetter(c))
            {
                bytes = Array.Empty<byte>();
                error = $"'{c}' is not a hex digit.";
                return false;
            }
            // Anything else (whitespace, punctuation, controls) acts as a separator.
        }
        if (nibbles != 0)
        {
            bytes = Array.Empty<byte>();
            error = "Odd number of hex digits; a byte needs two.";
            return false;
        }
        bytes = parsed.ToArray();
        error = null;
        return true;
    }

    /// <summary>Uppercase space-separated zero-padded pairs: {0xAA,0x05,0x10} → "AA 05 10". Empty → "".</summary>
    public static string Format(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return "";
        var pairs = new string[data.Length];
        for (int i = 0; i < data.Length; i++)
            pairs[i] = data[i].ToString("X2");
        return string.Join(' ', pairs);
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
