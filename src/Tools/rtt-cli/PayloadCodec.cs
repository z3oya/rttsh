using Toolbox.Core.SerialComm;

namespace Toolbox.Tools.RttCli;

/// <summary>TX payload building shared by send, monitor and script: --hex parsing, C-style escape
/// interpretation, text encoding, and the EOL terminator. Pure functions; the callers own the
/// transport writes (monitor's TX is always text + EOL - --hex only affects RX rendering there).</summary>
internal static class PayloadCodec
{
    /// <summary>Encodes a send payload: --hex parses raw bytes (bad text is a usage error naming
    /// the option), otherwise the text is unescaped and encoded.</summary>
    public static byte[] Encode(string payload, TextEncodingKind encoding, bool hex)
    {
        if (hex)
        {
            if (!HexCodec.TryParse(payload, out byte[] bytes, out string? error))
                throw new UsageException($"--hex: {error}");
            return bytes;
        }
        return TextCodec.Resolve(encoding).GetBytes(UnescapeSendText(payload));
    }

    /// <summary>The line terminator as encoded bytes; absent --eol defaults to Lf.</summary>
    public static byte[] Terminator(TextEol? eol, TextEncodingKind encoding) =>
        TextCodec.Resolve(encoding).GetBytes(EolText(eol ?? TextEol.Lf));

    /// <summary>Interprets \n \r \t \\ in send text: shell quoting for a literal newline varies
    /// (PowerShell needs `n), and C-style escapes are what users naturally type. Unknown
    /// escapes stay as-is.</summary>
    public static string UnescapeSendText(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\' || i + 1 >= text.Length)
            {
                sb.Append(c);
                continue;
            }
            char next = text[++i];
            switch (next)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '\\': sb.Append('\\'); break;
                default: sb.Append(c).Append(next); break;
            }
        }
        return sb.ToString();
    }

    private static string EolText(TextEol eol) => eol switch
    {
        TextEol.Lf => "\n",
        TextEol.Cr => "\r",
        TextEol.CrLf => "\r\n",
        TextEol.None => "",
        _ => "\n",
    };
}
