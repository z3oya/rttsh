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

    /// <summary>The rtt table the script sees: every entry validates its arguments
    /// Lua-side (so messages point at the rtt.* name the script wrote) and checks
    /// the host callback status, raising CB_ERR payloads as errors at level 0;
    /// protect() re-raises with the caller's position. rtt.exit is deliberately
    /// unprotected - it raises the __rtt_exit= sentinel that unwinds to the top
    /// level. Keep in sync with the Rust test fixture (SHIM in native/rttsh-lua) -
    /// the host tests pin the shared behavior.</summary>
    private const string Shim = """
        local function protect(fn)
            return function(...)
                local ok, res = pcall(fn, ...)
                if not ok then error(res, 2) end
                return res
            end
        end
        local function floor_arg(v, message)
            local n = tonumber(v)
            if n == nil then error(message, 0) end
            return math.floor(n)
        end
        rtt = {
            send = protect(function(text)
                local st, msg = HOST_send(text)
                if st ~= 0 then error(msg, 0) end
            end),
            send_hex = protect(function(hex)
                local st, msg = HOST_send_hex(hex)
                if st ~= 0 then error(msg, 0) end
            end),
            log = protect(function(line)
                local st, msg = HOST_log(tostring(line))
                if st ~= 0 then error(msg, 0) end
            end),
            wait = protect(function(ms)
                if ms == nil then error('wait: missing timeout in ms', 0) end
                local st, res = HOST_wait(math.floor(ms))
                if st ~= 0 then error(res, 0) end
                return res
            end),
            wait_hex = protect(function(ms)
                if ms == nil then error('wait_hex: missing timeout in ms', 0) end
                local st, res = HOST_wait_hex(math.floor(ms))
                if st ~= 0 then error(res, 0) end
                return res
            end),
            expect = protect(function(pattern, timeoutMs)
                if pattern == nil then error('expect: missing pattern', 0) end
                local st, res = HOST_expect(pattern, math.floor(timeoutMs or 1000))
                if st ~= 0 then error(res, 0) end
                return res
            end),
            now = protect(function() return HOST_now() end),
            sleep = protect(function(ms)
                local st, msg = HOST_sleep(math.floor(tonumber(ms) or 0))
                if st ~= 0 then error(msg, 0) end
            end),
            exit = function(code)
                local n = math.floor(tonumber(code) or 0)
                local st, msg = HOST_exit(n)
                if st ~= 0 then error(msg, 0) end
                error('__rtt_exit=' .. n, 0)
            end,
            mem_read = protect(function(addr, count, width)
                if addr == nil then error('mem_read: missing address', 0) end
                if count == nil then error('mem_read: missing count', 0) end
                local w = width == nil and 32 or floor_arg(width, 'mem_read: width must be a number')
                local st, res = HOST_mem_read(floor_arg(addr, 'mem_read: address must be a number'),
                                             floor_arg(count, 'mem_read: count must be a number'), w)
                if st ~= 0 then error(res, 0) end
                return res
            end),
            mem_write = protect(function(addr, values, width)
                if addr == nil then error('mem_write: missing address', 0) end
                if values == nil then error('mem_write: missing value(s)', 0) end
                local a = floor_arg(addr, 'mem_write: address must be a number')
                local w = width == nil and 32 or floor_arg(width, 'mem_write: width must be a number')
                if type(values) == 'table' then
                    local n = #values
                    if n == 0 then return end
                    local copy = {}
                    for i = 1, n do
                        local v = tonumber(values[i])
                        if v == nil then error('mem_write: value #' .. i .. ' is not a number', 0) end
                        copy[i] = math.floor(v)
                    end
                    local st, msg = HOST_mem_write_table(a, copy, n, w)
                    if st ~= 0 then error(msg, 0) end
                    return
                end
                local st, msg = HOST_mem_write_one(a, floor_arg(values, 'mem_write: value must be a number or table'), w)
                if st ~= 0 then error(msg, 0) end
            end),
            is_halted = protect(function()
                local st, v = HOST_is_halted()
                if st ~= 0 then error(v, 0) end
                return v
            end),
            halt = protect(function()
                local st, msg = HOST_halt()
                if st ~= 0 then error(msg, 0) end
            end),
            resume = protect(function()
                local st, msg = HOST_resume()
                if st ~= 0 then error(msg, 0) end
            end),
        }
        """;
}
