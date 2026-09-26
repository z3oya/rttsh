using System.Globalization;
using System.Text;
using System.Text.Json;
using RttSh.Core.Rtt;
using RttSh.Core.Rtt.Elf;
using RttSh.Core.SerialComm;
using Toolbox.Tools.RttCli.Scripting;

namespace Toolbox.Tools.RttCli;

/// <summary>Executes the eight MCP tools against rttsh's existing machinery: connect maps onto
/// RttConnectionConfig + ChipValidation + TargetLock + ElfResolver + JLinkRttTransport, the
/// console tools onto ScriptRuntime (the same engine Lua drives), list_devices onto the J-Link
/// device database. The Rust rmcp layer owns the schemas; this side owns the debugger.
///
/// Dispatch is serialized by the native session's dispatch gate (McpNativeSession): the
/// J-Link DLL is not thread-safe, and the session state (one transport, one runtime, one
/// target lock) is single by design. Errors become ok=false envelopes - the message is
/// the actionable evidence the model sees (Expect's timeout message carries the buffer
/// tail, for one). Encoding is UTF-8 and the line ending Lf for MVP; both are
/// per-connect parameters later if boards need them.</summary>
internal sealed class McpToolHost : IDisposable
{
    /// <summary>Test seams: transport, lock directory and chip gate are replaceable, so the
    /// whole tool surface runs without the J-Link DLL or hardware.</summary>
    public sealed record Options
    {
        /// <summary>Null = the real J-Link transport.</summary>
        public Func<RttConnectionConfig, IRttTransport>? TransportFactory { get; init; }
        /// <summary>Null = the default %LOCALAPPDATA% lock directory.</summary>
        public string? LockDirectory { get; init; }
        /// <summary>Null = the real device-database membership check (silently skipped when the
        /// DLL is unavailable, like ChipValidation.EnsureKnown).</summary>
        public Action<string>? ChipValidator { get; init; }
        /// <summary>Memory access for transports that are not ITargetMemory themselves
        /// (test fakes); the real J-Link transport carries its own.</summary>
        public ITargetMemory? Memory { get; init; }
    }

    private const int MaxDeviceNames = 50;

    private readonly Options _options;
    private IRttTransport? _transport;
    private ScriptRuntime? _runtime;
    private TargetLock? _lock;
    private RttConnectionConfig? _config;

    public McpToolHost(Options? options = null) => _options = options ?? new Options();

