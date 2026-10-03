namespace Toolbox.Tools.RttCli.Scripting;

/// <summary>Script-facing failure with a stable message (expect timeouts, link death, send
/// failures). Crosses the Lua boundary mapped to a Lua error by LuaScriptHost. Not sealed:
/// ScriptTimeoutError derives from it.</summary>
internal class ScriptError(string message) : Exception(message);

/// <summary>Raised at rtt.* call boundaries when --script-timeout is exceeded.</summary>
internal sealed class ScriptTimeoutError(string message) : ScriptError(message);

/// <summary>The soft expect timeout: Expect(throwOnTimeout: false) throws this
/// signal with the timeout message; the expect trampoline hands it over as
/// CB_TIMEOUT and the shim returns (nil, msg). Expect never returns null.</summary>
internal sealed class ScriptSoftTimeout(string message) : ScriptError(message);

/// <summary>Control-flow signal behind rtt.exit(code). ScriptRuntime.Exit throws it
/// and records ExitCode; the C# exit trampoline catches it and reports success, and
/// the Lua shim then raises the error("__rtt_exit=<code>", 0) sentinel that unwinds
/// the script - the top level classifies it back into an exit code
/// (exit_stops_script_and_returns_code pins the behavior, including the
/// pcall-swallowed variant that still yields the code).</summary>
internal sealed class ScriptExitSignal(int code) : Exception("rtt.exit")
{
    public int Code { get; } = code;
}
