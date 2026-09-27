using System.Diagnostics;

namespace Toolbox.Tools.RttCli;

/// <summary>fromelf (ARM Compiler) discovery and the ELF→hex conversion behind flash download.
/// The J-Link DLL's ELF loader takes section execution addresses and drops the flash
/// load-copy of RW data, so ELF/AXF images are flattened to Intel hex first.
/// Discovery: --fromelf / settings.json "fromelf" (hard error when missing) > PATH >
/// Keil roots. Without fromelf, ELF/AXF images are refused (MissingMessage).</summary>
internal static class FromElf
{
    public const string SettingsKey = "fromelf";

    private const int TimeoutMs = 120_000;

    /// <summary>The executable name as discovery looks for it under PATH and the Keil roots.</summary>
    private static string ExeName => OperatingSystem.IsWindows() ? "fromelf.exe" : "fromelf";

    /// <summary>A configured path must exist (hard error); otherwise the first discovery
    /// candidate wins. Null = nothing found.</summary>
    public static string? Resolve(CommandLineOptions options) =>
        Resolve(options, pathDirs: null, keilRoots: null);

    internal static string? Resolve(CommandLineOptions options,
        IEnumerable<string>? pathDirs, IEnumerable<string>? keilRoots)
    {
        string? configured = options.FromElfPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string path = Path.GetFullPath(configured.Trim());
            if (!File.Exists(path))
                throw new UsageException(
                    $"--fromelf: fromelf not found: '{configured}' (set via --fromelf or settings.json '{SettingsKey}')");
            return path;
        }

        foreach (string candidate in CandidatePaths(pathDirs ?? PathDirs(), keilRoots ?? KeilRoots(), ExeName))
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The refusal for an ELF image when no fromelf was found.</summary>
    public static string MissingMessage(string imagePath) => $$"""

        flash download: '{{imagePath}}' is an ELF image, but no fromelf was found to convert it with.
        The J-Link DLL loads ELF by section execution addresses and drops the flash load-copy
        of RW data - the firmware would boot with .data initialized from erased flash - so
        rttsh converts ELF/AXF to a flat hex with ARM Compiler's fromelf before downloading.
        Fix one of:
          - install Keil MDK or an ARM Compiler pack, or
          - set "{{SettingsKey}}" in ~/.rttsh/settings.json, e.g.
              { "{{SettingsKey}}": "C:\\Keil_v5\\ARM\\ARMCLANG\\bin\\fromelf.exe" }
          - pass --fromelf <path>, or
          - convert the image yourself and flash the hex:
              fromelf --i32combined --output app.hex app.axf
        """;

    /// <summary>Converts an ELF/AXF image to a flat Intel hex in a temp file and returns
    /// the temp path - FlashOnce owns the cleanup. Failures are usage errors: the
    /// conversion runs before any probe connection.</summary>
    public static string ConvertToHex(string imagePath, string fromElfPath, bool verbose) =>
        ConvertToHex(imagePath, fromElfPath, verbose, DefaultRun);

    /// <summary>Test seam: <paramref name="run"/> spawns fromelf instead of a real process.</summary>
    internal static string ConvertToHex(string imagePath, string fromElfPath, bool verbose,
        Func<string, IReadOnlyList<string>, (int ExitCode, string Error)> run)
    {
        string tempHex = Path.Combine(Path.GetTempPath(), $"rttsh-flash-{Guid.NewGuid():N}.hex");
        if (verbose)
            SessionSupport.WriteDiag($"rttsh: '{imagePath}' is ELF - converting with {fromElfPath} --i32combined");

        try
        {
            (int exitCode, string error) = run(fromElfPath, ["--i32combined", $"--output={tempHex}", imagePath]);
            if (exitCode != 0)
                throw new UsageException(
                    $"fromelf failed (exit {exitCode}) converting '{imagePath}': {Truncate(error)}");
            if (!File.Exists(tempHex))
                throw new UsageException($"fromelf produced no output for '{imagePath}'{Suffix(error)}");
            if (FirstByte(tempHex) != (byte)':')
                throw new UsageException($"fromelf output for '{imagePath}' is not Intel hex{Suffix(error)}");
            return tempHex;
        }
        catch
        {
            TryDelete(tempHex);
            throw;
        }
    }

