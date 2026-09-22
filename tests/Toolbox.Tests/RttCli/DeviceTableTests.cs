using System.Text;
using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

public class DeviceTableTests
{
    private static string Render(params JLinkDeviceRecord[] records)
    {
        var writer = new StringWriter { NewLine = "\n" };
        DeviceTable.Format([.. records], writer);
        return writer.ToString();
    }

    [Fact]
    public void Header_and_one_padded_row_per_record()
    {
        string table = Render(
            new JLinkDeviceRecord("STM32F103C8", "ST", "Cortex-M3", 64 * 1024, 20 * 1024),
            new JLinkDeviceRecord("nRF52840", "Nordic Semi", "Cortex-M4", 0, 256 * 1024));
        string[] lines = table.Split('\n');
        Assert.Equal("", lines[^1]);   // table ends with a newline
        Assert.StartsWith("NAME", lines[0]);
        Assert.Contains("MANUFACTURER", lines[0]);
        Assert.Contains("CORE", lines[0]);
        Assert.Contains("FLASH", lines[0]);
        Assert.Contains("RAM", lines[0]);
        Assert.Contains("64K", lines[1]);
        Assert.Contains("20K", lines[1]);
        Assert.Contains("       -", lines[2]);   // 0 bytes renders right-aligned as "-"
        Assert.Contains("256K", lines[2]);
    }

    [Fact]
    public void Name_column_clamps_at_8_for_short_names()
    {
        string table = Render(new JLinkDeviceRecord("x", "m", "c", 1, 1));
        // "NAME" padded to the 8-char floor, then the single-column separator
        Assert.StartsWith("NAME     MANUFACTURER", table);
        Assert.StartsWith("x        m", table.Split('\n')[1]);   // padded to 8 + 1 separator space
    }

    [Fact]
    public void Names_past_40_chars_are_truncated_with_an_ellipsis()
    {
        string table = Render(new JLinkDeviceRecord(new string('A', 41), "m", "c", 0, 0));
        string row = table.Split('\n')[1];
        Assert.Equal(new string('A', 39) + "…", row[..40]);
    }

    [Fact]
    public void Sizes_render_raw_below_1k_and_in_m_above()
    {
        string table = Render(new JLinkDeviceRecord("dev", "m", "c", 5 * 1024 * 1024, 1023));
        string row = table.Split('\n')[1];
        Assert.Contains("5M", row);
        Assert.Contains("1023", row);
    }
}
