namespace Toolbox.Tools.RttCli.Scripting;

/// <summary>Script-facing failure with a stable message (expect timeouts, link death, send
/// failures). Crosses the Lua boundary mapped to a Lua error by LuaScriptHost. Not sealed:
/// ScriptTimeoutError derives from it.</summary>
internal class ScriptError(string message) : Exception(message);

/// <summary>Raised at rtt.* call boundaries when --script-timeout is exceeded.</summary>
internal sealed class ScriptTimeoutError(string message) : ScriptError(message);

/// <summary>Control-flow signal behind rtt.exit(code). Crosses the NLua boundary wrapped in a
/// LuaScriptException and is unwrapped by LuaScriptHost - the test exit_stops_script_and_returns_code
/// pins that assumption; if NLua ever stops preserving InnerException, switch rtt.exit to a
/// Lua-side error({__rtt_exit = code}, 0) sentinel and match it in LuaScriptHost instead.</summary>
internal sealed class ScriptExitSignal(int code) : Exception("rtt.exit")
{
    public int Code { get; } = code;
}
