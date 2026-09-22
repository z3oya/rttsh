namespace Toolbox.Tools.RttCli;

/// <summary>The list-devices command: dump the DLL device database through a fresh JLinkLibrary.
/// No RTT link, no target lock - only the DLL load and the enumeration.</summary>
internal static class DeviceListing
{
    public static int Run(ListDevicesCommand command)
    {
        using var library = new JLinkLibrary();
        if (!library.Load(command.DllPath, out string error))
        {
            Console.Error.WriteLine($"rtt-cli: {error}");
            return 1;
        }
        if (!JLinkDeviceDatabase.TryEnumerate(library, out List<JLinkDeviceRecord> records, out error))
        {
            Console.Error.WriteLine($"rtt-cli: {error}");
            return 1;
        }

        if (command.Filter is { Length: > 0 } filter)
        {
            records = records.Where(r => ContainsIgnoreCase(r.Name, filter)
                || ContainsIgnoreCase(r.Manufacturer, filter)
                || ContainsIgnoreCase(r.Core, filter)).ToList();
        }
        DeviceTable.Format(records, Console.Out);
        Console.Error.WriteLine($"{records.Count} device(s).");
        return 0;
    }

    private static bool ContainsIgnoreCase(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
