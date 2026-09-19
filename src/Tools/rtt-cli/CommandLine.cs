using System.Globalization;
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
    public string? LogFile { get; set; }
    public int? WaitMs { get; set; }
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
internal sealed record HelpCommand : RttCommand;
internal sealed record UsageErrorCommand(string Message) : RttCommand;

internal sealed class UsageException(string message) : Exception(message);

/// <summary>Hand-rolled argv parsing (the repo has no command-line library; three commands
/// do not justify one). Pure function of string[] - trivially reviewable, no hidden state.</summary>
internal static class CommandLine
{
    public static RttCommand Parse(string[] args)
    {
        if (args.Length == 0)
            return new MonitorCommand(new CommandLineOptions());

        int first = 0;
        switch (args[0])
        {
            case "--help" or "-h" or "help":
                return new HelpCommand();
            case "list-devices":
                first = 1;
                break;
            case "send":
                if (args.Length < 2 || args[1].StartsWith("--"))
                    return new UsageErrorCommand("send: missing <text> payload");
                return new SendCommand(args[1], ParseOptions(args, 2));
            default:
                if (!args[0].StartsWith("--"))
                    return new UsageErrorCommand($"unknown command '{args[0]}'");
                break;   // options only -> default command is monitor
        }

        var options = ParseOptions(args, first);
        return first == 1
            ? new ListDevicesCommand(options.DeviceFilter, options.DllPath)
            : new MonitorCommand(options);
    }

    private static CommandLineOptions ParseOptions(string[] args, int start)
    {
        var options = new CommandLineOptions();
        for (int i = start; i < args.Length; i++)
        {
            string arg = args[i];
            string Next(string what)
            {
                if (i + 1 >= args.Length)
                    throw new UsageException($"{arg}: missing {what} value");
                return args[++i];
            }

            switch (arg)
            {
                case "--chip": options.Chip = Next("device name"); break;
                case "--speed": options.SpeedKhz = ParseInt(Next("speed"), arg); break;
                case "--if": options.Interface = ParseInterface(Next("swd|jtag")); break;
                case "--reset": options.ResetOnConnect = true; break;
                case "--no-reset": options.ResetOnConnect = false; break;
                case "--rtt-addr": options.RttAddress = ParseHex(Next("address"), arg); break;
                case "--rtt-range": options.RttRange = ParseHex(Next("range"), arg); break;
                case "--sn": options.SerialNo = ParseInt(Next("serial number"), arg); break;
                case "--dll": options.DllPath = Next("DLL path"); break;
                case "--eol": options.Eol = ParseEol(Next("lf|cr|crlf|none")); break;
                case "--encoding": options.Encoding = ParseEncoding(Next("utf8|ascii|latin1")); break;
                case "--hex": options.Hex = true; break;
                case "--log": options.LogFile = Next("log file"); break;
                case "--filter": options.DeviceFilter = Next("substring"); break;
                case "--wait": options.WaitMs = ParseInt(Next("milliseconds"), arg); break;
                default:
                    throw new UsageException($"unknown option '{arg}'");
            }
        }
        return options;
    }

    private static int ParseInt(string text, string option)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            throw new UsageException($"{option}: '{text}' is not a number");
        return value;
    }

    private static uint ParseHex(string text, string option)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
            throw new UsageException($"{option}: '{text}' is not a hex address");
        return value;
    }

    private static RttInterface ParseInterface(string text) => text.ToLowerInvariant() switch
    {
        "swd" => RttInterface.Swd,
        "jtag" => RttInterface.Jtag,
        _ => throw new UsageException($"--if: expected swd or jtag, got '{text}'"),
    };

    private static TextEol ParseEol(string text) => text.ToLowerInvariant() switch
    {
        "lf" => TextEol.Lf,
        "cr" => TextEol.Cr,
        "crlf" => TextEol.CrLf,
        "none" => TextEol.None,
        _ => throw new UsageException($"--eol: expected lf, cr, crlf or none, got '{text}'"),
    };

    private static TextEncodingKind ParseEncoding(string text) => text.ToLowerInvariant() switch
    {
        "utf8" => TextEncodingKind.Utf8,
        "ascii" => TextEncodingKind.Ascii,
        "latin1" => TextEncodingKind.Latin1,
        _ => throw new UsageException($"--encoding: expected utf8, ascii or latin1, got '{text}'"),
    };
}
