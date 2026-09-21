using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Globalization;
using Toolbox.Core.Rtt;
using Toolbox.Core.SerialComm;

namespace Toolbox.Tools.RttCli;

/// <summary>One fresh parse tree: every symbol the CLI surface is made of. Built per Parse
/// call so parsing stays a pure function of string[] (symbols may carry parse-time state).
/// Together with CommandLine.cs this is the only place that knows System.CommandLine -
/// future Toolbox tools copy the pair as a unit.</summary>
internal sealed class Spec
{
    public required RootCommand Root { get; init; }
    public required Command ListDevices { get; init; }
    public required Command Send { get; init; }
    public required Command Script { get; init; }
    public required HelpOption Help { get; init; }
    public required VersionOption Version { get; init; }
    public required Argument<string> Payload { get; init; }
    public required Argument<string?> ScriptFile { get; init; }
    public required Option<string?> Chip { get; init; }
    public required Option<int?> Speed { get; init; }
    public required Option<RttInterface?> If { get; init; }
    public required Option<bool> Reset { get; init; }
    public required Option<bool> NoReset { get; init; }
    public required Option<uint?> RttAddress { get; init; }
    public required Option<uint?> RttRange { get; init; }
    public required Option<int?> SerialNo { get; init; }
    public required Option<string?> Dll { get; init; }
    public required Option<TextEncodingKind?> Encoding { get; init; }
    public required Option<TextEol?> Eol { get; init; }
    public required Option<bool> Hex { get; init; }
    public required Option<bool> Tui { get; init; }
    public required Option<bool> Verbose { get; init; }
    public required Option<string?> Log { get; init; }
    public required Option<int?> Wait { get; init; }
    public required Option<int?> ScriptTimeout { get; init; }
    public required Option<string?> Filter { get; init; }
    public required Option<string?> Eval { get; init; }
    public required Option<bool> Manual { get; init; }
}

