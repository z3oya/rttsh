using System.CommandLine;
using System.CommandLine.Parsing;
using Toolbox.Core.Rtt;
using Toolbox.Core.SerialComm;

namespace Toolbox.Tools.RttCli;

/// <summary>TX line ending appended to each entered line.</summary>
internal enum TextEol { Lf, Cr, CrLf, None }

/// <summary>Parsed command line. Only what each command actually consumes is meaningful;
/// unset members stay null so the command can apply its own defaults.</summary>
internal sealed class CommandLineOptions
{
    public string Chip { get; set; } = "";
    public int? SpeedKhz { get; set; }
    public RttInterface? Interface { get; set; }
    /// <summary>null = command default (monitor resets, send does not).</summary>
    public bool? ResetOnConnect { get; set; }
    public uint? RttAddress { get; set; }
    public uint? RttRange { get; set; }
    public int? SerialNo { get; set; }
    public string DllPath { get; set; } = "";
    public TextEncodingKind? Encoding { get; set; }
    public TextEol? Eol { get; set; }
    public bool Hex { get; set; }
    /// <summary>Chat-style monitor layout (log on top, input pinned to the bottom); off by default.</summary>
    public bool Tui { get; set; }
    /// <summary>Show J-Link connection progress logs; off by default (errors are always shown).</summary>
    public bool Verbose { get; set; }
    public string? LogFile { get; set; }
    public int? WaitMs { get; set; }
    /// <summary>script only: hard limit for the whole script in ms; 0/absent = no limit.
    /// Enforced at rtt.* call boundaries.</summary>
    public int? ScriptTimeoutMs { get; set; }
    /// <summary>Substring filter, list-devices only.</summary>
    public string? DeviceFilter { get; set; }

    /// <summary>Encoding for both directions unless --encoding narrowed it (utf8 default).</summary>
    public TextEncodingKind EffectiveEncoding => Encoding ?? TextEncodingKind.Utf8;

    /// <summary>Builds the transport config from the parsed options; requires --chip.
    /// Non-trivial defaults (speed, interface) come from the record's own constants so they
    /// live in exactly one place; zero-valued options fall through to the record defaults.</summary>
    public RttConnectionConfig ToConnectionConfig(bool resetDefault)
    {
        if (Chip.Length == 0)
            throw new UsageException("missing required option --chip <device> (see rtt-cli list-devices)");
        return new RttConnectionConfig
        {
            Chip = Chip,
            ResetOnConnect = ResetOnConnect ?? resetDefault,
            DllPath = DllPath,
            SpeedKhz = SpeedKhz ?? RttConnectionConfig.DefaultSpeedKhz,
            Interface = Interface ?? RttConnectionConfig.DefaultInterface,
            RttAddress = RttAddress ?? 0,
            RttRange = RttRange ?? 0,
            SerialNo = SerialNo ?? 0,
        };
    }
}

internal abstract record RttCommand;
internal sealed record MonitorCommand(CommandLineOptions Options) : RttCommand;
internal sealed record ListDevicesCommand(string? Filter, string DllPath) : RttCommand;
internal sealed record SendCommand(string Payload, CommandLineOptions Options) : RttCommand;
internal sealed record ScriptCommand(string? ScriptPath, string? EvalSource, CommandLineOptions Options) : RttCommand;

/// <summary>Library-rendered help text (CliSpec.HelpText), ready to print.</summary>
internal sealed record HelpCommand(string Text) : RttCommand;
internal sealed record ManualCommand : RttCommand;
internal sealed record VersionCommand : RttCommand;
internal sealed record UsageErrorCommand(string Message) : RttCommand;

internal sealed class UsageException(string message) : Exception(message);

