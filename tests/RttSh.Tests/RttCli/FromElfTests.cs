using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

/// <summary>FromElf's discovery ordering and ELF→hex conversion; the golden check runs only
/// when this machine has a real fromelf.</summary>
public class FromElfTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"rtt-fromelf-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string TempFile(string relative, string content = "")
    {
        string path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- Resolve: configured path ----

    [Fact]
    public void A_configured_existing_fromelf_wins()
    {
        string fromelf = TempFile("fe/fromelf.exe");
        var options = new CommandLineOptions { FromElfPath = fromelf };
        Assert.Equal(Path.GetFullPath(fromelf), FromElf.Resolve(options, [], []));
    }

    [Fact]
    public void A_configured_missing_fromelf_is_a_hard_error()
    {
        var options = new CommandLineOptions { FromElfPath = "C:/no/such/fromelf.exe" };
        var ex = Assert.Throws<UsageException>(() => FromElf.Resolve(options, [], []));
        Assert.Contains("--fromelf: fromelf not found", ex.Message);
        Assert.Contains("C:/no/such/fromelf.exe", ex.Message);
    }

    [Fact]
    public void An_empty_configured_value_falls_through_to_discovery()
    {
        string fromelf = TempFile("path-dir/fromelf.exe");
        var options = new CommandLineOptions { FromElfPath = "  " };
        Assert.Equal(Path.GetFullPath(fromelf), FromElf.Resolve(options, [Path.GetDirectoryName(fromelf)!], []));
    }

    // ---- Resolve / CandidatePaths: discovery order ----

    [Fact]
    public void Discovery_finds_the_first_existing_candidate()
    {
        string fromelf = TempFile("path-dir/fromelf.exe");
        var options = new CommandLineOptions();
        Assert.Equal(Path.GetFullPath(fromelf), FromElf.Resolve(options, [Path.GetDirectoryName(fromelf)!], []));
    }

    [Fact]
    public void Discovery_returns_null_when_nothing_exists()
    {
        var options = new CommandLineOptions();
        Assert.Null(FromElf.Resolve(options, [Path.Combine(_dir, "empty")], [Path.Combine(_dir, "no-root")]));
    }

    [Fact]
    public void Path_dirs_come_before_keil_roots()
    {
        string[] candidates = FromElf.CandidatePaths(
            ["C:/p1", "C:/p2"], ["C:/keil"], "fromelf.exe").ToArray();
        string[] expected =
        [
            Path.Combine("C:/p1", "fromelf.exe"),
            Path.Combine("C:/p2", "fromelf.exe"),
        ];
        Assert.Equal(expected, candidates[..2]);   // the Keil probe follows
    }

    [Fact]
    public void Keil_root_probe_prefers_ac6_then_packs_newest_then_ac5()
    {
        foreach (string name in new[] { "ARMCC", "ARMCLANG", "ARMCompiler5.06", "ARMCompiler6.21", "UV4" })
            TempFile($"keil/ARM/{name}/bin/fromelf.exe");

        string[] candidates = FromElf.ProbeKeilRoot(Path.Combine(_dir, "keil"), "fromelf.exe").ToArray();
        string[] expected =
        [
            Path.Combine(_dir, "keil", "ARM", "ARMCLANG", "bin", "fromelf.exe"),
            Path.Combine(_dir, "keil", "ARM", "ARMCompiler6.21", "bin", "fromelf.exe"),
            Path.Combine(_dir, "keil", "ARM", "ARMCompiler5.06", "bin", "fromelf.exe"),
            Path.Combine(_dir, "keil", "ARM", "ARMCC", "bin", "fromelf.exe"),
        ];
        Assert.Equal(expected, candidates);
    }

    [Fact]
    public void A_missing_keil_root_yields_no_candidates()
    {
        Assert.Empty(FromElf.ProbeKeilRoot(Path.Combine(_dir, "absent"), "fromelf.exe"));
    }

    // ---- ConvertToHex: process orchestration through the injected runner ----

    private static string OutputPathOf(IReadOnlyList<string> args) =>
        args[1].Substring("--output=".Length);

    [Fact]
    public void Conversion_returns_the_temp_hex_the_runner_wrote()
    {
        string image = TempFile("app.axf", "ELF");
        string? written = null;
        string tempHex = FromElf.ConvertToHex(image, "C:/fe.exe", verbose: false, run: (fromelf, args) =>
        {
            Assert.Equal("C:/fe.exe", fromelf);
            Assert.Equal(["--i32combined", args[1], image], args);
            written = OutputPathOf(args);
            File.WriteAllText(written, ":020000040800F2\n:00000001FF\n");
            return (0, "");
        });
        try
        {
            Assert.Equal(written, tempHex);
            Assert.StartsWith(":", File.ReadAllText(tempHex));
        }
        finally
        {
            try { File.Delete(tempHex); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void A_failing_fromelf_names_the_exit_code_and_stderr()
    {
        string image = TempFile("bad.axf", "ELF");
        var ex = Assert.Throws<UsageException>(
            () => FromElf.ConvertToHex(image, "C:/fe.exe", verbose: false, run: (_, _) => (1, "error: not an ELF file\r\n")));
        Assert.Contains("fromelf failed (exit 1)", ex.Message);
        Assert.Contains("error: not an ELF file", ex.Message);
    }

    [Fact]
    public void Missing_output_is_a_usage_error()
    {
        string image = TempFile("app.axf", "ELF");
        var ex = Assert.Throws<UsageException>(
            () => FromElf.ConvertToHex(image, "C:/fe.exe", verbose: false, run: (_, _) => (0, "")));
        Assert.Contains("produced no output", ex.Message);
    }

    [Fact]
    public void Non_hex_output_is_a_usage_error()
    {
        string image = TempFile("app.axf", "ELF");
        var ex = Assert.Throws<UsageException>(
            () => FromElf.ConvertToHex(image, "C:/fe.exe", verbose: false, run: (_, args) =>
            {
                File.WriteAllText(OutputPathOf(args), "garbage");
                return (0, "");
            }));
        Assert.Contains("is not Intel hex", ex.Message);
    }

    [Fact]
    public void A_runner_failure_deletes_the_partial_temp_output()
    {
        string image = TempFile("app.axf", "ELF");
        string? partial = null;
        var ex = Assert.Throws<UsageException>(
            () => FromElf.ConvertToHex(image, "C:/fe.exe", verbose: false, run: (_, args) =>
            {
                partial = OutputPathOf(args);
                File.WriteAllText(partial, ":0200000408");   // half a record
                throw new UsageException("fromelf timed out after 120 s converting the image");
            }));
        Assert.Contains("timed out", ex.Message);
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public void Stderr_from_a_successful_run_names_itself_in_missing_output_errors()
    {
        string image = TempFile("app.axf", "ELF");
        var ex = Assert.Throws<UsageException>(
            () => FromElf.ConvertToHex(image, "C:/fe.exe", verbose: false, run: (_, _) => (0, "warning: stray note")));
        Assert.Contains("warning: stray note", ex.Message);
    }

    [Fact]
    public void The_missing_fromelf_refusal_leads_with_the_reason_and_the_ways_out()
    {
        string message = FromElf.MissingMessage("app.axf");
        Assert.Contains("app.axf", message);
        Assert.Contains("section execution addresses", message);   // the DLL loader flaw
        Assert.Contains("settings.json", message);
        Assert.Contains("--fromelf", message);
        Assert.Contains("--i32combined", message);
    }

    // ---- Golden: the real fromelf, when this machine has one ----

    [Fact]
    public void Real_fromelf_converts_the_example_axf_to_the_load_image()
    {
        string axf = Path.Combine(RepoRoot(), "examples", "stm32", "MDK-ARM", "stm32-project", "stm32-project.axf");
        if (!File.Exists(axf)) return;

        string? fromelf = FromElf.Resolve(new CommandLineOptions());
        if (fromelf is null) return;

        string tempHex = FromElf.ConvertToHex(axf, fromelf, verbose: false);
        try
        {
            (uint addr, byte[] bytes) = ParseFirstRange(tempHex);
            Assert.Equal(0x0800_0000u, addr);
            Assert.Equal(0x8258, bytes.Length);   // ER_IROM1 + the 0x18-byte RW load copy
        }
        finally
        {
            try { File.Delete(tempHex); } catch { }
        }
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "RttSh.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? ".";
    }

    /// <summary>Minimal Intel-hex reader: the first contiguous data range (types 00/04 only).</summary>
    private static (uint Addr, byte[] Bytes) ParseFirstRange(string path)
    {
        uint ext = 0;
        var bytes = new List<byte>();
        foreach (string line in File.ReadLines(path))
        {
            if (!line.StartsWith(':')) continue;
            int count = Convert.ToInt32(line[1..3], 16);
            int offset = Convert.ToInt32(line[3..7], 16);
            int type = Convert.ToInt32(line[7..9], 16);
            byte[] data = Convert.FromHexString(line[9..(9 + 2 * count)]);
            if (type == 4) ext = (uint)BitConverter.ToUInt16([data[1], data[0]], 0) << 16;
            else if (type == 0 && bytes.Count == 0) { ext += (uint)offset; bytes.AddRange(data); }
            else if (type == 0) bytes.AddRange(data);
            else if (type == 1) break;
        }
        return (ext, [.. bytes]);
    }
}
