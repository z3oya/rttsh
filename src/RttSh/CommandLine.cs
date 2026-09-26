using System.CommandLine;
using System.CommandLine.Parsing;
using RttSh.Core.Rtt;
using RttSh.Core.SerialComm;

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
    public int? Channel { get; set; }
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
    /// <summary>-c/--config: JSON file to load option defaults from; an explicit path must exist.</summary>
    public string? ConfigPath { get; set; }
    /// <summary>-C/--root: run as if started in this directory (RttRoot applies it before the
    /// config merge), so the implicit ./.rttsh state and every relative path anchor there.</summary>
    public string? RootDir { get; set; }
    /// <summary>Set by RttRoot when --root moved the working directory: where the process was
    /// started, so path errors can point at where a caller-relative path actually lives.</summary>
    public string? CallerDirectory { get; set; }
    /// <summary>--flash download: raw .bin images must pair with --addr (FlashImage validates the
    /// pairing after sniffing; the option itself only carries a value here).</summary>
    public uint? Addr { get; set; }
    /// <summary>--yes: flash erase's confirmation bypass (required when stdin is redirected).</summary>
    public bool Yes { get; set; }
    /// <summary>--elf: firmware image (ELF32) to resolve the RTT control-block address from.
    /// Resolved before dispatch (ElfResolver); an explicit RttAddress always wins.</summary>
    public string? ElfPath { get; set; }

    /// <summary>Encoding for both directions unless --encoding narrowed it (utf8 default).</summary>
    public TextEncodingKind EffectiveEncoding => Encoding ?? TextEncodingKind.Utf8;

    /// <summary>Fails fast when no chip is known: Program.Run calls this at dispatch time,
    /// right after the config merge and before any session work (an ELF parse, a log file,
    /// flash erase's confirmation prompt must not run first). ChipValidation.EnsureKnown
    /// follows with the membership check against the DLL device database. The session-internal
    /// ToConnectionConfig check below stays as the backstop; the wording lives here only.</summary>
    public void EnsureChip()
    {
        if (Chip.Length == 0)
            throw new UsageException("missing required option --chip <device> (see rttsh list-devices, or set 'chip' in .rttsh/config.json)");
    }

    /// <summary>Builds the transport config from the parsed options; requires --chip.
    /// Non-trivial defaults (speed, interface) come from the record's own constants so they
    /// live in exactly one place; zero-valued options fall through to the record defaults.</summary>
    public RttConnectionConfig ToConnectionConfig(bool resetDefault)
    {
        EnsureChip();
        int channel = Channel ?? 0;
        if (channel is < 0 or > RttConnectionConfig.MaxChannel)
            throw new UsageException($"--channel: expected 0-{RttConnectionConfig.MaxChannel}, got {Channel}");
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
            Channel = channel,
        };
    }
}

internal abstract record RttCommand;
internal sealed record MonitorCommand(CommandLineOptions Options) : RttCommand;
internal sealed record ListDevicesCommand(CommandLineOptions Options) : RttCommand;
internal sealed record SendCommand(string Payload, CommandLineOptions Options) : RttCommand;
internal sealed record ScriptCommand(string? ScriptPath, string? EvalSource, CommandLineOptions Options) : RttCommand;
/// <summary>The MCP server over stdio: no hardware options at startup - chip/validation
/// arrive per connect tool call, so only root/config pre-dispatch steps apply.</summary>
internal sealed record McpCommand(CommandLineOptions Options) : RttCommand;
/// <summary>Base of the two flash subcommands: the pre-dispatch options pass in Program
/// treats them identically (connect options, no RTT link).</summary>
internal abstract record FlashCommand(CommandLineOptions Options) : RttCommand;
internal sealed record FlashDownloadCommand(string FilePath, CommandLineOptions Options) : FlashCommand(Options);
internal sealed record FlashEraseCommand(CommandLineOptions Options) : FlashCommand(Options);

/// <summary>Library-rendered help text (CliSpec.HelpText), ready to print.</summary>
internal sealed record HelpCommand(string Text) : RttCommand;
internal sealed record ManualCommand : RttCommand;
internal sealed record VersionCommand : RttCommand;
internal sealed record UsageErrorCommand(string Message) : RttCommand;

/// <summary>System.CommandLine parses; Parse maps the ParseResult onto the RttCommand union.
/// Pure function of string[] (the test seam). Only this file and CliSpec.cs know the library;
/// the pre-checks below keep the old usage messages and the old
/// return-UsageErrorCommand-vs-throw-UsageException split that the tests pin down - they scan
/// the whole line for it, never just the first token after the subcommand.</summary>
internal static class CommandLine
{
    /// <summary>Legacy wording, shared by the args[0] pre-checks and the parse-level mapping
    /// (the backstop) so the two cannot drift.</summary>
    private const string FlashMissingSubcommand = "flash: missing subcommand - use 'flash download <file>' or 'flash erase'";
    private const string SendMissingPayload = "send: missing <text> payload";
    private const string ScriptMissingPath = "script: missing <file.lua> path (or --eval <code>)";

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

