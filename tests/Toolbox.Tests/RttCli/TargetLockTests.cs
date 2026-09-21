using Toolbox.Core.Rtt;
using Toolbox.Core.SerialComm;
using Toolbox.Tools.RttCli;

namespace Toolbox.Tests.RttCli;

/// <summary>File-handle semantics of the per-target+channel instance lock: exclusivity,
/// release on Dispose (the OS releases it on process death just the same), holder PID
/// reporting, key sanitization, and the no-false-claim path for unrelated IO failures.</summary>
public class TargetLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rtt-cli-lock-tests", Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static RttConnectionConfig Config(int sn = 12345, int channel = 0) => new()
    {
        Chip = "STM32H743XI",
        Interface = RttInterface.Swd,
        SerialNo = sn,
        Channel = channel,
    };

    [Fact]
    public void Second_instance_on_the_same_target_is_refused_with_the_holder_pid()
    {
        using var first = TargetLock.TryAcquire(Config(channel: 1), TextWriter.Null, _dir);
        Assert.NotNull(first);

        var error = new StringWriter();
        Assert.Null(TargetLock.TryAcquire(Config(channel: 1), error, _dir));
        Assert.Contains($"already held by another rtt-cli (PID {Environment.ProcessId})", error.ToString());
        Assert.Contains("channel 1", error.ToString());   // the warning names the channel from the config
    }

    [Fact]
    public void Disposed_lock_is_immediately_reacquirable()
    {
        var first = TargetLock.TryAcquire(Config(), TextWriter.Null, _dir);
        Assert.NotNull(first);
        first.Dispose();

        using var second = TargetLock.TryAcquire(Config(), TextWriter.Null, _dir);
        Assert.NotNull(second);
    }

    [Fact]
    public void Different_probe_serial_gets_its_own_lock()
    {
        using var a = TargetLock.TryAcquire(Config(sn: 1), TextWriter.Null, _dir);
        using var b = TargetLock.TryAcquire(Config(sn: 2), TextWriter.Null, _dir);
        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    public void Different_channel_gets_its_own_lock()
    {
        using var ch0 = TargetLock.TryAcquire(Config(channel: 0), TextWriter.Null, _dir);
        using var ch1 = TargetLock.TryAcquire(Config(channel: 1), TextWriter.Null, _dir);
        Assert.NotNull(ch0);
        Assert.NotNull(ch1);

        // Regression anchor: the channel is part of the lock key's file name.
        Assert.Contains("STM32H743XI@swd#12345#ch1.lock",
            Directory.GetFiles(_dir, "*.lock").Select(Path.GetFileName));
    }

    [Fact]
    public void Key_is_sanitized_into_a_safe_file_name()
    {
        using var _ = TargetLock.TryAcquire(new RttConnectionConfig { Chip = @"STM32\H743:XI?", SerialNo = 12345 }, TextWriter.Null, _dir);
        string name = Path.GetFileName(Assert.Single(Directory.GetFiles(_dir, "*.lock")));
        Assert.Equal("STM32_H743_XI_@swd#12345#ch0.lock", name);
    }

    [Fact]
    public void Unrelated_io_failure_is_not_reported_as_in_use()
    {
        // A directory parked where the lock file belongs makes the open fail with
        // "access denied" - that must surface as a lock-file problem, never as
        // "target held by PID <stale>".
        Directory.CreateDirectory(Path.Combine(_dir, "STM32H743XI@swd#12345#ch0.lock"));

        var error = new StringWriter();
        Assert.Null(TargetLock.TryAcquire(Config(), error, _dir));
        Assert.Contains("cannot create the instance lock file", error.ToString());
        Assert.DoesNotContain("already held", error.ToString());
    }
}
