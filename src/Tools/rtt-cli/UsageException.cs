namespace Toolbox.Tools.RttCli;

/// <summary>A command-line misuse (bad option value, missing --chip, unusable --log path, missing
/// script file). Program.Main catches it, prints "rtt-cli: <message>" plus the --help hint, and
/// exits 2 - as opposed to runtime failures, which exit 1.</summary>
internal sealed class UsageException(string message) : Exception(message);