        Spec spec = CliSpec.Build();

        // legacy pre-checks for the payload/path-bearing commands, message for message. Like
        // the flash branch below they scan the whole line - options may sit anywhere, before
        // or after the payload/path - and they only claim lines that carry no help spelling,
        // so "send --help" renders the subcommand help instead of the old usage error
        if (args.Length > 0 && args[0] == "send" && !HasHelpPassthrough(args))
        {
            (bool slidingUnknown, bool hasCandidate) = ScanPositional(spec, args, evalKnown: false);
            if (slidingUnknown || !hasCandidate)
                return new UsageErrorCommand(SendMissingPayload);
        }
        if (args.Length > 0 && args[0] == "script")
        {
            if (Array.IndexOf(args, "--manual") >= 0)   // retired flag: point at its replacement
                return new UsageErrorCommand("script: --manual was removed; run 'rttsh manual'");
            if (!HasHelpPassthrough(args))
            {
                (bool slidingUnknown, bool hasCandidate) = ScanPositional(spec, args, evalKnown: true);
                if (slidingUnknown)
                    return new UsageErrorCommand(ScriptMissingPath);
                int eval = IndexOfEvalToken(args);
                if (eval >= 0)
                {
                    // bare "--eval" with nothing but options around it: the old parser's
                    // missing-value message. Every other eval shape ("--eval=code" spelled
                    // values included) falls through - the parser owns the missing-value
                    // wording there, the mapping layer the path+eval conflict
                    if (args[eval] == "--eval" && eval == args.Length - 1 && !HasNonOptionTokenAfter(args, 0))
                        return new UsageErrorCommand("script: --eval needs the lua source text");
                }
                else if (!hasCandidate)
                {
                    return new UsageErrorCommand(ScriptMissingPath);
                }
            }
        }

        if (args.Length > 0 && args[0] == "flash" && !HasPassthroughToken(args))
        {
            // Every option is recursive, so the subcommand and the image path may sit anywhere
            // on the line - scan for them instead of staring at args[1]/args[2] (which mis-reads
            // e.g. "flash download --chip X app.bin" as a missing file). Help/version spellings
            // pass through via HasPassthroughToken, and the parse-level mapping below stays the
            // backstop for odd shapes like "--chip X flash".
            int download = Array.IndexOf(args, "download");
            if (download < 0 && Array.IndexOf(args, "erase") < 0)
                return new UsageErrorCommand(FlashMissingSubcommand);
            if (download >= 0 && !HasNonOptionTokenAfter(args, download))
                return new UsageErrorCommand("flash download: missing <file> image path");
        }

        // parse-only: the library reports problems via ParseResult.Errors and prints nothing
        ParseResult parsed = spec.Root.Parse(args);

        // help/version beat parse errors. Wider than the old switch, which only honored
        // them at args[0]: help anywhere on the line now wins, even alongside bad tokens.
        // Help re-renders from a clean parse so a stray bad token never leaks into the text.
        if (parsed.GetResult(spec.Help) is not null)
            return new HelpCommand(CliSpec.HelpText(HelpArgs(parsed.CommandResult.Command, spec)));
        if (parsed.GetResult(spec.Version) is not null)
            return new VersionCommand();

        // every remaining parse problem -> UsageException; Program.Main prints the
        // "rttsh: " prefix + hint and exits 2, unchanged
        if (parsed.Errors.Count > 0)
            throw new UsageException(Describe(parsed, args));

