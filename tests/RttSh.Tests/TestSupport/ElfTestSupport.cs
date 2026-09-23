namespace Toolbox.Tests;

/// <summary>Locates the real-firmware fixtures. Tests must not depend on the working
/// directory: the csproj copies Fixtures\** next to the test assembly.</summary>
internal static class ElfTestSupport
{
    /// <summary>The Keil fixture's control-block ground truth (armlink .map): the one
    /// address/size pair the Elf tests quote, on the real image and on synthetic ones.</summary>
    public const uint KeilCbAddress = 0x2400_0070;
    public const uint KeilCbSize = 168;

    public static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Elf", name);

    public static string KeilAxf => FixturePath("stm32-project.axf");
    public static string GccElf => FixturePath("stm32-project.elf");
    public static string GccReadelfSnapshot => FixturePath("stm32-project.elf.readelf.txt");
}
