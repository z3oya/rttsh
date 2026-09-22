using Toolbox.Core.SerialComm;
using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

public class PayloadCodecTests
{
    // ---- UnescapeSendText ----

    [Theory]
    [InlineData("a\\nb", "a\nb")]
    [InlineData("a\\rb", "a\rb")]
    [InlineData("a\\tb", "a\tb")]
    [InlineData("a\\\\b", "a\\b")]
    public void Known_escapes_are_interpreted(string text, string expected) =>
        Assert.Equal(expected, PayloadCodec.UnescapeSendText(text));

    [Fact]
    public void Unknown_escape_stays_as_is() =>
        Assert.Equal("a\\qb", PayloadCodec.UnescapeSendText("a\\qb"));

    [Fact]
    public void Trailing_backslash_stays_as_is() =>
        Assert.Equal("a\\", PayloadCodec.UnescapeSendText("a\\"));

    [Fact]
    public void Plain_text_passes_through_untouched() =>
        Assert.Equal("hello world", PayloadCodec.UnescapeSendText("hello world"));

    // ---- Encode ----

    [Fact]
    public void Text_payload_is_unescaped_then_encoded()
    {
        Assert.Equal("a\nb"u8.ToArray(), PayloadCodec.Encode("a\\nb", TextEncodingKind.Utf8, hex: false));
        // Latin-1: one byte per char, lossless above 0x7F
        Assert.Equal((byte[])[0xE9], PayloadCodec.Encode("é", TextEncodingKind.Latin1, hex: false));
    }

    [Fact]
    public void Hex_payload_parses_raw_bytes() =>
        Assert.Equal((byte[])[0xDE, 0xAD], PayloadCodec.Encode("dead", TextEncodingKind.Utf8, hex: true));

    [Fact]
    public void Bad_hex_text_names_the_option()
    {
        var ex = Assert.Throws<UsageException>(() => PayloadCodec.Encode("zz", TextEncodingKind.Utf8, hex: true));
        Assert.Contains("--hex", ex.Message);
    }

    // ---- Terminator ----

    [Fact]
    public void Terminator_encodes_each_eol()
    {
        Assert.Equal("\n"u8.ToArray(), PayloadCodec.Terminator(TextEol.Lf, TextEncodingKind.Utf8));
        Assert.Equal("\r"u8.ToArray(), PayloadCodec.Terminator(TextEol.Cr, TextEncodingKind.Utf8));
        Assert.Equal("\r\n"u8.ToArray(), PayloadCodec.Terminator(TextEol.CrLf, TextEncodingKind.Utf8));
        Assert.Equal(Array.Empty<byte>(), PayloadCodec.Terminator(TextEol.None, TextEncodingKind.Utf8));
    }

    [Fact]
    public void Terminator_defaults_to_lf_when_eol_absent() =>
        Assert.Equal("\n"u8.ToArray(), PayloadCodec.Terminator(null, TextEncodingKind.Utf8));
}
