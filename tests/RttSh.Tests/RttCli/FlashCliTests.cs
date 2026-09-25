using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

/// <summary>The flash command surface: subcommand mapping, the bare-flash usage shape, help
/// rendering, and the erase confirmation gate (pure decision - no probe touched).</summary>
public class FlashCliTests
{
    [Fact]
    public void Bare_flash_is_a_usage_error()
    {
        var error = Assert.IsType<UsageErrorCommand>(CommandLine.Parse(["flash"]));
        Assert.Contains("missing subcommand", error.Message);
    }

    [Fact]
    public void Options_before_bare_flash_is_also_a_usage_error()
    {
        // odd shape: the subcommand is missing even though options come first
        var error = Assert.IsType<UsageErrorCommand>(CommandLine.Parse(["--chip", "X", "flash"]));
        Assert.Contains("missing subcommand", error.Message);
    }

    [Fact]
    public void Flash_download_without_a_file_is_a_usage_error()
    {
        var error = Assert.IsType<UsageErrorCommand>(CommandLine.Parse(["flash", "download"]));
        Assert.Contains("missing <file>", error.Message);
    }

    [Fact]
    public void Flash_download_maps_the_file_and_options()
    {
        var command = Assert.IsType<FlashDownloadCommand>(
            CommandLine.Parse(["flash", "download", "app.bin", "--chip", "STM32H743XI", "--addr", "0x08000000", "--reset"]));
        Assert.Equal("app.bin", command.FilePath);
        Assert.Equal("STM32H743XI", command.Options.Chip);
        Assert.Equal(0x0800_0000u, command.Options.Addr);
        Assert.True(command.Options.ResetOnConnect);
    }

    [Fact]
    public void Flash_erase_maps_and_carries_yes()
    {
        var command = Assert.IsType<FlashEraseCommand>(CommandLine.Parse(["flash", "erase", "--chip", "X", "--yes"]));
        Assert.Equal("X", command.Options.Chip);
        Assert.True(command.Options.Yes);
        Assert.False(Assert.IsType<FlashEraseCommand>(CommandLine.Parse(["flash", "erase", "--chip", "X"])).Options.Yes);
    }

    [Fact]
    public void Flash_help_renders_the_flash_page_not_the_root()
    {
        var group = Assert.IsType<HelpCommand>(CommandLine.Parse(["flash", "--help"]));
        Assert.Contains("flash download", group.Text);
        Assert.DoesNotContain("text to send", group.Text);   // the root page lists send's argument
        var download = Assert.IsType<HelpCommand>(CommandLine.Parse(["flash", "download", "--help"]));
        Assert.Contains("--addr", download.Text);
    }

    // ---- the erase gate ------------------------------------------------------------------

    [Fact]
    public void Erase_with_yes_proceeds_without_touching_the_prompt()
    {
        var input = new StringReader("y\n");
        Assert.True(FlashOnce.EnsureEraseAllowed(interactive: true, yes: true, input: input, error: TextWriter.Null));
        Assert.Equal("y", input.ReadLine());   // untouched: the flag short-circuited before the prompt
    }

    [Fact]
    public void Erase_without_yes_on_redirected_stdin_is_a_usage_error()
    {
        var ex = Assert.Throws<UsageException>(() =>
            FlashOnce.EnsureEraseAllowed(interactive: false, yes: false, input: TextReader.Null, error: TextWriter.Null));
        Assert.Contains("requires --yes", ex.Message);
    }

    [Theory]
    [InlineData("y")]
    [InlineData(" Y ")]
    public void Erase_interactively_confirmed_proceeds(string answer)
    {
        Assert.True(FlashOnce.EnsureEraseAllowed(interactive: true, yes: false,
            input: new StringReader(answer + "\n"), error: TextWriter.Null));
    }

    [Theory]
    [InlineData("n")]
    [InlineData("")]
    [InlineData("no")]
    public void Erase_interactively_declined_aborts(string answer)
    {
        Assert.False(FlashOnce.EnsureEraseAllowed(interactive: true, yes: false,
            input: new StringReader(answer + "\n"), error: TextWriter.Null));
    }
}
