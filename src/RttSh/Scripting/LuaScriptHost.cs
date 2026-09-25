using System.Globalization;
using NLua;
using NLua.Exceptions;   // LuaException/LuaScriptException live here in NLua 1.7.x

namespace Toolbox.Tools.RttCli.Scripting;

/// <summary>Thin NLua binding over ScriptRuntime: builds the "rtt" table, runs a script source
/// (file contents or --eval text) and maps engine/Lua errors onto ScriptError. The whole script
/// runs on the caller's thread - the Lua state is never touched from event threads. Lua-side
/// shims coerce/validate arguments so error messages point at the rtt.* name the script actually
/// wrote. chunkName follows Lua conventions: "@path" renders errors as "path:line", "=eval"
/// renders them as "eval:line".</summary>
internal sealed class LuaScriptHost
{
    private readonly ScriptRuntime _runtime;
    private readonly string _source;
    private readonly string _chunkName;
    /// <summary>The live Lua state while Run executes; only touched on the single script thread.
    /// The mem API builds its result tables from this state.</summary>
    private Lua? _state;

    /// <summary>The state, or a loud failure when a HOST_* callback somehow runs outside Run -
    /// the mem table builder needs the same state the script runs on.</summary>
    private Lua State => _state ?? throw new InvalidOperationException("rtt.* called outside a running script host");

    public LuaScriptHost(ScriptRuntime runtime, string source, string chunkName)
    {
        _runtime = runtime;
        _source = source;
        _chunkName = chunkName;
    }

    /// <summary>Executes the script; returns the exit code (rtt.exit(code) or 0).</summary>
    public int Run()
    {
        using var lua = new Lua();
        _state = lua;
        lua.DoString("""
            local function protect(fn)
                return function(...)
                    local ok, res = pcall(fn, ...)
                    if not ok then error(HOST_describe(res), 2) end
                    return res
                end
            end
            local function floor_arg(v, message)
                local n = tonumber(v)
                if n == nil then error(message, 0) end
                return math.floor(n)
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
                mem_read = protect(function(addr, count, width)
                    if addr == nil then error('mem_read: missing address', 0) end
                    if count == nil then error('mem_read: missing count', 0) end
                    local w = width == nil and 32 or floor_arg(width, 'mem_read: width must be a number')
                    return HOST_mem_read(floor_arg(addr, 'mem_read: address must be a number'),
                                         floor_arg(count, 'mem_read: count must be a number'), w)
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
                        return HOST_mem_write_table(a, copy, n, w)
                    end
                    return HOST_mem_write_one(a, floor_arg(values, 'mem_write: value must be a number or table'), w)
                end),
                is_halted = protect(function() return HOST_is_halted() end),
                halt = protect(function() HOST_halt() end),
                resume = protect(function() HOST_resume() end),
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
        lua["HOST_mem_read"] = new Func<long, long, int, LuaTable>(MemRead);
        lua["HOST_mem_write_one"] = new Action<long, long, int>(MemWriteOne);
        lua["HOST_mem_write_table"] = new Action<long, LuaTable, long, int>(MemWriteTable);
        lua["HOST_is_halted"] = new Func<bool>(_runtime.IsHalted);
        lua["HOST_halt"] = new Action(_runtime.Halt);
        lua["HOST_resume"] = new Action(_runtime.Resume);
        lua["HOST_describe"] = new Func<object?, string>(Describe);

        // expect() gets Lua's own pattern engine: string.find lives in this state, and expect
        // runs on this same thread, so the injected matcher is safe to call. string.find's
        // 1-based inclusive end == the number of chars the runtime must consume. Malformed
        // patterns raise inside find.Call - rewrap so the Lua message reaches the script.
        LuaFunction find = lua.GetFunction("string.find");
        _runtime.PatternMatcher = (region, pattern) =>
        {
            object?[] res;
            try
            {
                res = find.Call(region, pattern, 1, false);
            }
            catch (LuaException ex)
            {
                throw new ScriptError($"expect: {ex.Message}");
            }
            return res.Length > 1 && res[1] is not null ? Convert.ToInt32(res[1]) : null;
        };

        try
        {
            lua.DoString(_source, _chunkName);
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

    // ---- HOST_* implementations for the mem API ------------------------------------------
    // Lua numbers arrive as long/int via the delegates (the Lua shims already floor them);
    // the table path still re-checks numeric-ness and integer-ness per element - the shim
    // validates the copy it builds and this side re-validates what NLua hands back, so
    // neither layer trusts the other. What remains here is the 32-bit range checking C#
    // needs. Values cross into Lua as a fresh 1-based table built here - explicit
    // construction, so no NLua array-marshaling guesswork.

    private LuaTable MemRead(long addr, long count, int width)
    {
        uint[] values = _runtime.MemRead(Addr(addr, "mem_read"), Count(count, "mem_read"), width);
        var table = (LuaTable)(State.DoString("return {}")[0]
            ?? throw new ScriptError("mem_read: table allocation failed"));
        for (int i = 0; i < values.Length; i++)
            table[i + 1] = (long)values[i];   // Lua is 1-based
        return table;
    }

    private void MemWriteOne(long addr, long value, int width)
    {
        _runtime.MemWrite(Addr(addr, "mem_write"), [CheckUint(value, "mem_write", 1)], width);
    }

    private void MemWriteTable(long addr, LuaTable table, long count, int width)
    {
        int n = Count(count, "mem_write");
        var values = new uint[n];
        for (long i = 1; i <= n; i++)
        {
            double number = table[i] switch
            {
                double d => d,
                long l => l,
                int iv => iv,
                IConvertible convertible => convertible.ToDouble(CultureInfo.InvariantCulture),
                _ => throw new ScriptError($"mem_write: value #{i} is not a number"),
            };
            if (Math.Floor(number) != number)
                throw new ScriptError($"mem_write: value #{i} ({number}) is not an integer");
            values[i - 1] = CheckUint((long)number, "mem_write", i);
        }
        _runtime.MemWrite(Addr(addr, "mem_write"), values, width);
    }

    private static uint Addr(long value, string what) =>
        value is >= 0 and <= uint.MaxValue
            ? (uint)value
            : throw new ScriptError($"{what}: address {value} is out of the 32-bit range");

    private static int Count(long value, string what) =>
        value is >= 0 and <= int.MaxValue
            ? (int)value
            : throw new ScriptError($"{what}: count must be between 0 and {int.MaxValue}");

    private static uint CheckUint(long value, string what, long index) =>
        value is >= 0 and <= uint.MaxValue
            ? (uint)value
            : throw new ScriptError($"{what}: value #{index} ({value}) is not an unsigned 32-bit integer");

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