    /// <summary>Discovery order: PATH entries, then Keil roots. Callers filter by existence.</summary>
    internal static IEnumerable<string> CandidatePaths(
        IEnumerable<string> pathDirs, IEnumerable<string> keilRoots, string fileName)
    {
        foreach (string dir in pathDirs)
            yield return Path.Combine(dir, fileName);
        foreach (string root in keilRoots)
            foreach (string candidate in ProbeKeilRoot(root, fileName))
                yield return candidate;
    }

    /// <summary>Compiler dirs under a Keil root's ARM folder, preference-ordered:
    /// ARMCLANG, ARMCompiler* packs (newest first), ARMCC. Absent root = none.</summary>
    internal static IEnumerable<string> ProbeKeilRoot(string root, string fileName)
    {
        string[] entries;
        try { entries = Directory.GetDirectories(Path.Combine(root, "ARM")); }
        catch { yield break; }

        var clang = new List<string>();
        var packs = new List<string>();
        var cc5 = new List<string>();
        foreach (string dir in entries)
        {
            string name = Path.GetFileName(dir);
            if (name.Equals("ARMCLANG", StringComparison.OrdinalIgnoreCase)) clang.Add(dir);
            else if (name.StartsWith("ARMCompiler", StringComparison.OrdinalIgnoreCase)) packs.Add(dir);
            else if (name.Equals("ARMCC", StringComparison.OrdinalIgnoreCase)) cc5.Add(dir);
        }
        packs.Sort(StringComparer.OrdinalIgnoreCase);   // ascending; walked back = newest first

        foreach (string dir in clang) yield return Path.Combine(dir, "bin", fileName);
        for (int i = packs.Count - 1; i >= 0; i--) yield return Path.Combine(packs[i], "bin", fileName);
        foreach (string dir in cc5) yield return Path.Combine(dir, "bin", fileName);
    }

    private static IEnumerable<string> PathDirs()
    {
        string value = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return dir;
    }

    private static IEnumerable<string> KeilRoots()
    {
        if (!OperatingSystem.IsWindows())   // Keil MDK installs only on Windows
            yield break;

        // uVision's default install roots; exotic locations belong in --fromelf.
        string[] roots =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Keil_v5"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Keil_v5"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Keil"),
            "C:\\Keil_v5", "C:\\Keil", "D:\\Keil_v5", "D:\\Keil", "E:\\Keil_v5", "E:\\Keil",
        ];
        foreach (string root in roots)
            yield return root;
    }

    private static (int ExitCode, string Error) DefaultRun(string fileName, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        try
        {
            using Process? process = Process.Start(psi);
            if (process is null)
                throw new UsageException($"cannot start '{fileName}'");
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(TimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw new UsageException($"fromelf timed out after {TimeoutMs / 1000} s converting the image");
            }
            _ = stdout.GetAwaiter().GetResult();   // drain both pipes
            return (process.ExitCode, stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is not UsageException)
        {
            throw new UsageException($"cannot run '{fileName}': {ex.Message}");
        }
    }

    private static byte FirstByte(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.ReadByte() switch { -1 => 0, int b => (byte)b };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UsageException($"fromelf output '{path}' is unreadable: {ex.Message}");
        }
    }

    private static string Truncate(string error)
    {
        string trimmed = error.Trim();
        if (trimmed.Length == 0) return "(no stderr output)";
        return trimmed.Length <= 500 ? trimmed : trimmed[..500] + "...";
    }

    private static string Suffix(string error) =>
        error.Trim().Length == 0 ? "" : $" - fromelf said: {Truncate(error)}";

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