/// <summary>Option/command declarations plus the small conversion helpers. Error wording
/// inside the CustomParsers is moved over from the old hand-rolled parser verbatim -
/// the messages are part of the CLI surface. Descriptions come from the old help text.</summary>
internal static class CliSpec
{
    public static Spec Build()
    {
        // shared option pool: Recursive=true puts every one of these on every subcommand,
        // keeping the old "any command accepts any option" looseness
        Option<string?> chip = TextOption("--chip", "name", "device name",
            "target device, e.g. STM32H743XI (required for monitor/send)");
        Option<int?> speed = IntOption("--speed", "kHz", "speed", "interface speed, default 4000");
        Option<RttInterface?> iff = EnumOption("--if", "swd|jtag", "target interface, default swd",
            [("swd", RttInterface.Swd), ("jtag", RttInterface.Jtag)], "swd or jtag");
        Option<bool> reset = Flag("--reset",
            "reset the target on connect (off by default: J-Link reset halts the core briefly; it is resumed automatically)");
        Option<bool> noReset = Flag("--no-reset", "connect without resetting (default for every command)");
        Option<uint?> rttAddress = HexOption("--rtt-addr", "hex", "address",
            "known control-block address (default: SDK auto-scan)");
        Option<uint?> rttRange = HexOption("--rtt-range", "hex", "range",
            "byte range searched for the \"SEGGER RTT\" signature");
        Option<int?> serialNo = IntOption("--sn", "number", "serial number",
            "probe USB serial number (default: first probe)");
        Option<string?> dll = TextOption("--dll", "path", "DLL path",
            "JLink DLL path (default: auto-detect, incl. SEGGER roots)");
        Option<TextEol?> eol = EnumOption("--eol", "lf|cr|crlf|none",
            "line ending appended to text sent by monitor input and send (default lf; --hex send payloads are raw)",
            [("lf", TextEol.Lf), ("cr", TextEol.Cr), ("crlf", TextEol.CrLf), ("none", TextEol.None)],
            "lf, cr, crlf or none");
        Option<TextEncodingKind?> encoding = EnumOption("--encoding", "utf8|ascii|latin1",
            "decode received bytes (default utf8)",
            [("utf8", TextEncodingKind.Utf8), ("ascii", TextEncodingKind.Ascii), ("latin1", TextEncodingKind.Latin1)],
            "utf8, ascii or latin1");
        Option<bool> hex = Flag("--hex", "show payload as a 16-byte-per-line hex dump (send: parse payload as hex)");
        Option<bool> tui = Flag("--tui", ["-tui"],
            "(monitor only) chat-style layout: log on top, \"> \" input pinned to the bottom " +
            "(off by default; needs a VT terminal); Up/Down recall previously sent lines");
        Option<bool> verbose = Flag("--verbose",
            "show J-Link connection progress logs (default: quiet; runtime errors are always shown)");
        Option<string?> log = TextOption("--log", "file", "log file",
            "also append raw received bytes to a file");
        Option<int?> wait = IntOption("--wait", "ms", "milliseconds",
            "(send / redirected monitor) print received bytes for this long before exiting; " +
            "redirected monitor defaults to 500 ms");
        Option<int?> scriptTimeout = IntOption("--script-timeout", "ms", "milliseconds",
            "script only: hard limit for the whole script in ms (0/absent = off; enforced at rtt.* call boundaries)");
        Option<string?> filter = TextOption("--filter", "text", "substring",
            "substring filter, list-devices only");

        var root = new RootCommand("rtt-cli - SEGGER J-Link RTT terminal (channel 0)");
        foreach (Option option in new Option[]
                 { chip, speed, iff, reset, noReset, rttAddress, rttRange, serialNo, dll, eol,
                   encoding, hex, tui, verbose, log, wait, scriptTimeout, filter })
            root.Options.Add(option);

        Command listDevices = new("list-devices", "list the J-Link DLL device database");

        Command send = new("send", "send once, optionally wait for a reply");
        Argument<string> payload = new("text")
        {
            Description = "text to send; \\n \\r \\t \\\\ escapes are interpreted",
        };
        send.Arguments.Add(payload);

        Command script = new("script",
            "run a Lua automation script (rtt.* API; --manual prints the reference)");
        Argument<string?> scriptFile = new("file.lua")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Lua script to run",
        };
        Option<string?> eval = new("--eval")
        {
            HelpName = "lua code",
            Description = "run the given Lua code instead of a script file",
            Arity = ArgumentArity.ZeroOrOne,
            CustomParser = result =>
            {
                if (result.Tokens.Count == 0)
                {
                    result.AddError("--eval: missing lua code value");
                    return null;
                }
                return result.Tokens[0].Value;
            },
        };
        Option<bool> manual = new("--manual") { Description = "print the rtt.* Lua API reference" };
        script.Arguments.Add(scriptFile);
        script.Options.Add(eval);
        script.Options.Add(manual);

        root.Subcommands.Add(listDevices);
        root.Subcommands.Add(send);
        root.Subcommands.Add(script);
        // A root with subcommands demands one ("Required command was not provided") unless
        // it has its own action; Parse never invokes it - the mapping layer turns the
        // matched root into the default monitor command.
        root.Action = new MonitorAction();

        // built-in help/version: restore the legacy -v alias and the exit-codes help footer
        HelpOption help = root.Options.OfType<HelpOption>().First();
        VersionOption version = root.Options.OfType<VersionOption>().First();
        version.Aliases.Add("-v");   // the built-in --version has no short form; legacy -v kept
        version.Description = "show rtt-cli's own version";
        help.Action = new ExitCodesHelpAction((HelpAction)help.Action!);