    /// <summary>The Rust→C# dispatch entry: one tool call, pure compute, answered with an
    /// envelope (the native session serializes execution under its gate and owns the
    /// native-allocation handover). <paramref name="handle"/> identifies the calling
    /// session; the tools themselves don't need it.</summary>
    public McpEnvelope Dispatch(int handle, string method, ReadOnlySpan<byte> request)
    {
        try
        {
            return method switch
            {
                "connect" => new McpEnvelope(true, Connect(Params(request))),
                "disconnect" => new McpEnvelope(true, Disconnect()),
                "get_status" => new McpEnvelope(true, GetStatus()),
                "send" => new McpEnvelope(true, Send(Params(request))),
                "rtt_read" => new McpEnvelope(true, RttRead(Params(request))),
                "expect" => new McpEnvelope(true, Expect(Params(request))),
                "mem_read" => new McpEnvelope(true, MemRead(Params(request))),
                "list_devices" => new McpEnvelope(true, ListDevices(Params(request))),
                _ => throw new InvalidOperationException($"unknown tool '{method}'"),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new McpEnvelope(false, ex.Message);
        }
    }

    // ---- tools -------------------------------------------------------------------------

    private string Connect(JsonElement p)
    {
        if (_transport is not null)
            throw new InvalidOperationException("already connected - disconnect first (one session at a time)");
        string chip = (OptString(p, "chip") ?? "").Trim();
        if (chip.Length == 0)
            throw new InvalidOperationException("connect: chip is required (list exact names with list_devices)");

        string? elf = OptString(p, "elf");
        uint rttAddress = ParseHex(OptString(p, "rtt_address") ?? "0", "rtt_address");
        string note = "";
        if (elf is not null && rttAddress == 0)
            (rttAddress, note) = ResolveElf(elf);
        else if (elf is not null)
            note = "elf ignored: rtt_address already pins the control block";

        int channel = OptInt(p, "channel") ?? 0;
        if (channel is < 0 or > RttConnectionConfig.MaxChannel)
            throw new InvalidOperationException($"channel: expected 0-{RttConnectionConfig.MaxChannel}, got {channel}");

        var config = new RttConnectionConfig
        {
            Chip = chip,
            SpeedKhz = OptInt(p, "speed_khz") ?? RttConnectionConfig.DefaultSpeedKhz,
            Interface = ParseInterface(OptString(p, "interface")),
            // Every CLI command connects without a reset (the --reset flag opts in); an
            // MCP connect must not silently disturb a running target either.
            ResetOnConnect = false,
            SerialNo = OptInt(p, "serial_number") ?? 0,
            Channel = channel,
            RttAddress = rttAddress,
        }.Clamped();

        ValidateChip(config.Chip, config.DllPath);

        _lock = TargetLock.TryAcquire(config, Console.Error, _options.LockDirectory)
            ?? throw new InvalidOperationException(
                $"{config.Chip} channel {config.Channel} is already held by another rttsh - close that instance first, or pick a different probe (serial_number)");

        var transport = _options.TransportFactory?.Invoke(config) ?? new JLinkRttTransport();
        // The runtime wires itself to the transport's events and must exist before Open
        // (ScriptRuntime's contract: construct, then open).
        _runtime = new ScriptRuntime(
            transport,
            Encoding.UTF8,
            PayloadCodec.Terminator(TextEol.Lf, TextEncodingKind.Utf8),
            line => SessionSupport.WriteDiag($"rttsh-mcp: {line}"),
            scriptTimeoutMs: 0,
            memory: transport as ITargetMemory ?? _options.Memory);
        try
        {
            transport.Open(config);
        }
        catch
        {
            _runtime = null;
            _transport = null;
            _lock.Dispose();
            _lock = null;
            throw;
        }

        _transport = transport;
        _config = config;
        return $"connected to {config.Chip} ({config.Interface.ToString().ToLowerInvariant()} @ {config.SpeedKhz} kHz, RTT channel {config.Channel})"
               + (note.Length > 0 ? $" - {note}" : "");
    }

    private string Disconnect()
    {
        if (_transport is null)
            return "not connected";
        _runtime = null;
        _transport.Close();
        _transport = null;
        _config = null;
        _lock?.Dispose();
        _lock = null;
        return "disconnected (target lock released)";
    }

    private string GetStatus()
    {
        if (_transport is null || _config is null)
            return "connected: false";
        string halted;
        try
        {
            halted = (_runtime?.IsHalted() ?? false).ToString().ToLowerInvariant();
        }
        catch (Exception)
        {
            halted = "unknown";   // transports without memory access
        }
        return $"connected: true, chip: {_config.Chip}, channel: {_config.Channel}, halted: {halted}";
    }

    private string Send(JsonElement p)
    {
        Runtime().Send(ReqString(p, "text"));
        return "sent";
    }

    private string RttRead(JsonElement p)
    {
        int timeoutMs = OptInt(p, "timeout_ms") ?? 1000;
        string text = Runtime().ReadAvailable(timeoutMs);
        return text.Length == 0 ? "(no output)" : text;
    }

    private string Expect(JsonElement p)
    {
        // The connection check comes first - "not connected" is the more actionable error.
        // A timeout throws ScriptError whose message carries the escaped buffer tail - the
        // first thing a failing pattern needs (ScriptRuntime's acceptance semantics,
        // unchanged). timeout_ms is required because the inputSchema marks it required.
        ScriptRuntime runtime = Runtime();
        return runtime.Expect(ReqString(p, "pattern"), ReqInt(p, "timeout_ms"));
    }

    private string MemRead(JsonElement p)
    {
        ScriptRuntime runtime = Runtime();
        uint address = ParseHex(ReqString(p, "address"), "address");
        int count = ReqInt(p, "count");
        int width = OptInt(p, "width") ?? 32;
        uint[] values = runtime.MemRead(address, count, width);
        return FormatWords(address, width, values);
    }

    private string ListDevices(JsonElement p)
    {
        string? filter = OptString(p, "filter");
        using var library = new JLinkLibrary();
        if (!library.Load("", out string loadError))
            throw new InvalidOperationException($"J-Link DLL not available ({loadError}) - install the SEGGER J-Link package");
        if (!JLinkDeviceDatabase.TryEnumerate(library, out List<JLinkDeviceRecord> records, out string error))
            throw new InvalidOperationException($"cannot enumerate the J-Link device database: {error}");

        IEnumerable<string> names = records.Select(r => r.Name);
        if (!string.IsNullOrEmpty(filter))
            names = names.Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase));
        List<string> all = [.. names];
        List<string> shown = all.Take(MaxDeviceNames).ToList();

