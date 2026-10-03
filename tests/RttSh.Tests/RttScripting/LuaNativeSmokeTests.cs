using System.Text;
using Toolbox.Tools.RttCli.Scripting;

namespace Toolbox.Tests.RttScripting;

/// <summary>HOST_*-level smoke tests over the native Lua session (no shim): the
/// P/Invoke plumbing, the GCHandle trampoline routing and the string/i64
/// handover discipline. Shim behavior — the rtt table, the exit sentinel
/// unwind, protect/positions — is pinned by LuaScriptHostTests; the state's own
/// pattern engine is pinned Rust-side (rttsh-lua's cargo tests) and here
/// top-level via <see cref="LuaNativeSession.FindEnd"/>.</summary>
public class LuaNativeSmokeTests
{
    private sealed record Harness(LuaNativeSession Session, TestRttTransport Transport, ScriptRuntime Runtime, TestTargetMemory Memory) : IDisposable
    {
        public void Dispose() => Session.Dispose();
    }

    private static Harness Make(TestTargetMemory? memory = null)
    {
        memory ??= new TestTargetMemory();
        var transport = new TestRttTransport();
        var log = new List<string>();
        var runtime = new ScriptRuntime(transport, Encoding.UTF8, [(byte)'\n'], log.Add, 0, memory);
        return new Harness(LuaNativeSession.Start(runtime), transport, runtime, memory);
    }

    [Fact]
    public void Abi_matches_and_a_trivial_chunk_runs()
    {
        Assert.Equal(LuaNative.AbiVersion, LuaNative.ProbeAbiVersion());
        using var h = Make();
        var (kind, code, message) = h.Session.DoString("return 1", "=t");
        Assert.Equal(LuaNative.KindOk, kind);
        Assert.Equal(0, code);
        Assert.Null(message);
    }

    [Fact]
    public void Send_trampoline_reaches_the_transport()
    {
        using var h = Make();
        h.Session.DoString("HOST_send('hello')", "=t");
        Assert.Equal([.."hello"u8.ToArray(), (byte)'\n'], h.Transport.Written[0]);
    }

    [Fact]
    public void Wait_hands_the_arrived_text_over()
    {
        using var h = Make();
        h.Transport.Feed("abc"u8.ToArray());
        var (kind, _, message) = h.Session.DoString("local st, v = HOST_wait(500) error('[' .. v .. ']', 0)", "=t");
        Assert.Equal(LuaNative.KindError, kind);
        Assert.StartsWith("[abc]", message); // mlua appends the Lua traceback
    }

    [Fact]
    public void Exit_trampoline_records_the_code()
    {
        using var h = Make();
        var (kind, _, _) = h.Session.DoString("HOST_exit(42)", "=t");
        Assert.Equal(LuaNative.KindOk, kind);
        Assert.Equal(42, h.Runtime.ExitCode);
    }

    [Fact]
    public void Mem_read_hands_i64s_over_and_mem_write_round_trips()
    {
        var memory = new TestTargetMemory
        {
            // 1e9, 2e9, 3e9 as little-endian u32s; the i64 sum exceeds 2^32
            Data = [0x00, 0xCA, 0x9A, 0x3B, 0x00, 0x94, 0x35, 0x77, 0x00, 0x5E, 0xD0, 0xB2],
        };
        using var h = Make(memory);
        var (kind, _, message) = h.Session.DoString(
            "local st, t = HOST_mem_read(0, 3, 32) " +
            "local sum = 0 for i = 1, #t do sum = sum + t[i] end error(sum, 0)", "=t");
        Assert.Equal(LuaNative.KindError, kind);
        Assert.StartsWith("6000000000", message); // mlua appends the Lua traceback
        Assert.Equal(4u, memory.LastAccess);

        h.Session.DoString("HOST_mem_write_one(0x20, 42, 16)", "=t");
        Assert.Equal([(byte)42, (byte)0], memory.Written[0]);

        h.Session.DoString("HOST_mem_write_table(0x30, {10, 20, 30}, 3, 8)", "=t");
        Assert.Equal([(byte)10, (byte)20, (byte)30], memory.Written[1]);
    }

    [Fact]
    public void Host_failure_status_carries_the_message_back()
    {
        using var h = Make();
        var (kind, _, message) = h.Session.DoString("local st, msg = HOST_send_hex('zz') error(msg, 0)", "=t");
        Assert.Equal(LuaNative.KindError, kind);
        Assert.NotNull(message);
        Assert.Contains("send_hex:", message);
    }

    [Fact]
    public void FindEnd_maps_the_pattern_engine_results()
    {
        using var h = Make();
        Assert.Equal(11, h.Session.FindEnd("hello world", "w[a-z]+"));   // 1-based inclusive end
        Assert.Null(h.Session.FindEnd("abc", "zzz"));
        var ex = Assert.Throws<ScriptError>(() => h.Session.FindEnd("x", "["));
        Assert.Contains("malformed", ex.Message);
    }
}
