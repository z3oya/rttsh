namespace Toolbox.Tools.RttCli.Scripting;

/// <summary>Host over the native Lua session (rttsh_lua_native.dll): installs the
/// "rtt" shim, runs the script source (file contents or --eval text) and maps the
/// outcome onto ScriptError / the exit code. The whole script runs on the caller's
/// thread - the Lua state is never touched from event threads. Lua-side shims
/// coerce/validate arguments so error messages point at the rtt.* name the script
/// actually wrote; host failures arrive as callback statuses the shim turns into
/// Lua errors, so nothing throws across the native boundary. chunkName follows Lua
/// conventions: "@path" renders errors as "path:line", "=eval" as "eval:line".</summary>
internal sealed class LuaScriptHost
{
    private readonly ScriptRuntime _runtime;
    private readonly string _source;
    private readonly string _chunkName;

    public LuaScriptHost(ScriptRuntime runtime, string source, string chunkName)
    {
        _runtime = runtime;
        _source = source;
        _chunkName = chunkName;
    }

    /// <summary>Executes the script; returns the exit code (rtt.exit(code) or 0).
    /// A completed script still yields a pcall-swallowed rtt.exit's code through
    /// ExitCode; a failed chunk throws ScriptError carrying the Lua message
    /// (position + traceback).</summary>
    public int Run()
    {
        using var session = LuaNativeSession.Start(_runtime);
        // expect() gets Lua's own pattern engine: FindEnd re-enters the same state on
        // this same thread, so the injected matcher is safe to call from any callback.
        _runtime.PatternMatcher = session.FindEnd;

        var (shimKind, _, shimMessage) = session.DoString(Shim, "=shim");
        if (shimKind != LuaNative.KindOk)
            throw new InvalidOperationException($"the rtt shim failed to install: {shimMessage}");

        var (kind, code, message) = session.DoString(_source, _chunkName);
        return kind switch
        {
            LuaNative.KindExit => checked((int)code),
            LuaNative.KindError => throw new ScriptError(message ?? "unknown script error"),
            _ => _runtime.ExitCode,
        };
    }

    /// <summary>The shim source: rtt_shim.lua embedded as an assembly resource,
    /// shared with the Rust tests via include_str!.</summary>
    private static readonly string Shim = ReadShim();

    private const string ShimResourceName = "RttSh.Scripting.rtt_shim.lua";

    private static string ReadShim()
    {
        using var stream = typeof(LuaScriptHost).Assembly.GetManifestResourceStream(ShimResourceName)
            ?? throw new InvalidOperationException($"the rtt shim resource is missing from the assembly (expected {ShimResourceName})");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
