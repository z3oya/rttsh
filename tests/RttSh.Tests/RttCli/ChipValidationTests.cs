using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

public class ChipValidationTests
{
    private static readonly List<JLinkDeviceRecord> Database =
    [
        new("STM32F407VG", "ST", "Cortex-M4", 1_000_000, 192_000),
        new("STM32H743XI", "ST", "Cortex-M7", 2_000_000, 1_000_000),
        new("STM32H743XIH", "ST", "Cortex-M7", 2_000_000, 1_000_000),
        new("STM32H743ZI", "ST", "Cortex-M7", 2_000_000, 1_000_000),
        new("i.MX RT1064", "NXP", "Cortex-M7", 4_000_000, 1_000_000),
    ];

    [Fact]
    public void Exact_name_passes()
    {
        ChipValidation.EnsureKnownInDb(Database, "STM32H743XI");   // must not throw
    }

    [Fact]
    public void Case_insensitive_name_passes()
    {
        // The DLL resolves device names case-insensitively - the check must not reject
        // a spelling the DLL itself would accept.
        ChipValidation.EnsureKnownInDb(Database, "stm32h743xi");   // must not throw
    }

    [Fact]
    public void Unknown_name_throws_usage_with_the_name_and_a_list_devices_hint()
    {
        UsageException ex = Assert.Throws<UsageException>(
            () => ChipValidation.EnsureKnownInDb(Database, "STM32H74X"));
        Assert.Contains("STM32H74X", ex.Message);
        Assert.Contains("list-devices", ex.Message);
    }

    [Fact]
    public void Unknown_name_suggests_containing_names_in_database_order()
    {
        UsageException ex = Assert.Throws<UsageException>(
            () => ChipValidation.EnsureKnownInDb(Database, "STM32H743"));
        Assert.Matches(
            @"Similar names: STM32H743XI, STM32H743XIH, STM32H743ZI",
            ex.Message);
    }

    [Fact]
    public void Suggestions_are_capped_to_keep_the_error_on_one_line()
    {
        var wide = Enumerable.Range(1, 10)
            .Select(i => new JLinkDeviceRecord($"ACMESP{i}", "Acme", "Cortex-M0", 0, 0))
            .ToList();
        UsageException ex = Assert.Throws<UsageException>(
            () => ChipValidation.EnsureKnownInDb(wide, "ACMESP"));
        Assert.Contains("ACMESP6", ex.Message);        // the sixth suggestion is the last
        Assert.DoesNotContain("ACMESP7", ex.Message);  // the seventh is cut off
    }

    [Fact]
    public void Empty_database_throws_without_suggestions()
    {
        UsageException ex = Assert.Throws<UsageException>(
            () => ChipValidation.EnsureKnownInDb([], "STM32H743XI"));
        Assert.Contains("STM32H743XI", ex.Message);
        Assert.DoesNotContain("Similar names", ex.Message);
    }
}