/// <summary>System.CommandLine parses; Parse maps the ParseResult onto the RttCommand union.
/// Pure function of string[] (the test seam). Only this file and CliSpec.cs know the library;
/// the first-token pre-checks below keep the old usage messages and the old
/// return-UsageErrorCommand-vs-throw-UsageException split that the tests pin down.</summary>
internal static class CommandLine
{
    public static RttCommand Parse(string[] args)
    {
        // legacy bare words, exactly at args[0] like the old switch did
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "help": return new HelpCommand(CliSpec.HelpText(["--help"]));
                case "version": return new VersionCommand();
            }
        }

        // legacy first-token pre-checks, message for message; they claim only "--" tokens
        // (payloads/paths may start with a single dash), and help spellings pass through so
        // "send --help" renders the subcommand help instead of the old usage error
        if (args.Length > 0 && args[0] == "send"
            && (args.Length < 2 || IsNonHelpOption(args[1])))
            return new UsageErrorCommand("send: missing <text> payload");
        if (args.Length > 0 && args[0] == "script")
        {
            if (Array.IndexOf(args, "--manual") >= 0)   // position-independent, as before
                return new ManualCommand();
            if (args.Length >= 2 && args[1] == "--eval")
            {
                if (args.Length < 3)
                    return new UsageErrorCommand("script: --eval needs the lua source text");
                // else: fall through - the parser validates the remaining tokens
            }
            else if (args.Length < 2 || IsNonHelpOption(args[1]))
            {
                return new UsageErrorCommand("script: missing <file.lua> path (or --eval <code>)");
            }
        }

        // parse-only: the library reports problems via ParseResult.Errors and prints nothing
        Spec spec = CliSpec.Build();
        ParseResult parsed = spec.Root.Parse(args);

        // help/version beat parse errors. Wider than the old switch, which only honored
        // them at args[0]: help anywhere on the line now wins, even alongside bad tokens.
        // Help re-renders from a clean parse so a stray bad token never leaks into the text.
        if (parsed.GetResult(spec.Help) is not null)
            return new HelpCommand(CliSpec.HelpText(HelpArgs(parsed.CommandResult.Command, spec)));
        if (parsed.GetResult(spec.Version) is not null)
            return new VersionCommand();

        // every remaining parse problem -> UsageException; Program.Main prints the
        // "rtt-cli: " prefix + hint and exits 2, unchanged
        if (parsed.Errors.Count > 0)
            throw new UsageException(Describe(parsed, args));

        // map onto the command union; every command accepts every option (recursive shared
        // pool), each command consumes only what it knows - loose old behavior kept
        CommandLineOptions options = BuildOptions(parsed, spec);
        Command command = parsed.CommandResult.Command;
        if (command == spec.Send)
            return new SendCommand(parsed.GetValue(spec.Payload)!, options);
        if (command == spec.Script)
        {
            if (parsed.GetValue(spec.Manual))
                return new ManualCommand();   // "rtt-cli --chip X script --manual" (script not at args[0])
            string? path = parsed.GetValue(spec.ScriptFile);
            string? eval = parsed.GetValue(spec.Eval);
            if (eval is not null && path is not null)
                throw new UsageException($"script: --eval cannot be combined with a script file ('{path}')");
            return new ScriptCommand(path, eval, options);
        }
        if (command == spec.ListDevices)
            return new ListDevicesCommand(options.DeviceFilter, options.DllPath);
        return new MonitorCommand(options);   // no subcommand = default monitor
    }

    /// <summary>An option-looking token the pre-checks must not claim: anything starting
    /// with "--" except the help spellings (they fall through to the parser) and the bare
    /// "--" end-of-options separator (the library consumes it, so "send -- -5" sends "-5").
    /// Single-dash tokens stay payload/path material, as in the old parser ("send -5",
    /// "script -x.lua"); "-h" after send/script renders that subcommand's help.</summary>
    private static bool IsNonHelpOption(string arg) =>
        arg.StartsWith("--") && arg is not ("--help" or "--");

    /// <summary>Parse args for re-rendering help of the matched command (one level deep).</summary>
    private static string[] HelpArgs(Command matched, Spec spec)
    {
        if (matched == spec.Send) return ["send", "--help"];
        if (matched == spec.Script) return ["script", "--help"];
        if (matched == spec.ListDevices) return ["list-devices", "--help"];
        return ["--help"];
    }

    /// <summary>Old wording for the two most common failures; anything else keeps the
    /// library message (exit code 2 + "rtt-cli: " prefix hold regardless).</summary>
    private static string Describe(ParseResult parsed, string[] args)
    {
        if (parsed.UnmatchedTokens.Count > 0)
        {
            string token = parsed.UnmatchedTokens[0];
            if (token.StartsWith('-')) return $"unknown option '{token}'";
            if (token == args[0]) return $"unknown command '{token}'";
        }
        return parsed.Errors[0].Message;
    }

    private static CommandLineOptions BuildOptions(ParseResult p, Spec s) => new()
    {
        Chip = p.GetValue(s.Chip) ?? "",
        SpeedKhz = p.GetValue(s.Speed),
        Interface = p.GetValue(s.If),
        // both flags given is pathological; the old parser was last-wins (position
        // dependent), we pick a fixed precedence instead
        ResetOnConnect = p.GetValue(s.NoReset) ? false : p.GetValue(s.Reset) ? true : null,
        RttAddress = p.GetValue(s.RttAddress),
        RttRange = p.GetValue(s.RttRange),
        SerialNo = p.GetValue(s.SerialNo),
        DllPath = p.GetValue(s.Dll) ?? "",
        Encoding = p.GetValue(s.Encoding),
        Eol = p.GetValue(s.Eol),
        Hex = p.GetValue(s.Hex),
        Tui = p.GetValue(s.Tui),
        Verbose = p.GetValue(s.Verbose),
        LogFile = p.GetValue(s.Log),
        WaitMs = p.GetValue(s.Wait),
        ScriptTimeoutMs = p.GetValue(s.ScriptTimeout),
        DeviceFilter = p.GetValue(s.Filter),
    };
}