        return new Spec
        {
            Root = root,
            ListDevices = listDevices,
            Send = send,
            Script = script,
            Help = help,
            Version = version,
            Payload = payload,
            ScriptFile = scriptFile,
            Chip = chip,
            Speed = speed,
            If = iff,
            Reset = reset,
            NoReset = noReset,
            RttAddress = rttAddress,
            RttRange = rttRange,
            SerialNo = serialNo,
            Dll = dll,
            Encoding = encoding,
            Eol = eol,
            Hex = hex,
            Tui = tui,
            Verbose = verbose,
            Log = log,
            Wait = wait,
            ScriptTimeout = scriptTimeout,
            Filter = filter,
            Eval = eval,
            Manual = manual,
        };
    }

    /// <summary>Renders the library-generated help (plus the exit-codes footer) into a string:
    /// fresh tree, Output redirected - pure string-in/string-out.</summary>
    public static string HelpText(string[] args)
    {
        Spec spec = Build();
        var buffer = new StringWriter();
        var invocation = new InvocationConfiguration { Output = buffer, Error = buffer };
        spec.Root.Parse(args).Invoke(invocation);
        return buffer.ToString();
    }

    // ---- option factories ----------------------------------------------------------------
    // Every value option keeps ZeroOrOne arity, so an option given without a value reaches
    // its CustomParser with no tokens; the guard below turns that into the legacy error
    // (the old parser never accepted a valueless option).

    private static Option<string?> TextOption(string name, string helpName, string what, string description) => new(name)
    {
        HelpName = helpName,
        Description = description,
        Recursive = true,
        Arity = ArgumentArity.ZeroOrOne,   // valueless option must reach the CustomParser guard (legacy wording)
        CustomParser = result => ValueOrError(result, $"{name}: missing {what} value"),
    };

    private static Option<int?> IntOption(string name, string helpName, string what, string description) => new(name)
    {
        HelpName = helpName,
        Description = description,
        Recursive = true,
        Arity = ArgumentArity.ZeroOrOne,   // valueless option must reach the CustomParser guard (legacy wording)
        CustomParser = result =>
        {
            string? text = ValueOrError(result, $"{name}: missing {what} value");
            if (text is null)
                return null;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                result.AddError($"{name}: '{text}' is not a number");
                return null;
            }
            return value;
        },
    };

    private static Option<uint?> HexOption(string name, string helpName, string what, string description) => new(name)
    {
        HelpName = helpName,
        Description = description,
        Recursive = true,
        Arity = ArgumentArity.ZeroOrOne,   // valueless option must reach the CustomParser guard (legacy wording)
        CustomParser = result =>
        {
            string? text = ValueOrError(result, $"{name}: missing {what} value");
            if (text is null)
                return null;
            string digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
            if (!uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
            {
                // legacy wording calls the range value an address too - kept verbatim
                result.AddError($"{name}: '{text}' is not a hex address");
                return null;
            }
            return value;
        },
    };

    private static Option<T?> EnumOption<T>(string name, string helpName, string description,
        (string Word, T Value)[] map, string expected) where T : struct => new(name)
    {
        HelpName = helpName,
        Description = description,
        Recursive = true,
        Arity = ArgumentArity.ZeroOrOne,   // valueless option must reach the CustomParser guard (legacy wording)
        CustomParser = result =>
        {
            string? token = ValueOrError(result, $"{name}: missing {helpName} value");
            if (token is null)
                return null;
            string text = token.ToLowerInvariant();
            foreach ((string word, T value) in map)
            {
                if (text == word)
                    return value;
            }
            // legacy messages show the lowered text, because the old switch matched on it
            result.AddError($"{name}: expected {expected}, got '{text}'");
            return null;
        },
    };

    private static Option<bool> Flag(string name, string description) => Flag(name, [], description);

    private static Option<bool> Flag(string name, string[] aliases, string description) => new(name, aliases)
    {
        Description = description,
        Recursive = true,
        Arity = ArgumentArity.Zero,   // declarative value rejection: "--tui false" was an error, stays one
    };

    /// <summary>The option's value token, or reports the legacy "missing value" error
    /// (ZeroOrOne arity lets a valueless option through; the old parser never did)
    /// and returns null so the caller can bail.</summary>
    private static string? ValueOrError(ArgumentResult result, string missingMessage)
    {
        if (result.Tokens.Count == 0)
        {
            result.AddError(missingMessage);
            return null;
        }
        return result.Tokens[0].Value;
    }

    /// <summary>Never runs (Parse-only mapping); its presence marks "root invoked without a
    /// subcommand" as valid - the default monitor command.</summary>
    private sealed class MonitorAction : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult) => 0;
    }

    /// <summary>Appends the exit-code contract to the generated help. Writes to the
    /// configuration's output stream - never Console directly - so redirected rendering
    /// (HelpText) captures the footer too.</summary>
    private sealed class ExitCodesHelpAction(HelpAction inner) : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            int result = inner.Invoke(parseResult);
            TextWriter output = parseResult.InvocationConfiguration.Output;
            output.WriteLine();
            output.WriteLine("Exit codes: 0 ok, 1 runtime failure, 2 usage error.");
            return result;
        }
    }
}
