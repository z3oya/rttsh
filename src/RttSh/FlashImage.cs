namespace Toolbox.Tools.RttCli;

/// <summary>Flash image format sniffing and the --addr pairing rules for flash download.
/// The DLL's JLINK_DownloadFile runs erase+program+verify from the file itself; hex/elf/mot
/// carry their own load addresses (the addr argument is ignored by the DLL), so rttsh insists
/// on the pairing up front - a raw .bin without --addr would land nowhere useful, and an
/// --addr next to a self-addressed format is almost always a hand-copied mistake the DLL
/// would silently drop.</summary>
internal enum FlashImageFormat { Elf, IntelHex, Srec, Raw }

internal static class FlashImage
{
    /// <summary>Sniffs the format from the leading bytes: \x7fELF magic, an Intel HEX start
    /// code, an S-record start code ('S' + record-type digit, the .mot/.srec family), or raw
    /// binary. The header read loops until the buffer stops growing - Stream.Read may legally
    /// return less than asked, and a short first read must not misclassify.</summary>
    public static FlashImageFormat Sniff(string path)
    {
        FileStream stream;
        try
        {
            stream = File.OpenRead(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
                                        or UnauthorizedAccessException or IOException)
        {
            throw new UsageException($"flash download: cannot open '{path}': {ex.Message}");
        }
        using (stream)
        {
            Span<byte> header = stackalloc byte[256];
            int got = 0;
            while (got < header.Length)
            {
                int n = stream.Read(header[got..]);
                if (n <= 0) break;
                got += n;
            }
            if (got == 0)
                throw new UsageException($"flash download: '{path}' is empty");
            if (got >= 4 && header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
                return FlashImageFormat.Elf;
            if (header[0] == (byte)':')
                return FlashImageFormat.IntelHex;
            // S-record: 'S' + a record-type digit (S0-S9). The two-byte check keeps a one-byte
            // file whose lone byte is 'S' from reading uninitialized stack as the digit.
            if (got >= 2 && header[0] == (byte)'S' && header[1] is >= (byte)'0' and <= (byte)'9')
                return FlashImageFormat.Srec;
            return FlashImageFormat.Raw;
        }
    }

    /// <summary>Validates the (format, --addr) pair and returns the download address: raw
    /// binary requires --addr, self-addressed formats reject it. Throws UsageException
    /// (exit 2) - this check runs before the probe is touched.</summary>
    public static uint ValidateAddress(FlashImageFormat format, uint? addr, string path) => format switch
    {
        FlashImageFormat.Raw when addr is null =>
            throw new UsageException($"flash download: '{path}' is a raw binary image and needs --addr (hex) to know where it loads"),
        FlashImageFormat.Raw => addr!.Value,
        _ when addr is not null =>
            throw new UsageException($"flash download: --addr does not apply to '{path}' (the image carries its own load addresses); drop the option"),
        _ => 0,
    };
}
