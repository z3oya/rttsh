using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

/// <summary>PrimaryFlashBytes picks the on-chip flash chain out of the DLL's bank list. Every
/// pattern here is a live database layout (STM32H743ZI, AT32A403A, R7FA4E10D, CS32F103C8,
/// TLE9842) captured via JLINKARM_DEVICE_GetInfo on a real DLL.</summary>
public class JLinkDeviceDatabaseTests
{
    private static JLinkNative.FlashArea Area(uint address, uint size) => new() { Address = address, Size = size };

    [Fact]
    public void Sums_the_contiguous_internal_chain_and_skips_the_external_qspi_window()
    {
        var areas = new[] { Area(0x0800_0000, 0x10_0000), Area(0x0810_0000, 0x10_0000), Area(0x9000_0000, 0x1000_0000) };
        Assert.Equal(0x20_0000u, JLinkDeviceDatabase.PrimaryFlashBytes(0x1020_0000, areas));
    }

    [Fact]
    public void Merges_dual_bank_halves_across_the_hole_but_not_the_qspi_window()
    {
        // STM32H743ZG: two 512KB banks spaced a hole apart, 256MB QSPI window far out.
        var areas = new[] { Area(0x0800_0000, 0x8_0000), Area(0x0810_0000, 0x8_0000), Area(0x9000_0000, 0x1000_0000) };
        Assert.Equal(0x10_0000u, JLinkDeviceDatabase.PrimaryFlashBytes(0x1010_0000, areas));
    }

    [Fact]
    public void Stops_at_gaps_so_stray_and_external_banks_are_excluded()
    {
        var areas = new[] { Area(0x0800_0000, 0x4_0000), Area(0x1FFF_F800, 0x30), Area(0x0840_0000, 0x100_0000) };
        Assert.Equal(0x4_0000u, JLinkDeviceDatabase.PrimaryFlashBytes(0x1040_030, areas));
    }

    [Fact]
    public void Anchors_on_the_lowest_real_bank_not_the_dll_flash_address()
    {
        var areas = new[] { Area(0x0100_A100, 0x200), Area(0x0000_0000, 0x8_0000), Area(0x0800_0000, 0x2000), Area(0x6000_0000, 0x400_0000) };
        Assert.Equal(0x8_0000u, JLinkDeviceDatabase.PrimaryFlashBytes(0x408_2200, areas));
    }

    [Fact]
    public void Skips_leading_option_banks_below_64_bytes()
    {
        var areas = new[] { Area(0x0600_0000, 0x10), Area(0x0800_0000, 0x2_0000) };
        Assert.Equal(0x2_0000u, JLinkDeviceDatabase.PrimaryFlashBytes(0x2_0010, areas));
    }

    [Fact]
    public void Ignores_tiny_leading_banks_even_when_they_abut_across_the_address_wrap()
    {
        var areas = new[] { Area(0x10FF_FFFC, 0x4), Area(0x1100_0000, 0x8000) };
        Assert.Equal(0x8000u, JLinkDeviceDatabase.PrimaryFlashBytes(0x9004, areas));
    }

    [Fact]
    public void Falls_back_to_the_dll_total_without_any_bank_data()
    {
        var areas = new JLinkNative.FlashArea[32];
        Assert.Equal(0x2_0000u, JLinkDeviceDatabase.PrimaryFlashBytes(0x2_0000, areas));
    }

    [Fact]
    public void Falls_back_to_the_dll_total_when_only_stray_banks_remain()
    {
        var areas = new[] { Area(0x0600_0000, 0x10) };
        Assert.Equal(0x10u, JLinkDeviceDatabase.PrimaryFlashBytes(0x10, areas));
    }
}
