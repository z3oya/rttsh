using RttSh.Core.Rtt.Elf;
using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

/// <summary>RttRootTests mutates the process working directory - global state xunit knows
/// nothing about. This collection runs serialized against every other one, so the suite-wide
/// "no test depends on cwd" assumption cannot be broken by a parallel class observing a
/// half-restored directory.</summary>
[CollectionDefinition("RttRoot", DisableParallelization = true)]
public sealed class RttRootCollection;

/// <summary>-C/--root: the option's parse surface and the RttRoot.Apply mechanics over real
/// directories. Apply mutates the process working directory, so every test saves and restores
/// it - and the end-to-end tests prove the payoff contracts: the implicit config is picked
/// up from <root>/.rttsh/, and the elf parse cache lands in <root>/.rttsh/elf-cache even
/// though the process was started elsewhere.</summary>
[Collection("RttRoot")]
public class RttRootTests
{
    [Fact]
    public void Parse_carries_the_root_from_both_spellings()
    {
        Assert.Equal("some/dir", ((SendCommand)CommandLine.Parse(["-C", "some/dir", "send", "hi"])).Options.RootDir);
        Assert.Equal("some/dir", ((MonitorCommand)CommandLine.Parse(["--root", "some/dir"])).Options.RootDir);
        Assert.Null(((MonitorCommand)CommandLine.Parse([])).Options.RootDir);
    }

    [Fact]
    public void Parse_rejects_a_valueless_root_option()
    {
        UsageException ex = Assert.Throws<UsageException>(() => CommandLine.Parse(["--root"]));
        Assert.Contains("--root: missing directory value", ex.Message);
    }

    [Fact]
    public void Apply_moves_the_working_directory()
    {
        string saved = Environment.CurrentDirectory;
        try
        {
            string root = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "rttsh-root-" + Path.GetRandomFileName())).FullName;

            RttRoot.Apply(new CommandLineOptions { RootDir = root });

