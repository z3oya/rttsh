using System.Text;

namespace RttSh.Core.SerialComm;

/// <summary>Text encodings offered by the UI. Gbk (cp936) additionally requires the
/// System.Text.Encoding.CodePages provider, registered by the exe and the tests before first use.</summary>
public enum TextEncodingKind { Utf8, Ascii, Latin1, Gbk }

public static class TextCodec
{
    /// <summary>Resolves the kind to a BCL encoding; note ASCII maps &gt;0x7F to '?' (use Latin1 for lossless single bytes).</summary>
    public static Encoding Resolve(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Ascii => Encoding.ASCII,
        TextEncodingKind.Utf8 => Encoding.UTF8,
        TextEncodingKind.Latin1 => Encoding.Latin1,
        TextEncodingKind.Gbk => Encoding.GetEncoding(936),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
