using System.Diagnostics;
using System.Text;
using RttSh.Core.Rtt;
using RttSh.Core.SerialComm;
using Toolbox.Tools.RttCli.Scripting;

namespace Toolbox.Tests.RttScripting;

public class ScriptRuntimeTests
{
    private static ScriptRuntime MakeRuntime(TestRttTransport transport, int timeoutMs = 0, List<string>? log = null) =>
        new(transport, Encoding.UTF8, [(byte)'\n'], log is null ? _ => { } : log.Add, timeoutMs);

    [Fact]
    public void Send_appends_configured_eol()
    {
        var t = new TestRttTransport();
        var rt = MakeRuntime(t);
        rt.Send("ping");
        Assert.Equal([.."ping"u8.ToArray(), (byte)'\n'], t.Written[0]);
    }

    [Fact]
    public void Send_hex_sends_raw_bytes_without_eol()
    {
        var t = new TestRttTransport();
        var rt = MakeRuntime(t);
        rt.SendHex("DE AD");
        Assert.Equal([0xDE, 0xAD], t.Written[0]);
    }

    [Fact]
    public void Send_hex_rejects_bad_hex()
    {
        var rt = MakeRuntime(new TestRttTransport());
        var ex = Assert.Throws<ScriptError>(() => rt.SendHex("zz"));
        Assert.Contains("send_hex", ex.Message);
    }

    [Fact]
    public void Send_reports_write_failure()
    {
        var t = new TestRttTransport { WriteFailure = new IOException("gone") };
        var rt = MakeRuntime(t);
        var ex = Assert.Throws<ScriptError>(() => rt.Send("x"));
        Assert.Contains("gone", ex.Message);
    }

    [Fact]
    public void Wait_returns_data_that_arrived_before_the_call()
    {
        var t = new TestRttTransport();
        var rt = MakeRuntime(t);
        t.Feed("abc"u8.ToArray());
        Assert.Equal("abc", rt.Wait(50));
    }

    [Fact]
    public async Task Wait_returns_data_that_arrives_during_the_wait()
    {
        var t = new TestRttTransport();
        var rt = MakeRuntime(t);
        _ = Task.Run(async () => { await Task.Delay(50); t.Feed("xyz"u8.ToArray()); });
        Assert.Equal("xyz", rt.Wait(2000));
    }

    [Fact]
    public void Wait_returns_empty_after_quiet_timeout()
    {
        var rt = MakeRuntime(new TestRttTransport());
        Assert.Equal("", rt.Wait(40));
    }

    [Fact]
    public void Wait_is_a_passive_tap_and_does_not_starve_expect()
    {
        var t = new TestRttTransport();
        var rt = MakeRuntime(t);
        t.Feed("hello"u8.ToArray());
        Assert.Equal("hello", rt.Wait(50));
        Assert.Equal("hello", rt.Expect("hello", 100));
    }

    [Fact]
    public void Expect_returns_text_through_match_end()
    {
        var t = new TestRttTransport();
        var rt = MakeRuntime(t);
        t.Feed("VER 1.2 ok"u8.ToArray());
        Assert.Equal("VER 1.2", rt.Expect("1.2", 100));
    }

    [Fact]
    public void Expect_matches_across_chunk_boundaries()
    {
        var t = new TestRttTransport();
        var rt = MakeRuntime(t);
        t.Feed("VE"u8.ToArray(), "R 1.2"u8.ToArray());
        Assert.Equal("VER 1.2", rt.Expect("VER 1.2", 100));
    }

