namespace Toolbox.Tools.RttCli;

/// <summary>list-devices' table: the NAME column sizes to the data (clamped 8-40), MANUFACTURER
/// and CORE are fixed-width, FLASH/RAM render as compact sizes. Writes to an injectable
/// TextWriter; never called with an empty list (enumeration fails before that).</summary>
internal static class DeviceTable
{
    public static void Format(List<JLinkDeviceRecord> records, TextWriter writer)
    {
        int nameWidth = Math.Clamp(records.Max(r => r.Name.Length), 8, 40);
        const int manuWidth = 14;
        const int coreWidth = 12;
        writer.WriteLine($"{"NAME".PadRight(nameWidth)} {"MANUFACTURER".PadRight(manuWidth)} {"CORE".PadRight(coreWidth)} {"FLASH",8} {"RAM",8}");
        foreach (var record in records)
        {
            writer.WriteLine($"{Truncate(record.Name, nameWidth).PadRight(nameWidth)} " +
                $"{Truncate(record.Manufacturer, manuWidth).PadRight(manuWidth)} " +
                $"{Truncate(record.Core, coreWidth).PadRight(coreWidth)} " +
                $"{SizeText(record.FlashBytes),8} {SizeText(record.RamBytes),8}");
        }
    }

    private static string SizeText(uint bytes) => bytes switch
    {
        0 => "-",
        < 1024 => bytes.ToString(),
        < 1024 * 1024 => $"{bytes / 1024}K",
        _ => $"{bytes / (1024 * 1024)}M",
    };

    private static string Truncate(string text, int width) =>
        text.Length <= width ? text : text[..(width - 1)] + "…";
}
