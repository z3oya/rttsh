using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

/// <summary>FlashImage's sniffing and --addr pairing rules, exercised through temp files so the
/// header-read loop runs for real.</summary>
public class FlashImageTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rtt-flash-test-{Guid.NewGuid():N}.bin");

    private FlashImageFormat Sniff(params byte[] bytes)
    {
        File.WriteAllBytes(_path, bytes);
        return FlashImage.Sniff(_path);
    }

    [Fact]
    public void Elf_magic_sniffs_as_elf()
    {
        var bytes = new List<byte> { 0x7F, (byte)'E', (byte)'L', (byte)'F', 0x02, 0x01, 0x01, 0x00 };
        bytes.AddRange(new byte[64]);
        Assert.Equal(FlashImageFormat.Elf, Sniff([.. bytes]));
    }

    [Fact]
    public void Intel_hex_start_code_sniffs_as_hex()
    {
        byte[] line = ":100000002C"u8.ToArray();
        Assert.Equal(FlashImageFormat.IntelHex, Sniff(line));
    }

    [Fact]
    public void S_record_start_code_sniffs_as_srec()
    {
        // "S0" header record + payload: the .mot/.srec family the DLL accepts.
        byte[] line = "S00600004844521B"u8.ToArray();
        Assert.Equal(FlashImageFormat.Srec, Sniff(line));
        Assert.Equal(FlashImageFormat.Srec, Sniff("S31508000000"u8.ToArray()));   // an S3 data record
        // 'S' alone (no record-type digit) stays raw; so does a lone 'S' byte file.
        Assert.Equal(FlashImageFormat.Raw, Sniff((byte)'S'));
    }

    [Fact]
    public void Anything_else_sniffs_as_raw()
    {
        Assert.Equal(FlashImageFormat.Raw, Sniff(0xE9, 0x00, 0x10, 0x00));   // a Cortex-M vector
        Assert.Equal(FlashImageFormat.Raw, Sniff(0x00));
    }

    [Fact]
    public void Empty_file_is_a_usage_error()
    {
        File.WriteAllBytes(_path, []);
        var ex = Assert.Throws<UsageException>(() => FlashImage.Sniff(_path));
        Assert.Contains("is empty", ex.Message);
    }

    [Fact]
    public void Missing_file_is_a_usage_error_with_the_path()
    {
        Assert.Throws<UsageException>(() => FlashImage.Sniff(_path));
    }

    [Fact]
    public void Raw_requires_an_address_and_keeps_it()
    {
        Assert.Contains("--addr", Assert.Throws<UsageException>(
            () => FlashImage.ValidateAddress(FlashImageFormat.Raw, null, "app.bin")).Message);
        Assert.Equal(0x0800_0000u, FlashImage.ValidateAddress(FlashImageFormat.Raw, 0x0800_0000, "app.bin"));
    }

    [Fact]
    public void Self_addressed_formats_reject_an_explicit_address()
    {
        foreach (var format in new[] { FlashImageFormat.Elf, FlashImageFormat.IntelHex, FlashImageFormat.Srec })
        {
            var ex = Assert.Throws<UsageException>(() => FlashImage.ValidateAddress(format, 0x0800_0000, "app.hex"));
            Assert.Contains("--addr", ex.Message);
            Assert.Contains("carries its own", ex.Message);
        }
        // without an address they are fine, yielding 0 (the DLL ignores it anyway)
        Assert.Equal(0u, FlashImage.ValidateAddress(FlashImageFormat.Elf, null, "app.elf"));
        Assert.Equal(0u, FlashImage.ValidateAddress(FlashImageFormat.IntelHex, null, "app.hex"));
        Assert.Equal(0u, FlashImage.ValidateAddress(FlashImageFormat.Srec, null, "app.mot"));
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* best effort */ }
    }
}