    [Fact]
    public void Expect_times_out_with_a_clear_error()
    {
        var rt = MakeRuntime(new TestRttTransport());
        var ex = Assert.Throws<ScriptError>(() => rt.Expect("nope", 40));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void Expect_timeout_includes_buffer_tail()
    {
        var t = new TestRttTransport();
        var rt = MakeRuntime(t);
        t.Feed("boot ok\r\n\u001b[31mrtt> "u8.ToArray());
        var ex = Assert.Throws<ScriptError>(() => rt.Expect("NEVER", 40));
        Assert.Contains("buffer tail:", ex.Message);
        Assert.Contains("rtt> ", ex.Message);
        Assert.Contains("boot ok", ex.Message);
        Assert.DoesNotContain("\r", ex.Message);
        Assert.DoesNotContain(ex.Message, c => c < ' ' || c == '\x7f');
        Assert.Contains("\\x1b", ex.Message);
    }

    [Fact]
    public void Expect_timeout_tail_escapes_every_branch()
    {
        var t = new TestRttTransport();
        var rt = MakeRuntime(t);
        // RX is viewed as Latin-1, so each byte below is one tail char: tab, \n, quote,
        // backslash, DEL, \x01 - and a high byte that must pass through unescaped.
        t.Feed(new byte[] { (byte)'A', (byte)'\t', (byte)'B', (byte)'\n', (byte)'C', (byte)'"',
                            (byte)' ', (byte)'D', (byte)'\\', (byte)'E', 0x7f, 0x01, 0xE9 });
        var ex = Assert.Throws<ScriptError>(() => rt.Expect("NEVER", 40));
        Assert.Contains("\\t", ex.Message);
        Assert.Contains("\\n", ex.Message);
        Assert.Contains("\\\"", ex.Message);
        Assert.Contains("\\\\", ex.Message);   // the introducer doubled: the tail stays invertible
        Assert.Contains("\\x7f", ex.Message);
        Assert.Contains("\\x01", ex.Message);
        Assert.Contains("\u00E9", ex.Message);   // printable Latin-1 passes through raw
        Assert.DoesNotContain(ex.Message, c => c < ' ' || c == '\x7f');
    }

    [Fact]
    public void Expect_timeout_tail_keeps_only_the_last_80_chars()
    {
        var t = new TestRttTransport();
        var rt = MakeRuntime(t);
        t.Feed(new byte[] { 0x01 }.Concat(Enumerable.Repeat((byte)'a', 100)).ToArray());
        var ex = Assert.Throws<ScriptError>(() => rt.Expect("NEVER", 40));
        Assert.Contains(new string('a', 80), ex.Message);
        Assert.DoesNotContain(new string('a', 81), ex.Message);
        Assert.DoesNotContain("\\x01", ex.Message);   // the cut-off head did not sneak back in
    }

    [Fact]
    public void Expect_consumes_through_the_match()
    {
        var t = new TestRttTransport();
        var rt = MakeRuntime(t);
        t.Feed("AAxxBB"u8.ToArray());
        Assert.Equal("AA", rt.Expect("AA", 100));
        Assert.Equal("xxBB", rt.Expect("BB", 100));
    }

    [Fact]
    public void Expect_stays_correct_across_the_compaction_boundary()
    {
        var t = new TestRttTransport();
        var rt = MakeRuntime(t);
        var bytes = Enumerable.Repeat((byte)'a', 8190).Concat("MARKER"u8.ToArray()).ToArray();
        t.Feed(bytes);
        string matched = rt.Expect("MARKER", 100);   // match ends at exactly 8196 -> compaction fires
        Assert.Equal(8196, matched.Length);
        Assert.EndsWith("MARKER", matched);
        Assert.Equal("", rt.Wait(20));               // consumed history gone, tap starts clean
    }

    [Fact]
    public void Expect_rejects_empty_pattern()
    {
        var rt = MakeRuntime(new TestRttTransport());
        Assert.Throws<ScriptError>(() => rt.Expect("", 50));
    }

    [Fact]
    public void Expect_honors_an_injected_pattern_matcher()
    {
        // The seam LuaScriptHost fills: any matcher strategy can drive Expect - here a CLR
        // regex stands in for the real Lua engine, pinning consumption through the match end.
        var t = new TestRttTransport();
        var rt = MakeRuntime(t);
        rt.PatternMatcher = (region, pattern) =>
        {
            var m = System.Text.RegularExpressions.Regex.Match(region, pattern);
            return m.Success ? m.Index + m.Length : null;
        };
        t.Feed("n=42 off"u8.ToArray());
        Assert.Equal("n=42", rt.Expect(@"\d+", 100));
        Assert.Equal("", rt.Wait(20));   // consumed through the match end; the tap sees no new data
    }

    [Fact]
    public void Injected_matcher_returning_null_keeps_pumping_until_timeout()
    {
        // null = "not yet", not "failed": the pump keeps waiting for more data.
        var rt = MakeRuntime(new TestRttTransport());
        rt.PatternMatcher = (_, _) => null;
        var ex = Assert.Throws<ScriptError>(() => rt.Expect("x", 40));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void Wait_preserves_every_byte_value_internally()
    {
        var t = new TestRttTransport();
        var rt = MakeRuntime(t);
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        t.Feed(bytes);
        Assert.Equal(bytes, Encoding.Latin1.GetBytes(rt.Wait(50)));
    }

    [Fact]
    public void Wait_hex_roundtrips_every_byte_value()
    {
        var t = new TestRttTransport();
        var rt = MakeRuntime(t);
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        t.Feed(bytes);
        Assert.True(HexCodec.TryParse(rt.WaitHex(50), out byte[] parsed, out string? error), error);
        Assert.Equal(bytes, parsed);
    }

    [Fact]
    public void Wait_returns_early_when_cancelled()
    {
        var rt = MakeRuntime(new TestRttTransport());
        rt.RequestCancel();
        var sw = Stopwatch.StartNew();
        Assert.Equal("", rt.Wait(5000));
        Assert.True(sw.ElapsedMilliseconds < 500, "cancel should wake the pump");
    }

    [Fact]
    public void Expect_reports_cancellation()
    {
        var rt = MakeRuntime(new TestRttTransport());
        rt.RequestCancel();
        var ex = Assert.Throws<ScriptError>(() => rt.Expect("x", 5000));
        Assert.Contains("cancel", ex.Message);
    }

    [Fact]
    public void Sleep_returns_early_on_cancel()
    {
        var rt = MakeRuntime(new TestRttTransport());
        rt.RequestCancel();
        var sw = Stopwatch.StartNew();
        rt.Sleep(5000);
        Assert.True(sw.ElapsedMilliseconds < 500, "cancel should wake the pump");
    }

    [Fact]
    public void Now_advances_monotonically()
    {
        var rt = MakeRuntime(new TestRttTransport());
        double t0 = rt.Now();
        Thread.Sleep(30);
        Assert.True(rt.Now() >= t0 + 15);
    }

    [Fact]
    public void Link_error_fails_the_next_api_call()
    {
        var t = new TestRttTransport();
        var rt = MakeRuntime(t);
        t.Fail("probe gone");
        var ex = Assert.Throws<ScriptError>(() => rt.Expect("x", 50));
        Assert.Contains("probe gone", ex.Message);
    }

    [Fact]
    public void Watchdog_expires_at_api_boundary()
    {
        var rt = MakeRuntime(new TestRttTransport(), timeoutMs: 50);
        Assert.Throws<ScriptTimeoutError>(() => rt.Sleep(200));
    }

    [Fact]
    public void Exit_sets_code_and_raises_the_stop_signal()
    {
        var rt = MakeRuntime(new TestRttTransport());
        var signal = Assert.Throws<ScriptExitSignal>(() => rt.Exit(3));
        Assert.Equal(3, signal.Code);
        Assert.Equal(3, rt.ExitCode);
    }

    // ---- rtt.mem_* / halt (the ITargetMemory-backed API) --------------------------------
    // Named MakeMemRuntime (not an overload of MakeRuntime) so "no backend" can be spelled
    // explicitly as null instead of leaning on overload resolution against TestRttTransport.

    private static ScriptRuntime MakeMemRuntime(ITargetMemory? memory, int timeoutMs = 0) =>
        new(new TestRttTransport(), Encoding.UTF8, [(byte)'\n'], _ => { }, timeoutMs, memory);

    [Fact]
    public void Mem_read_decodes_units_little_endian_and_passes_the_access_width()
    {
        var m = new TestTargetMemory { Data = [0x78, 0x56, 0x34, 0x12, 0x21, 0x43] };
        var rt = MakeMemRuntime(m);
        Assert.Equal([0x12345678u, 0x4321u], rt.MemRead(0, 2, 32));
        Assert.Equal(4u, m.LastAccess);
    }

    [Fact]
    public void Mem_read16_uses_halfword_access()
    {
        var m = new TestTargetMemory { Data = [0x34, 0x12] };
        var rt = MakeMemRuntime(m);
        Assert.Equal([0x1234u], rt.MemRead(0, 1, 16));
        Assert.Equal(2u, m.LastAccess);
    }

    [Fact]
    public void Mem_read8_is_alignment_free()
    {
        var m = new TestTargetMemory { Data = [0x11, 0x22, 0x33] };
        var rt = MakeMemRuntime(m);
        Assert.Equal([0x22u], rt.MemRead(1, 1, 8));
    }

    [Fact]
    public void Mem_read_rejects_bad_width_unaligned_address_and_overcap_counts()
    {
        var rt = MakeMemRuntime(new TestTargetMemory());
        Assert.Contains("mem_read: width", Assert.Throws<ScriptError>(() => rt.MemRead(0, 1, 12)).Message);
        Assert.Contains("aligned", Assert.Throws<ScriptError>(() => rt.MemRead(0x2000_0001, 1, 32)).Message);
        Assert.Contains("aligned", Assert.Throws<ScriptError>(() => rt.MemRead(0x2000_0001, 1, 16)).Message);
        // the cap is on bytes: 300000 x 1 fits, 1100000 x 1 does not, and 300000 x 4 overflows it
        var values = rt.MemRead(0, 300000, 8);
        Assert.Equal(300000, values.Length);
        Assert.Contains("1048576", Assert.Throws<ScriptError>(() => rt.MemRead(0, 1100000, 8)).Message);
        Assert.Contains("1048576", Assert.Throws<ScriptError>(() => rt.MemRead(0, 300000, 32)).Message);
    }

    [Fact]
    public void Mem_read_zero_count_is_a_no_op()
    {
        var m = new TestTargetMemory { Data = [0x01] };
        var rt = MakeMemRuntime(m);
        Assert.Equal([], rt.MemRead(0, 0, 32));
        Assert.Empty(m.ReadAddresses);
    }

    [Fact]
    public void Mem_read_maps_a_negative_dll_code_through_the_error_table()
    {
        var rt = MakeMemRuntime(new TestTargetMemory { ErrorCode = -261 });
        var ex = Assert.Throws<ScriptError>(() => rt.MemRead(0x2000_0000, 1, 32));
        Assert.Contains("mem_read failed at 0x20000000", ex.Message);
        Assert.Contains("-261", ex.Message);
        Assert.Contains("could not find supported CPU", ex.Message);
    }

    [Fact]
    public void Mem_read_without_a_memory_backend_names_the_api()
    {
        var rt = MakeMemRuntime(memory: null);   // explicit: the no-backend case under test
        Assert.Contains("mem_read: this session has no memory access", Assert.Throws<ScriptError>(() => rt.MemRead(0, 1, 32)).Message);
        Assert.Contains("halt: this session has no memory access", Assert.Throws<ScriptError>(() => rt.Halt()).Message);
    }

    [Fact]
    public void Mem_write_encodes_units_little_endian()
    {
        var m = new TestTargetMemory();
        var rt = MakeMemRuntime(m);
        rt.MemWrite(0x2000_0010, [0x1234, 0xABCD], 16);
        Assert.Equal([0x34, 0x12, 0xCD, 0xAB], m.Written[0]);
        Assert.Equal(2u, m.LastAccess);
    }

    [Fact]
    public void Mem_write_rejects_values_that_do_not_fit_the_width()
    {
        var rt = MakeMemRuntime(new TestTargetMemory());
        Assert.Contains("does not fit 16 bits", Assert.Throws<ScriptError>(() => rt.MemWrite(0, [0x1_2345], 16)).Message);
        Assert.Contains("does not fit 8 bits", Assert.Throws<ScriptError>(() => rt.MemWrite(0, [0x100], 8)).Message);
        // 0xFFFFFFFF is a legal 32-bit unit (the >32-bit case is the Lua boundary's job)
        rt.MemWrite(0, [0xFFFF_FFFFu], 32);
    }

    [Fact]
    public void Halt_resume_roundtrip_and_is_halted()
    {
        var m = new TestTargetMemory();
        var rt = MakeMemRuntime(m);
        Assert.False(rt.IsHalted());
        rt.Halt();
        Assert.True(rt.IsHalted());
        Assert.Equal(1, m.Halts);
        rt.Resume();
        Assert.False(rt.IsHalted());
        Assert.Equal(1, m.Resumes);
    }
}
