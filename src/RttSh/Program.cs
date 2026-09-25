using System.Reflection;
using System.Text;
using Toolbox.Tools.RttCli.Scripting;

namespace Toolbox.Tools.RttCli;

/// <summary>Entry point: parse, dispatch (monitor / send / script / flash / list-devices), exit codes.
/// Data goes to stdout, every diagnostic to stderr, so stdout stays pipeable.
///
/// The exit path never calls Environment.Exit (deadlock-prone next to the native DLL): threads
/// only signal their session's `done`, the main thread wakes and returns normally. Per-command
/// Ctrl+C policies live with their sessions - MonitorSession for the interactive terminal
/// (TreatControlCAsInput, CancelKeyPress only as a Ctrl+Break fallback) and ScriptSession for
/// script mode (CancelKeyPress is the primary cancel path). Usage errors exit 2, runtime
/// failures exit 1.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; }   // CP936 consoles would mangle UTF-8 logs
        catch { /* redirected or exotic hosts may refuse; the default is still usable */ }

        try
        {
            return Run(CommandLine.Parse(args));
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"rttsh: {ex.Message}");
            Console.Error.WriteLine("Run 'rttsh --help' for usage.");
            return 2;
        }
    }

    private static int Run(RttCommand command)
    {
        // Config-file defaults land before dispatch, so a config error beats per-command usage
        // validation; help/version/manual stay config-free (a broken config file must not take
        // --help down). -C/--root applies first (it moves the working directory the config
        // pickup and the cache home hang off), --elf resolves right after the config merge
        // (it must see the merged rttAddr/rttRange to honor precedence) and only for the
        // connection commands - list-devices never opens a link.
        switch (command)
        {
            case MonitorCommand c:
                RttRoot.Apply(c.Options);
                ConfigFile.ApplyTo(c.Options, c.Options.ConfigPath);
                ElfResolver.Apply(c.Options);
                break;
            case SendCommand c:
                RttRoot.Apply(c.Options);
                ConfigFile.ApplyTo(c.Options, c.Options.ConfigPath);
                ElfResolver.Apply(c.Options);
                break;
            case ScriptCommand c:
                RttRoot.Apply(c.Options);
                ConfigFile.ApplyTo(c.Options, c.Options.ConfigPath);
                ElfResolver.Apply(c.Options);
                break;
            case ListDevicesCommand c:
                RttRoot.Apply(c.Options);
                ConfigFile.ApplyTo(c.Options, c.Options.ConfigPath);
                break;
            case FlashCommand c:
                RttRoot.Apply(c.Options);
                ConfigFile.ApplyTo(c.Options, c.Options.ConfigPath);
                break;
        }

        return command switch
        {
            HelpCommand c => PrintHelp(c),
            ManualCommand => PrintManual(),
            VersionCommand => PrintVersion(),
            UsageErrorCommand c => UsageError(c.Message),
            ListDevicesCommand c => DeviceListing.Run(c),
            MonitorCommand c => MonitorSession.Run(c.Options),
            SendCommand c => SendOnce.Run(c),
            ScriptCommand c => ScriptSession.Run(c),
            FlashDownloadCommand c => FlashOnce.Run(c),
            FlashEraseCommand c => FlashOnce.RunErase(c),
            _ => UsageError("internal: unhandled command"),
        };
    }

    private static int PrintManual()
    {
        Console.WriteLine(Manual.Text);
        return 0;
    }

    private static int PrintVersion()
    {
        string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
        Console.WriteLine($"rttsh {version}");
        return 0;
    }

    /// <summary>Help text is rendered by the parser from the CliSpec declarations
    /// (library-generated; the exit-codes footer is part of it).</summary>
    private static int PrintHelp(HelpCommand command)
    {
        Console.Write(command.Text);
        return 0;
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"rttsh: {message}");
        Console.Error.WriteLine("Run 'rttsh --help' for usage.");
        return 2;
    }
}
