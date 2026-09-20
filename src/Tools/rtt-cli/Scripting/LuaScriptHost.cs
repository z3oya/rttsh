using NLua;
using NLua.Exceptions;   // LuaException/LuaScriptException live here in NLua 1.7.x

namespace Toolbox.Tools.RttCli.Scripting;

/// <summary>Thin NLua binding over ScriptRuntime: builds the "rtt" table, runs the script file and
/// maps engine/Lua errors onto ScriptError. The whole script runs on the caller's thread - the
/// Lua state is never touched from event threads. Lua-side shims coerce/validate arguments so
/// error messages point at the rtt.* name the script actually wrote.</summary>
internal sealed class LuaScriptHost
{
    private readonly ScriptRuntime _runtime;
    private readonly string _scriptPath;

    public LuaScriptHost(ScriptRuntime runtime, string scriptPath)
    {
        _runtime = runtime;
        _scriptPath = scriptPath;
    }

    /// <summary>Executes the script; returns the exit code (rtt.exit(code) or 0).</summary>
    public int Run()
    {
        using var lua = new Lua();
        lua.DoString("""
            local function protect(fn)
                return function(...)
                    local ok, res = pcall(fn, ...)
                    if not ok then error(HOST_describe(res), 2) end
                    return res
                end
            end
            rtt = {
                send = protect(function(text) HOST_send(text) end),
                send_hex = protect(function(hex) HOST_send_hex(hex) end),
                log = protect(function(line) HOST_log(tostring(line)) end),
                wait = protect(function(ms)
                    if ms == nil then error('wait: missing timeout in ms', 0) end
                    return HOST_wait(math.floor(ms))
                end),
                wait_hex = protect(function(ms)
                    if ms == nil then error('wait_hex: missing timeout in ms', 0) end
                    return HOST_wait_hex(math.floor(ms))
                end),
                expect = protect(function(pattern, timeoutMs)
                    if pattern == nil then error('expect: missing pattern', 0) end
                    return HOST_expect(pattern, math.floor(timeoutMs or 1000))
                end),
                now = protect(function() return HOST_now() end),
                sleep = protect(function(ms) HOST_sleep(math.floor(tonumber(ms) or 0)) end),
                exit = function(code) HOST_exit(math.floor(tonumber(code) or 0)) end,
            }
            """);
        lua["HOST_send"] = new Action<string>(_runtime.Send);
        lua["HOST_send_hex"] = new Action<string>(_runtime.SendHex);
        lua["HOST_log"] = new Action<string>(_runtime.Log);
        lua["HOST_wait"] = new Func<int, string>(_runtime.Wait);
        lua["HOST_wait_hex"] = new Func<int, string>(_runtime.WaitHex);
        lua["HOST_expect"] = new Func<string, int, string>(_runtime.Expect);
        lua["HOST_now"] = new Func<double>(_runtime.Now);
        lua["HOST_sleep"] = new Action<int>(_runtime.Sleep);
        lua["HOST_exit"] = new Action<int>(_runtime.Exit);
        lua["HOST_describe"] = new Func<object?, string>(Describe);

        try
        {
            lua.DoFile(_scriptPath);
            return _runtime.ExitCode;
        }
        catch (LuaException ex) when (ex is LuaScriptException { InnerException: ScriptExitSignal signal })
        {
            return signal.Code;
        }
        catch (LuaException ex)
        {
            throw new ScriptError(FormatLuaError(ex));
        }
    }

    /// <summary>Turns a pcall-captured Lua error into a readable message. CLR exceptions cross
    /// as LuaScriptException wrappers whose InnerException carries the real ScriptError text;
    /// plain Lua errors and strings pass through unchanged.</summary>
    private static string Describe(object? error) =>
        error is LuaScriptException { InnerException: { } inner } ? inner.Message
        : error?.ToString() ?? "unknown error";

    private static string FormatLuaError(LuaException ex) =>
        ex is LuaScriptException { IsNetException: true, InnerException: { } inner }
            ? inner.Message
            : ex.Message;
}
