namespace Toolbox.Tools.RttCli;

/// <summary>-C/--root: run as if the process had been started in the given directory. Applied
/// right before the config merge, it moves Environment.CurrentDirectory, which is the one
/// anchor every downstream path already hangs off: the implicit ./.rttsh/config.json pickup,
/// the --elf parse cache home (<root>/.rttsh/elf-cache) and the resolution of every relative
/// path on the command line (elf, log, script, dll, an explicit --config too). git -C
/// semantics, deliberately nothing narrower - half-anchored paths are how config files stop
/// meaning the same thing twice.
///
/// The directory must exist (a typo must not silently run unconfigured); a missing config
/// inside it is still fine. A relative --root value resolves against the caller's real
/// working directory, because this runs before the switch.</summary>
internal static class RttRoot
{
    public static void Apply(CommandLineOptions options)
    {
        if (string.IsNullOrEmpty(options.RootDir))
            return;

        string full = Path.GetFullPath(options.RootDir);
        if (!Directory.Exists(full))
            throw new UsageException($"--root: directory not found: {options.RootDir}");
        options.CallerDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = full;
    }

    /// <summary>The note appended to a missing-path error when the path exists relative to the
    /// caller's original directory: with --root anchored elsewhere, that mismatch is the single
    /// most likely cause, and naming the file's real location turns the stumble into a fix.
    /// Empty unless --root moved the directory and the file is actually findable there.</summary>
    public static string MissingPathNote(CommandLineOptions options, string path)
    {
        if (options.CallerDirectory is null)
            return "";
        string callerPath = Path.GetFullPath(Path.Combine(options.CallerDirectory, path));
        return File.Exists(callerPath)
            ? $" (note: with --root, relative paths resolve against the root directory - this file exists at '{callerPath}')"
            : "";
    }
}
