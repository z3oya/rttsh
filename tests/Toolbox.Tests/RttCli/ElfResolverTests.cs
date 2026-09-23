using Toolbox.Core.Rtt.Elf;
using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

/// <summary>--elf wiring, red first: value/parse errors, every policy fallback, the fatal
/// file-level errors, then the green pins (address override, range clearing, config merge).
/// The explicit-address-wins case proves the "no IO" contract by passing a load delegate that
/// fails the test if it is ever invoked; Apply-level cases use real temp files.</summary>
public class ElfResolverTests
{
    private const string CbName = "_SEGGER_RTT";

    private static byte[] ImageWithCb(uint address = ElfTestSupport.KeilCbAddress, uint size = ElfTestSupport.KeilCbSize) =>
        new ElfBuilder
        {
            Symbols = { new ElfBuilder.Sym(CbName, address, size, ElfBuilder.TypeObject, ElfBuilder.BindGlobal) },
        }.Build();

    private static string TempFile(string name, byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ---- red: the CLI surface ----------------------------------------------------------

    [Fact]
    public void Parse_rejects_a_valueless_elf_option()
    {
        UsageException ex = Assert.Throws<UsageException>(() => CommandLine.Parse(["--elf"]));
        Assert.Contains("--elf: missing file value", ex.Message);
    }

    [Fact]
    public void Parse_carries_the_elf_path()
    {
        RttCommand command = CommandLine.Parse(["--elf", "fw.elf", "send", "hi"]);
        SendCommand send = Assert.IsType<SendCommand>(command);
        Assert.Equal("fw.elf", send.Options.ElfPath);
    }

    // ---- red: Decide policy fallbacks --------------------------------------------------

    [Fact]
    public void Explicit_address_wins_without_loading_the_file()
    {
        Func<ElfImage> load = () => throw new InvalidOperationException("the file must not be read");

        ElfResolver.ElfDecision decision = ElfResolver.Decide(0x2000_0000, explicitRange: false, "fw.elf", load);

        Assert.False(decision.Override);
        Assert.Contains("ignored", decision.Message);
        Assert.Contains("0x20000000", decision.Message);
    }

    [Fact]
    public void An_image_without_the_symbol_degrades_to_the_scan()
    {
        ElfImage image = ElfImage.Load(new ElfBuilder().Build());   // no _SEGGER_RTT symbol
        ElfResolver.ElfDecision decision = ElfResolver.Decide(null, false, "fw.elf", () => image);
        Assert.False(decision.Override);
        Assert.Contains("not in 'fw.elf'", decision.Message);
        Assert.Contains("falling back", decision.Message);
    }

    [Fact]
    public void An_ambiguous_symbol_degrades_to_the_scan()
    {
        ElfImage image = ElfImage.Load(new ElfBuilder
        {
            Symbols =
            {
                new ElfBuilder.Sym(CbName, 0x2400_0000, 168, ElfBuilder.TypeObject, ElfBuilder.BindLocal),
                new ElfBuilder.Sym(CbName, 0x2400_1000, 168, ElfBuilder.TypeObject, ElfBuilder.BindGlobal),
            },
        }.Build());
        ElfResolver.ElfDecision decision = ElfResolver.Decide(null, false, "fw.elf", () => image);
        Assert.False(decision.Override);
        Assert.Contains("ambiguous", decision.Message);
    }

    [Fact]
    public void An_implausible_control_block_degrades_to_the_scan()
    {
        ElfImage image = ElfImage.Load(ImageWithCb(size: 999));
        ElfResolver.ElfDecision decision = ElfResolver.Decide(null, false, "fw.elf", () => image);
        Assert.False(decision.Override);
        Assert.Contains("implausible", decision.Message);
    }

    [Fact]
    public void An_unparsable_image_is_a_usage_error()
    {
        ElfImage image = ElfImage.Load([0x7f, (byte)'E', (byte)'L', (byte)'F', 1, 0, 0]);   // valid magic, truncated
        UsageException ex = Assert.Throws<UsageException>(
            () => ElfResolver.Decide(null, false, "fw.elf", () => image));
        Assert.Contains("--elf: cannot use 'fw.elf'", ex.Message);
    }

    // ---- green: Decide pins the resolved address ---------------------------------------

    [Fact]
    public void A_resolved_control_block_is_pinned()
    {
        ElfImage image = ElfImage.Load(ImageWithCb());
        ElfResolver.ElfDecision decision = ElfResolver.Decide(null, explicitRange: false, "fw.elf", () => image);
        Assert.True(decision.Override);
        Assert.Equal(ElfTestSupport.KeilCbAddress, decision.Address);
        Assert.False(decision.ClearRange);
        Assert.Contains("0x24000070", decision.Message);
        Assert.Contains("(from 'fw.elf')", decision.Message);
    }

    [Fact]
    public void A_resolved_control_block_clears_a_user_range()
    {
        ElfImage image = ElfImage.Load(ImageWithCb());
        ElfResolver.ElfDecision decision = ElfResolver.Decide(null, explicitRange: true, "fw.elf", () => image);
        Assert.True(decision.ClearRange);
        Assert.Contains("--rtt-range ignored", decision.Message);
    }

    // ---- Apply: the mechanical shell over real files -----------------------------------

    [Fact]
    public void Apply_pins_the_resolved_address_and_clears_the_range()
    {
        string path = TempFile("elf-resolver-green.elf", ImageWithCb());
        var options = new CommandLineOptions { Chip = "STM32H743XI", ElfPath = path, RttRange = 0x100 };

        ElfResolver.Apply(options);

        Assert.Equal(ElfTestSupport.KeilCbAddress, options.RttAddress);
        Assert.Null(options.RttRange);
    }

    [Fact]
    public void Apply_with_an_explicit_address_never_touches_the_file()
    {
        // the path does not exist: if the shell ever tried to read it, this test would throw
        var options = new CommandLineOptions
        {
            Chip = "STM32H743XI",
            ElfPath = Path.Combine(Path.GetTempPath(), "elf-resolver-must-not-read.elf"),
            RttAddress = 0x2000_0000,
        };

        ElfResolver.Apply(options);

        Assert.Equal(0x2000_0000u, options.RttAddress);
    }

    [Fact]
    public void Apply_without_elf_is_a_silent_noop()
    {
        var options = new CommandLineOptions { Chip = "STM32H743XI" };
        ElfResolver.Apply(options);
        Assert.Null(options.RttAddress);
    }

    [Fact]
    public void Apply_reports_a_missing_file_as_a_usage_error()
    {
        var options = new CommandLineOptions
        {
            Chip = "STM32H743XI",
            ElfPath = Path.Combine(Path.GetTempPath(), "elf-resolver-no-such-image.elf"),
        };
        UsageException ex = Assert.Throws<UsageException>(() => ElfResolver.Apply(options));
        Assert.Contains("--elf: cannot read", ex.Message);
    }

    [Fact]
    public void Apply_reports_a_non_elf_file_as_a_usage_error()
    {
        string path = TempFile("elf-resolver-junk.bin", [1, 2, 3, 4, 5, 6, 7, 8]);
        var options = new CommandLineOptions { Chip = "STM32H743XI", ElfPath = path };
        UsageException ex = Assert.Throws<UsageException>(() => ElfResolver.Apply(options));
        Assert.Contains("--elf: cannot use", ex.Message);
        Assert.Contains("magic", ex.Message);
    }

    [Fact]
    public void Apply_reports_an_elf64_image_as_a_usage_error()
    {
        string path = TempFile("elf-resolver-64.elf", new ElfBuilder { Elf64Header = true }.Build());
        var options = new CommandLineOptions { Chip = "STM32H743XI", ElfPath = path };
        UsageException ex = Assert.Throws<UsageException>(() => ElfResolver.Apply(options));
        Assert.Contains("ELF64", ex.Message);
    }

    // ---- config merge: "elf" is an ordinary fourteenth key ------------------------------

    [Fact]
    public void Config_elf_fills_the_option_when_the_cli_left_it_unset()
    {
        var options = new CommandLineOptions { Chip = "STM32H743XI" };
        ConfigFile.ApplyTo(options, new ConfigValues { Elf = "from-config.elf" });
        Assert.Equal("from-config.elf", options.ElfPath);
    }

    [Fact]
    public void The_cli_elf_path_beats_the_config_file()
    {
        var options = new CommandLineOptions { Chip = "STM32H743XI", ElfPath = "from-cli.elf" };
        ConfigFile.ApplyTo(options, new ConfigValues { Elf = "from-config.elf" });
        Assert.Equal("from-cli.elf", options.ElfPath);
    }
}