        var text = new StringBuilder($"total: {all.Count}");
        foreach (string name in shown)
            text.Append('\n').Append(name);
        if (all.Count > shown.Count)
            text.Append($"\n... ({all.Count - shown.Count} more - narrow with filter)");
        return text.ToString();
    }

    // ---- pieces ------------------------------------------------------------------------

    public void Dispose()
    {
        _runtime = null;
        try { _transport?.Close(); }
        catch { /* best effort during shutdown */ }
        _transport = null;
        _lock?.Dispose();
        _lock = null;
    }

    private ScriptRuntime Runtime() =>
        _runtime ?? throw new InvalidOperationException("not connected - call connect first");

    private void ValidateChip(string chip, string dllPath)
    {
        if (_options.ChipValidator is not null)
        {
            _options.ChipValidator(chip);
            return;
        }
        ChipValidation.EnsureKnown(chip, dllPath);
    }

    private static (uint Address, string Note) ResolveElf(string elfPath)
    {
        ElfResolver.ElfDecision decision = ElfResolver.Decide(
            explicitAddress: null,
            explicitRange: false,
            elfPath,
            () => ElfImageCache.Load(elfPath, Path.Combine(Environment.CurrentDirectory, ConfigFile.DirName, "elf-cache")));
        // Fallbacks degrade to the SDK RAM scan exactly like the CLI (a note, not an error);
        // file-level problems throw UsageException and surface as tool errors.
        return decision.Override ? (decision.Address, decision.Message) : (0, decision.Message);
    }

    private static RttInterface ParseInterface(string? text) => text?.ToLowerInvariant() switch
    {
        null or "" => RttConnectionConfig.DefaultInterface,
        "swd" => RttInterface.Swd,
        "jtag" => RttInterface.Jtag,
        _ => throw new InvalidOperationException($"interface: expected swd or jtag, got '{text}'"),
    };

    private static string FormatWords(uint address, int width, uint[] values)
    {
        int unit = width / 8;
        string digits = width switch { 8 => "X2", 16 => "X4", _ => "X8" };
        var text = new StringBuilder();
        for (int i = 0; i < values.Length; i++)
        {
            if (i % 8 == 0)
            {
                if (i > 0)
                    text.Append('\n');
                text.Append($"0x{address + (uint)(i * unit):X8}:");
            }
            text.Append(" 0x").Append(values[i].ToString(digits, CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    // ---- parameter helpers ---------------------------------------------------

    private static JsonElement Params(ReadOnlySpan<byte> request)
    {
        using var document = JsonDocument.Parse(request.ToArray());
        if (document.RootElement.TryGetProperty("params", out var parameters)
            && parameters.ValueKind == JsonValueKind.Object)
        {
            return parameters.Clone();
        }
        using var empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }

    private static string? OptString(JsonElement p, string name) =>
        p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string ReqString(JsonElement p, string name) =>
        OptString(p, name) ?? throw new InvalidOperationException($"{name} is required");

    private static int ReqInt(JsonElement p, string name) =>
        OptInt(p, name) ?? throw new InvalidOperationException($"{name} is required");

    private static int? OptInt(JsonElement p, string name) =>
        p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int parsed)
            ? parsed
            : null;

    private static uint ParseHex(string text, string name)
    {
        string digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (!uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
            throw new InvalidOperationException($"{name}: '{text}' is not a hex address");
        return value;
    }
}
