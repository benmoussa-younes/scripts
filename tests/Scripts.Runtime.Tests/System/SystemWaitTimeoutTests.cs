using System.Diagnostics;
using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>
/// <c>process.wait_for_process</c> with a positive deadline (SPEC-STDLIB §7.3): a bounded wait counted from the call,
/// the handle consumed in every outcome, and the whole spawned tree terminated on expiry.
/// </summary>
[Collection(SystemTestCollection.Name)]
public sealed class SystemWaitTimeoutTests
{
    private static RecordValue Spawn(RuntimeContext context, string program, params string[] arguments) =>
        Ok<RecordValue>(Call(context, "process.spawn", Text(program), TextList(arguments)));

    private static Value WaitWithTimeout(RuntimeContext context, RecordValue handle, long timeoutMilliseconds) =>
        Call(context, "process.wait_for_process", handle, Integer(timeoutMilliseconds));

    [Fact]
    public void Expiry_terminates_the_child_and_reports_timed_out_with_the_program_as_subject()
    {
        using var context = NewContext();
        var handle = Spawn(context, "sleep", "5");
        var stopwatch = Stopwatch.StartNew();

        var result = WaitWithTimeout(context, handle, 100);
        stopwatch.Stop();

        AssertProcessError(result, "timed_out", "sleep");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"the deadline took {stopwatch.Elapsed}");
        AssertProcessError(Call(context, "process.wait_for_process", handle, Integer(0)), "invalid_argument");
        AssertProcessError(Call(context, "process.wait_for_process", handle, Integer(1000)), "invalid_argument");
        AssertProcessError(Call(context, "process.terminate", handle), "invalid_argument");
    }

    [Theory]
    [InlineData(5000)]
    [InlineData(long.MaxValue)]
    public void A_child_that_ends_before_the_deadline_returns_its_outputs(long timeoutMilliseconds)
    {
        using var context = NewContext();
        var handle = Spawn(context, "/bin/sh", "-c", "echo fils; echo err >&2; exit 3");

        var outcome = Ok<RecordValue>(WaitWithTimeout(context, handle, timeoutMilliseconds));

        Assert.Equal(3, outcome.GetInteger("exit_code"));
        Assert.Equal("fils\n", outcome.GetText("standard_output"));
        Assert.Equal("err\n", outcome.GetText("standard_error"));
        Assert.Equal("/bin/sh", outcome.GetText("program"));
    }

    [Fact]
    public void A_negative_timeout_is_invalid_argument_and_consumes_the_handle()
    {
        using var context = NewContext();
        var handle = Spawn(context, "sleep", "30");

        // A valid handle names its spawned program, even when the timeout itself is rejected.
        AssertProcessError(WaitWithTimeout(context, handle, -1), "invalid_argument", "sleep");
        AssertProcessError(Call(context, "process.wait_for_process", handle, Integer(0)), "invalid_argument");
        AssertProcessError(Call(context, "process.terminate", handle), "invalid_argument");

        // The child was disposed by the invalid-timeout path; the run shutdown terminates the unwaited child.
        context.Shutdown();
    }

    [Fact]
    public void A_zero_timeout_waits_without_deadline()
    {
        using var context = NewContext();
        var handle = Spawn(context, "/bin/sh", "-c", "sleep 0.2; echo done");

        var outcome = Ok<RecordValue>(WaitWithTimeout(context, handle, 0));

        Assert.Equal(0, outcome.GetInteger("exit_code"));
        Assert.Equal("done\n", outcome.GetText("standard_output"));
        AssertProcessError(Call(context, "process.terminate", handle), "invalid_argument");
    }

    [Fact]
    public async Task Concurrent_wait_and_kill_consume_the_handle_exactly_once()
    {
        for (var iteration = 0; iteration < 30; iteration++)
        {
            using var context = NewContext();
            var handle = Spawn(context, "sleep", "30");
            using var start = new ManualResetEventSlim(false);

            var wait = Task.Run(() =>
            {
                start.Wait();
                return WaitWithTimeout(context, handle, 100);
            });
            var kill = Task.Run(() =>
            {
                start.Wait();
                return Call(context, "process.terminate", handle);
            });
            start.Set();
            var results = await Task.WhenAll(wait, kill);

            var waitInvalid = IsInvalidArgument(results[0]);
            var killInvalid = IsInvalidArgument(results[1]);
            Assert.True(
                waitInvalid ^ killInvalid,
                $"iteration {iteration}: only one side may consume the handle (wait invalid={waitInvalid}, kill invalid={killInvalid})");
        }
    }

    [Fact]
    public async Task Two_concurrent_kills_reserve_the_handle_exactly_once()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            using var context = NewContext();
            var handle = Spawn(context, "sleep", "30");
            using var start = new ManualResetEventSlim(false);

            var first = Task.Run(() =>
            {
                start.Wait();
                return Call(context, "process.terminate", handle);
            });
            var second = Task.Run(() =>
            {
                start.Wait();
                return Call(context, "process.terminate", handle);
            });
            start.Set();
            var results = await Task.WhenAll(first, second);

            var okCount = results.Count(result => Assert.IsType<ResultValue>(result).IsOk);
            var invalidCount = results.Count(IsInvalidArgument);
            Assert.Equal(1, okCount);
            Assert.Equal(1, invalidCount);
        }
    }

    private static bool IsInvalidArgument(Value value) =>
        value is ResultValue { IsOk: false } result
        && result.Payload is RecordValue error
        && error.GetText("kind") == "invalid_argument";

    [Fact]
    public void Expiry_terminates_the_whole_spawned_tree()
    {
        using var workspace = new SystemTestWorkspace();
        var pidFile = workspace.Combine("pid");
        var script = SystemScripts.CreateExecutable(
            workspace,
            "spawned-tree",
            $"#!/bin/sh\nsleep 30 &\necho $! > '{pidFile}'\nwait\n");
        using var context = NewContext();
        var handle = Spawn(context, script);
        Assert.True(WaitUntil(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0));
        var grandchild = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);

        AssertProcessError(WaitWithTimeout(context, handle, 200), "timed_out", script);

        Assert.True(WaitUntil(() => !IsAlive(grandchild), 6000), "the spawned tree survived the deadline");
    }

    [Fact]
    public void A_child_exiting_just_before_the_deadline_is_ok()
    {
        using var context = NewContext();
        var handle = Spawn(context, "/bin/sh", "-c", "sleep 0.1; echo done");

        var outcome = Ok<RecordValue>(WaitWithTimeout(context, handle, 1000));

        Assert.Equal(0, outcome.GetInteger("exit_code"));
        Assert.Equal("done\n", outcome.GetText("standard_output"));
        Assert.Equal("/bin/sh", outcome.GetText("program"));
    }
}
