using System.Diagnostics;
using System.Globalization;
using Scripts.Runtime.Signals;
using Scripts.Runtime.Stdlib;
using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>
/// Intrinsic namespace `time` (SPEC-STDLIB §7.10d): the wall clock, the monotonic clock and an interruptible sleep.
/// Every case runs in the serialized system collection: the wait observes process-wide signals.
/// </summary>
[Collection(SystemTestCollection.Name)]
public sealed class SystemTimeTests
{
    private static RuntimeContext ContextWithRecordedForcedExit(TimeSpan watchdogDelay) =>
        new(
            new Scripts.Runtime.Output.MemoryOutputChannel(),
            new Scripts.Runtime.Output.MemoryOutputChannel(),
            [],
            forceExit: _ => { },
            watchdogDelay: watchdogDelay);

    private static long UnixSeconds(RuntimeContext context) =>
        Assert.IsType<IntegerValue>(Call(context, "time.unix_seconds")).Number;

    private static long MonotonicMilliseconds(RuntimeContext context) =>
        Assert.IsType<IntegerValue>(Call(context, "time.monotonic_milliseconds")).Number;

    private static Value Sleep(RuntimeContext context, long durationMilliseconds) =>
        Call(context, "time.sleep_milliseconds", Integer(durationMilliseconds));

    [Fact]
    public void Unix_seconds_is_floored_wall_clock_utc_within_the_bounds_of_the_call()
    {
        using var context = NewContext();

        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var observed = UnixSeconds(context);
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.InRange(observed, before, after);
    }

    [Fact]
    public void Monotonic_milliseconds_is_non_decreasing_and_grows_after_a_sleep()
    {
        using var context = NewContext();

        var first = MonotonicMilliseconds(context);
        for (var sample = 0; sample < 100; sample++)
        {
            Assert.True(MonotonicMilliseconds(context) >= first);
        }

        var beforeSleep = MonotonicMilliseconds(context);
        Thread.Sleep(30);
        var afterSleep = MonotonicMilliseconds(context);

        // Wide anti-regression guard: Thread.Sleep(30) guarantees a strictly later timestamp.
        Assert.True(afterSleep > beforeSleep, $"{afterSleep} <= {beforeSleep}");
    }

    [Fact]
    public void Sleep_of_zero_returns_ok_immediately()
    {
        using var context = NewContext();
        var stopwatch = Stopwatch.StartNew();

        var result = Sleep(context, 0);
        stopwatch.Stop();

        OkUnit(result);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), stopwatch.Elapsed.ToString());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void A_negative_duration_is_invalid_argument_without_waiting(long durationMilliseconds)
    {
        using var context = NewContext();
        var stopwatch = Stopwatch.StartNew();

        var result = Sleep(context, durationMilliseconds);
        stopwatch.Stop();

        AssertIoError(result, "invalid_argument", durationMilliseconds.ToString(CultureInfo.InvariantCulture));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public void Sleep_of_twenty_milliseconds_waits_at_least_the_wide_lower_bound()
    {
        using var context = NewContext();
        var stopwatch = Stopwatch.StartNew();

        var result = Sleep(context, 20);
        stopwatch.Stop();

        OkUnit(result);
        // Wide lower bound only (scheduler tolerance); the exact budget is not asserted on the JIT runtime.
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(15), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public async Task A_sleep_is_interrupted_promptly_and_raises_the_termination()
    {
        using var context = ContextWithRecordedForcedExit(TimeSpan.FromSeconds(30));
        var raiser = Task.Run(() =>
        {
            Thread.Sleep(100);
            context.Interruption.Raise("SIGINT", InterruptionMonitor.InterruptExitCode);
        });
        var stopwatch = Stopwatch.StartNew();

        var interruption = Assert.Throws<InterruptionTermination>(() => Sleep(context, 30_000));

        await raiser;
        stopwatch.Stop();
        Assert.Equal("SIGINT", interruption.SignalName);
        Assert.Equal(InterruptionMonitor.InterruptExitCode, interruption.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public void A_huge_duration_does_not_overflow_and_an_already_pending_signal_wins()
    {
        using var context = ContextWithRecordedForcedExit(TimeSpan.FromSeconds(30));
        context.Interruption.Raise("SIGTERM", InterruptionMonitor.TerminateExitCode);

        var stopwatch = Stopwatch.StartNew();

        var interruption = Assert.Throws<InterruptionTermination>(() => Sleep(context, long.MaxValue));
        stopwatch.Stop();

        Assert.Equal(InterruptionMonitor.TerminateExitCode, interruption.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public async Task A_huge_duration_waits_in_chunks_and_stays_interruptible()
    {
        using var context = ContextWithRecordedForcedExit(TimeSpan.FromSeconds(30));
        var raiser = Task.Run(() =>
        {
            Thread.Sleep(100);
            context.Interruption.Raise("SIGHUP", InterruptionMonitor.HangUpExitCode);
        });
        var stopwatch = Stopwatch.StartNew();

        var interruption = Assert.Throws<InterruptionTermination>(() => Sleep(context, long.MaxValue));

        await raiser;
        stopwatch.Stop();
        Assert.Equal(InterruptionMonitor.HangUpExitCode, interruption.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public void A_sleep_during_a_suspended_interruption_waits_instead_of_spinning_on_the_signalled_handle()
    {
        using var context = ContextWithRecordedForcedExit(TimeSpan.FromSeconds(30));
        context.Interruption.Raise("SIGINT", InterruptionMonitor.InterruptExitCode);

        using (context.SuspendingInterruptionForDeferredActions())
        {
            var stopwatch = Stopwatch.StartNew();

            OkUnit(Sleep(context, 20));

            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(15), stopwatch.Elapsed.ToString());
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), stopwatch.Elapsed.ToString());
        }

        Assert.Throws<InterruptionTermination>(() => Sleep(context, 1));
    }

    [Fact]
    public void Milliseconds_scaling_splits_quotient_and_remainder()
    {
        Assert.Equal(0, TimeFunctions.ScaleToMilliseconds(0, 1_000_000_000));
        Assert.Equal(1_500, TimeFunctions.ScaleToMilliseconds(1_500_000_000, 1_000_000_000));
        Assert.Equal(1_999, TimeFunctions.ScaleToMilliseconds(1_999_999_999, 1_000_000_000));
        // A timestamp close to long.MaxValue must not overflow when scaled at 1 GHz: the naive
        // timestamp * 1000 would, while the quotient/remainder split yields the exact floor.
        Assert.Equal(9_223_372_036_854, TimeFunctions.ScaleToMilliseconds(long.MaxValue, 1_000_000_000));
    }
}
