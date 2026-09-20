using System.Text;
using Toolbox.Core.SerialComm;
using Toolbox.Tools.RttCli.Scripting;

namespace Toolbox.Tests.RttScripting;

public class LuaScriptHostTests
{
    private sealed record Harness(LuaScriptHost Host, TestRttTransport Transport, ScriptRuntime Runtime, List<string> Log, string ScriptPath) : IDisposable
    {
        public void Dispose()
        {
            try { File.Delete(ScriptPath); } catch { /* best effort */ }
        }
    }

    private static Harness Make(string script)
    {
        var transport = new TestRttTransport();
        var log = new List<string>();
        var runtime = new ScriptRuntime(transport, Encoding.UTF8, [(byte)'\n'], log.Add, 0);
        string path = Path.Combine(Path.GetTempPath(), $"rtt-cli-test-{Guid.NewGuid():N}.lua");
        File.WriteAllText(path, script);
        return new Harness(new LuaScriptHost(runtime, File.ReadAllText(path), "@" + path), transport, runtime, log, path);
    }

    [Fact]
    public void Log_reaches_the_sink()
    {
        using var h = Make("rtt.log('hello')");
        Assert.Equal(0, h.Host.Run());
        Assert.Equal(["hello"], h.Log);
    }

    [Fact]
    public void Send_reaches_transport_with_eol()
    {
        using var h = Make("rtt.send('ping')");
        h.Host.Run();
        Assert.Equal([.."ping"u8.ToArray(), (byte)'\n'], h.Transport.Written[0]);
    }

    [Fact]
    public void Send_hex_reaches_transport_raw()
    {
        using var h = Make("rtt.send_hex('C0 FF EE')");
        h.Host.Run();
        Assert.Equal([0xC0, 0xFF, 0xEE], h.Transport.Written[0]);
    }

    [Fact]
    public void Expect_returns_match_fed_before_start()
    {
        using var h = Make("rtt.log(rtt.expect('OK', 500))");
        h.Transport.Feed("OK"u8.ToArray());
        Assert.Equal(0, h.Host.Run());
        Assert.Equal(["OK"], h.Log);
    }

    [Fact]
    public async Task Expect_finds_data_arriving_mid_wait()
    {
        using var h = Make("rtt.log(rtt.expect('GO', 2000))");
        _ = Task.Run(async () => { await Task.Delay(50); h.Transport.Feed("GO"u8.ToArray()); });
        Assert.Equal(0, h.Host.Run());
        Assert.Equal(["GO"], h.Log);
    }

    [Fact]
    public void Expect_timeout_raises_script_error()
    {
        using var h = Make("rtt.expect('X', 40)");
        Assert.Throws<ScriptError>(() => h.Host.Run());
    }

    [Fact]
    public void Exit_stops_script_and_returns_code()
    {
        // Pins the assumption that NLua preserves a CLR exception from a registered delegate
        // as LuaScriptException.InnerException (see ScriptExitSignal doc for the fallback).
        using var h = Make("rtt.log('a')\nrtt.exit(3)\nrtt.log('b')");
        Assert.Equal(3, h.Host.Run());
        Assert.Equal(["a"], h.Log);
    }

    [Fact]
    public void Lua_syntax_error_maps_to_script_error()
    {
        using var h = Make("this is not lua");
        var ex = Assert.Throws<ScriptError>(() => h.Host.Run());
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Lua_runtime_error_maps_to_script_error()
    {
        using var h = Make("error('boom')");
        var ex = Assert.Throws<ScriptError>(() => h.Host.Run());
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public void Missing_argument_reports_the_function()
    {
        using var h = Make("rtt.wait()");
        var ex = Assert.Throws<ScriptError>(() => h.Host.Run());
        Assert.Contains("wait", ex.Message);
    }

    [Fact]
    public void Send_failure_message_survives_pcall()
    {
        using var h = Make("""
            local ok, e = pcall(rtt.send, 'x')
            if ok then error('expected send to fail', 0) end
            rtt.log(e)
            """);
        h.Transport.WriteFailure = new IOException("gone");
        h.Host.Run();
        Assert.Contains("gone", h.Log[0]);
    }

    [Fact]
    public void Wait_hex_roundtrips_bytes_through_lua()
    {
        using var h = Make("""
            local hx = rtt.wait_hex(200)
            rtt.log(hx)
            """);
        h.Transport.Feed([0x00, 0x41, 0xFF]);
        Assert.Equal(0, h.Host.Run());
        // Decode-side assertion: independent of HexCodec.Format's exact spacing style.
        Assert.True(HexCodec.TryParse(h.Log[0], out byte[] bytes, out string? error), error);
        Assert.Equal([0x00, 0x41, 0xFF], bytes);
    }

    [Fact]
    public void Now_and_sleep_are_usable()
    {
        using var h = Make("""
            local t0 = rtt.now()
            rtt.sleep(30)
            assert(rtt.now() - t0 >= 15, 'clock should advance')
            """);
        Assert.Equal(0, h.Host.Run());
    }

    private sealed record EvalHarness(LuaScriptHost Host, TestRttTransport Transport, ScriptRuntime Runtime, List<string> Log);

    private static EvalHarness MakeEval(string source)
    {
        var transport = new TestRttTransport();
        var log = new List<string>();
        var runtime = new ScriptRuntime(transport, Encoding.UTF8, [(byte)'\n'], log.Add, 0);
        return new EvalHarness(new LuaScriptHost(runtime, source, "=eval"), transport, runtime, log);
    }

    [Fact]
    public void Eval_source_runs_without_a_file()
    {
        var h = MakeEval("rtt.log('inline')");
        Assert.Equal(0, h.Host.Run());
        Assert.Equal(["inline"], h.Log);
    }

    [Fact]
    public void Eval_errors_carry_the_eval_chunk_name()
    {
        var h = MakeEval("error('boom')");
        var ex = Assert.Throws<ScriptError>(() => h.Host.Run());
        Assert.Contains("eval:1:", ex.Message);
    }
}