        // map onto the command union; every command accepts every option (recursive shared
        // pool), each command consumes only what it knows - loose old behavior kept
        CommandLineOptions options = BuildOptions(parsed, spec);
        Command command = parsed.CommandResult.Command;
        if (command == spec.Send)
        {
            // payload is parse-level optional; the backstop carries the legacy wording for
            // shapes the args[0] pre-check cannot see ("--chip STM32H743XI send", "send --")
            // and for candidates swallowed by an option value
            string? payload = parsed.GetValue(spec.Payload);
            return payload is null
                ? new UsageErrorCommand(SendMissingPayload)
                : new SendCommand(payload, options);
        }
        if (command == spec.Script)
        {
            string? path = parsed.GetValue(spec.ScriptFile);
            string? eval = parsed.GetValue(spec.Eval);
            if (eval is not null && path is not null)
                throw new UsageException($"script: --eval cannot be combined with a script file ('{path}')");
            return path is null && eval is null
                ? new UsageErrorCommand(ScriptMissingPath)
                : new ScriptCommand(path, eval, options);
        }
        if (command == spec.ListDevices)
            return new ListDevicesCommand(options);
        if (command == spec.Mcp)
            return new McpCommand(options);
        if (command == spec.Manual)
            return new ManualCommand();
        if (command == spec.Flash)
            return new UsageErrorCommand(FlashMissingSubcommand);
        if (command == spec.FlashDownload)
            return new FlashDownloadCommand(parsed.GetValue(spec.FlashFile)!, options);
        if (command == spec.FlashErase)
            return new FlashEraseCommand(options);
        return new MonitorCommand(options);   // no subcommand = default monitor
    }

    /// <summary>Spellings the send/script pre-checks must not claim a line over: the help
    /// spellings render the subcommand's help from the parser, and the bare "--" separator
    /// means what follows is payload/path material ("send -- --value" has no other non-dash
    /// token). Deliberately narrower than flash's HasPassthroughToken below: --version/-v are
    /// root-only, so under send/script a "--version" token is unknown material for
    /// ScanPositional, and single-dash tokens ("-v", "-5") stay payload/path material, as
    /// everywhere else. ScanPositional is only called on lines this guard let through, so
    /// the spellings live in this one list.</summary>
    private static bool HasHelpPassthrough(string[] args)
    {
        foreach (string arg in args)
        {
            if (arg is "--help" or "-h" or "--") return true;
        }
        return false;
    }

    /// <summary>Whole-line scan (index 1 on, past the subcommand) for the send/script
    /// pre-checks: whether the payload/path slot is taken - the first unconsumed token that
    /// does not start with "--" - and whether an unknown "--" token sits in front of it. Such
    /// a token would slide into the still-empty slot at parse time (for send: a typo silently
    /// sent to the firmware), so the caller must claim the line; after the slot is taken the
    /// parser instead reports the accurate unknown option. Only called on lines
    /// HasHelpPassthrough let through: the help spellings and the bare "--" separator are the
    /// guard's job and never appear here. Known value options swallow the token that follows
    /// them - System.CommandLine takes it as the value even when it starts with a dash -
    /// while flags and "--opt=value" spellings swallow nothing. "--eval" counts as known only
    /// in the script branch (evalKnown); --version is root-only and deliberately unknown
    /// here; single-dash tokens are positional material and never enter this judgment.</summary>
    private static (bool SlidingUnknown, bool HasCandidate) ScanPositional(Spec spec, string[] args, bool evalKnown)
    {
        bool candidate = false;
        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--"))
            {
                candidate = true;
                continue;
            }
            int eq = arg.IndexOf('=');
            string name = eq > 0 ? arg[..eq] : arg;
            Option? option = evalKnown && name == "--eval" ? spec.Eval : spec.FindOption(name);
            if (option is null)
            {
                if (!candidate)
                    return (SlidingUnknown: true, HasCandidate: false);
                continue;   // slot taken: the parser reports the accurate unknown option
            }
            if (option.Arity.Equals(ArgumentArity.Zero) || eq > 0)
                continue;   // flag, or the value is embedded after '='
            if (i + 1 < args.Length)
                i++;   // value option: what follows is its value, not the positional candidate
        }
        return (SlidingUnknown: false, candidate);
    }

    /// <summary>First index of an "--eval"/"--eval=code" token, either spelling; -1 when none.</summary>
    private static int IndexOfEvalToken(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--eval" || args[i].StartsWith("--eval=", StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    /// <summary>Tokens the flash pre-checks must not claim: help/version spellings render
    /// their own output from the parser, and the bare "--" end-of-options separator is
    /// consumed there too (what follows it is payload/path material).</summary>
    private static bool IsPassthroughToken(string arg) =>
        arg is "--help" or "-h" or "--version" or "-v" or "--";

    private static bool HasPassthroughToken(string[] args)
    {
        foreach (string arg in args)
        {
            if (IsPassthroughToken(arg)) return true;
        }
        return false;
    }

    /// <summary>True when a non-option token follows the one at <paramref name="index"/>:
    /// for `flash download` that token can be the image path (or an option's value - a wrong
    /// guess only falls through to the parser, which knows the difference); with index 0 it
    /// doubles as the whole-line test behind the bare "--eval" pre-check. Single-dash
    /// tokens stay path material, as everywhere else.</summary>
    private static bool HasNonOptionTokenAfter(string[] args, int index)
    {
        for (int i = index + 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) return true;
        }
        return false;
    }

    /// <summary>Parse args for re-rendering help of the matched command (one level deep).</summary>
    private static string[] HelpArgs(Command matched, Spec spec)
    {
        if (matched == spec.Send) return ["send", "--help"];
        if (matched == spec.Script) return ["script", "--help"];
        if (matched == spec.ListDevices) return ["list-devices", "--help"];
        if (matched == spec.Mcp) return ["mcp", "--help"];
        if (matched == spec.Manual) return ["manual", "--help"];
        if (matched == spec.Flash) return ["flash", "--help"];
        if (matched == spec.FlashDownload) return ["flash", "download", "--help"];
        if (matched == spec.FlashErase) return ["flash", "erase", "--help"];
        return ["--help"];
    }

    /// <summary>Old wording for the two most common failures; anything else keeps the
    /// library message (exit code 2 + "rttsh: " prefix hold regardless).</summary>
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
        Channel = p.GetValue(s.Channel),
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
        ConfigPath = p.GetValue(s.Config),
        RootDir = p.GetValue(s.RootDir),
        ElfPath = p.GetValue(s.Elf),
        Addr = p.GetValue(s.Addr),
        Yes = p.GetValue(s.Yes),
    };
}
