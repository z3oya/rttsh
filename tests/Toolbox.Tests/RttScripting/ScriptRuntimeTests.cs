using System.Diagnostics;
using System.Text;
using Toolbox.Core.SerialComm;
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
}