            Assert.Equal(root, Environment.CurrentDirectory);
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }

    [Fact]
    public void Apply_accepts_a_relative_root_against_the_callers_directory()
    {
        string saved = Environment.CurrentDirectory;
        try
        {
            string parent = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "rttsh-root-" + Path.GetRandomFileName())).FullName;
            string name = "inner-" + Path.GetRandomFileName();
            Directory.CreateDirectory(Path.Combine(parent, name));
            Environment.CurrentDirectory = parent;

            RttRoot.Apply(new CommandLineOptions { RootDir = name });

            Assert.Equal(Path.Combine(parent, name), Environment.CurrentDirectory);
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }

    [Fact]
    public void Apply_reports_a_missing_directory_as_a_usage_error()
    {
        string saved = Environment.CurrentDirectory;
        try
        {
            string missing = Path.Combine(Path.GetTempPath(), "rttsh-root-absent-" + Path.GetRandomFileName());

            UsageException ex = Assert.Throws<UsageException>(
                () => RttRoot.Apply(new CommandLineOptions { RootDir = missing }));

            Assert.Contains("--root: directory not found", ex.Message);
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }

    [Fact]
    public void Apply_without_a_root_is_a_noop()
    {
        string saved = Environment.CurrentDirectory;
        try
        {
            RttRoot.Apply(new CommandLineOptions());
            RttRoot.Apply(new CommandLineOptions { RootDir = "" });
            Assert.Equal(saved, Environment.CurrentDirectory);
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }

    [Fact]
    public void The_implicit_config_comes_from_the_root()
    {
        string saved = Environment.CurrentDirectory;
        try
        {
            string root = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "rttsh-root-" + Path.GetRandomFileName())).FullName;
            Directory.CreateDirectory(Path.Combine(root, ".rttsh"));
            File.WriteAllText(Path.Combine(root, ".rttsh", "config.json"), """{ "chip": "FROM_ROOT" }""");

            var options = new CommandLineOptions { RootDir = root };
            RttRoot.Apply(options);
            ConfigFile.ApplyTo(options, options.ConfigPath);

            Assert.Equal("FROM_ROOT", options.Chip);
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }

    [Fact]
    public void An_explicit_config_beats_the_root_default_and_resolves_against_it()
    {
        string saved = Environment.CurrentDirectory;
        try
        {
            // alt.json exists only relative to the root, so "FROM_ALT" proves both halves of
            // the contract at once: the explicit file won AND resolved against <root>, not
            // against the caller's working directory.
            string root = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "rttsh-root-" + Path.GetRandomFileName())).FullName;
            Directory.CreateDirectory(Path.Combine(root, ".rttsh"));
            File.WriteAllText(Path.Combine(root, ".rttsh", "config.json"), """{ "chip": "FROM_ROOT" }""");
            File.WriteAllText(Path.Combine(root, "alt.json"), """{ "chip": "FROM_ALT" }""");

            var options = new CommandLineOptions { RootDir = root, ConfigPath = "alt.json" };
            RttRoot.Apply(options);
            ConfigFile.ApplyTo(options, options.ConfigPath);

            Assert.Equal("FROM_ALT", options.Chip);
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }

    [Fact]
    public void An_elf_path_that_exists_relative_to_the_caller_gets_pointed_out()
    {
        string saved = Environment.CurrentDirectory;
        try
        {
            string caller = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "rttsh-root-" + Path.GetRandomFileName())).FullName;
            string root = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "rttsh-root-" + Path.GetRandomFileName())).FullName;
            File.WriteAllBytes(Path.Combine(caller, "fw.elf"), new ElfBuilder
            {
                Symbols = { new ElfBuilder.Sym("_SEGGER_RTT", 0x2400_0070, 168, ElfBuilder.TypeObject, ElfBuilder.BindGlobal) },
            }.Build());
            Environment.CurrentDirectory = caller;   // stand where the user stood when typing the path

            var options = new CommandLineOptions { RootDir = root, ElfPath = "fw.elf" };
            RttRoot.Apply(options);   // the classic stumble: the path was typed caller-relative

            UsageException ex = Assert.Throws<UsageException>(() => ElfResolver.Apply(options));
            Assert.Contains("note: with --root", ex.Message);
            Assert.Contains(Path.Combine(caller, "fw.elf"), ex.Message);
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }

    [Fact]
    public void A_genuinely_missing_elf_path_gets_no_note()
    {
        string saved = Environment.CurrentDirectory;
        try
        {
            string root = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "rttsh-root-" + Path.GetRandomFileName())).FullName;

            var options = new CommandLineOptions { RootDir = root, ElfPath = "nowhere.elf" };
            RttRoot.Apply(options);

            UsageException ex = Assert.Throws<UsageException>(() => ElfResolver.Apply(options));
            Assert.DoesNotContain("note:", ex.Message);
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }

    [Fact]
    public void The_elf_cache_lands_under_the_root()
    {
        string saved = Environment.CurrentDirectory;
        try
        {
            string root = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "rttsh-root-" + Path.GetRandomFileName())).FullName;
            File.WriteAllBytes(Path.Combine(root, "fw.elf"), new ElfBuilder
            {
                Symbols = { new ElfBuilder.Sym("_SEGGER_RTT", 0x2400_0070, 168, ElfBuilder.TypeObject, ElfBuilder.BindGlobal) },
            }.Build());

            var options = new CommandLineOptions { RootDir = root, ElfPath = "fw.elf" };
            RttRoot.Apply(options);
            ElfResolver.Apply(options);   // cacheRoot defaults to the (moved) working directory

            Assert.Equal(0x2400_0070u, options.RttAddress);
            string[] entries = Directory.GetFiles(Path.Combine(root, ".rttsh", "elf-cache"), "*.json");
            Assert.Single(entries);
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }

    // ---- corner cases -------------------------------------------------------------------

    [Fact]
    public void Root_pointing_at_a_file_is_rejected()
    {
        string saved = Environment.CurrentDirectory;
        try
        {
            string file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            File.WriteAllBytes(file, [1]);

            UsageException ex = Assert.Throws<UsageException>(
                () => RttRoot.Apply(new CommandLineOptions { RootDir = file }));

            Assert.Contains("--root: directory not found", ex.Message);
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }

    [Fact]
    public void Root_dot_anchors_without_moving()
    {
        string saved = Environment.CurrentDirectory;
        try
        {
            var options = new CommandLineOptions { RootDir = "." };

            RttRoot.Apply(options);

            Assert.Equal(saved, Environment.CurrentDirectory);
            Assert.Equal(saved, options.CallerDirectory);   // the caller is still recorded for hints
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }

    [Fact]
    public void Help_stays_root_free()
    {
        Assert.IsType<HelpCommand>(CommandLine.Parse(["-C", "anywhere", "--help"]));
    }

    [Fact]
    public void Root_is_order_independent_and_supports_equals_syntax()
    {
        Assert.Equal("d", ((SendCommand)CommandLine.Parse(["send", "-C", "d", "hi"])).Options.RootDir);
        Assert.Equal("d", ((SendCommand)CommandLine.Parse(["--root=d", "send", "hi"])).Options.RootDir);
        // the user's stumble: both spellings must carry, whichever comes first on the line
        Assert.Equal("d", ((MonitorCommand)CommandLine.Parse(["--elf", "fw.elf", "--root", "d"])).Options.RootDir);
        Assert.Equal("d", ((MonitorCommand)CommandLine.Parse(["--root", "d", "--elf", "fw.elf"])).Options.RootDir);
    }

    [Fact]
    public void A_dotrttsh_file_instead_of_a_directory_is_tolerated()
    {
        string saved = Environment.CurrentDirectory;
        try
        {
            string root = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "rttsh-root-" + Path.GetRandomFileName())).FullName;
            File.WriteAllText(Path.Combine(root, ".rttsh"), "not a directory");

            var options = new CommandLineOptions { RootDir = root };
            RttRoot.Apply(options);
            ConfigFile.ApplyTo(options, options.ConfigPath);

            Assert.Equal("", options.Chip);   // soft skip: unusable state never becomes an error
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }
}
