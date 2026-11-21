using System.Diagnostics;
using Scripts.Runtime.Platform;
using Scripts.Runtime.Signals;
using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

[Collection(SystemTestCollection.Name)]
public sealed class SystemInterruptionMonitorTests
{
    private static RuntimeContext ContextWithRecordedForcedExit(List<int> forcedExits, TimeSpan watchdogDelay) =>
        new(
            new Scripts.Runtime.Output.MemoryOutputChannel(),
            new Scripts.Runtime.Output.MemoryOutputChannel(),
            [],
            forceExit: forcedExits.Add,
            watchdogDelay: watchdogDelay);

    [Fact]
    public void Nothing_is_pending_until_raised_then_the_first_signal_wins()
    {
        using var context = NewContext();

        context.ThrowIfInterrupted();
        Assert.False(context.Interruption.IsInterrupted);
        Assert.False(context.Interruption.WaitHandle.WaitOne(0));

        context.Interruption.Raise("SIGINT", 130);
        context.Interruption.Raise("SIGTERM", 143);

        var interruption = Assert.Throws<InterruptionTermination>(context.ThrowIfInterrupted);
        Assert.Equal("SIGINT", interruption.SignalName);
        Assert.Equal(130, interruption.ExitCode);
        Assert.True(context.Interruption.WaitHandle.WaitOne(0));
        Assert.Throws<InterruptionTermination>(context.ThrowIfInterrupted);
    }

    [Fact]
    public void The_watchdog_forces_exit_with_the_signal_code_when_nobody_unwinds()
    {
        var forcedExits = new List<int>();
        using var context = ContextWithRecordedForcedExit(forcedExits, TimeSpan.FromMilliseconds(100));

        context.Interruption.Raise("SIGTERM", 143);

        Assert.True(WaitUntil(() => forcedExits.Count == 1, 3000));
        Assert.Equal([143], forcedExits);
    }

    [Fact]
    public void Disposing_the_context_disarms_the_watchdog()
    {
        var forcedExits = new List<int>();
        var context = ContextWithRecordedForcedExit(forcedExits, TimeSpan.FromMilliseconds(300));

        context.Interruption.Raise("SIGHUP", 129);
        context.Dispose();
        Thread.Sleep(800);

        Assert.Empty(forcedExits);
    }

    [Fact]
    public async Task A_blocked_process_run_returns_promptly_with_the_interruption_and_the_child_is_terminated()
    {
        using var workspace = new SystemTestWorkspace();
        var pidFile = workspace.Combine("pid");
        var script = SystemScripts.CreateExecutable(workspace, "sleeper", $"#!/bin/sh\necho $$ > '{pidFile}'\nexec sleep 30\n");
        using var context = ContextWithRecordedForcedExit([], TimeSpan.FromSeconds(30));
        var raiser = Task.Run(() =>
        {
            WaitUntil(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0);
            context.Interruption.Raise("SIGINT", 130);
        });
        var stopwatch = Stopwatch.StartNew();

        var interruption = Assert.Throws<InterruptionTermination>(() => SystemScripts.ExecResult(context, script));

        await raiser;
        Assert.Equal(130, interruption.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), stopwatch.Elapsed.ToString());
        var processIdentifier = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.True(WaitUntil(() => !IsAlive(processIdentifier)));
    }

    [Fact]
    public async Task A_blocked_wait_for_process_and_process_run_inherit_observe_the_interruption()
    {
        using var context = ContextWithRecordedForcedExit([], TimeSpan.FromSeconds(30));
        var handle = Ok<RecordValue>(Call(context, "process.spawn", Text("sleep"), TextList("30")));
        var processIdentifier = (int)handle.GetInteger("process_identifier");
        var raiser = Task.Run(() =>
        {
            Thread.Sleep(200);
            context.Interruption.Raise("SIGTERM", 143);
        });

        var waitInterruption = Assert.Throws<InterruptionTermination>(() => Call(context, "process.wait_for_process", handle, Integer(0)));
        await raiser;

        Assert.Equal(143, waitInterruption.ExitCode);
        Assert.True(WaitUntil(() => !IsAlive(processIdentifier)));
        Assert.Throws<InterruptionTermination>(() => SystemScripts.InheritResult(context, "sleep", "30"));
    }

    [Fact]
    public void Nothing_is_launched_once_an_interruption_is_pending()
    {
        using var workspace = new SystemTestWorkspace();
        var marker = workspace.Combine("marker");
        var script = SystemScripts.CreateExecutable(workspace, "mark", $"#!/bin/sh\ntouch '{marker}'\n");
        using var context = ContextWithRecordedForcedExit([], TimeSpan.FromSeconds(30));
        context.Interruption.Raise("SIGINT", 130);

        Assert.Throws<InterruptionTermination>(() => SystemScripts.ExecResult(context, script));
        Assert.Throws<InterruptionTermination>(() => Call(context, "process.spawn", Text(script), TextList()));

        Thread.Sleep(200);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task A_child_ignoring_sigterm_receives_sigkill_after_two_seconds_on_interruption()
    {
        using var workspace = new SystemTestWorkspace();
        var pidFile = workspace.Combine("pid");
        // The trap comes first: the pid file is the readiness marker, so the signal cannot arrive before it is set.
        var script = SystemScripts.CreateExecutable(workspace, "stubborn", $"#!/bin/sh\ntrap '' TERM\necho $$ > '{pidFile}'\nexec sleep 30\n");
        using var context = ContextWithRecordedForcedExit([], TimeSpan.FromSeconds(30));
        var raiser = Task.Run(() =>
        {
            WaitUntil(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0);
            Thread.Sleep(200);
            context.Interruption.Raise("SIGINT", 130);
        });

        Assert.Throws<InterruptionTermination>(() => SystemScripts.ExecResult(context, script));
        await raiser;

        var processIdentifier = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.True(IsAlive(processIdentifier));
        Assert.True(WaitUntil(() => !IsAlive(processIdentifier), 6000));
    }

    [Fact]
    public void Print_and_the_other_stdlib_calls_do_not_check_for_interruption_themselves()
    {
        using var context = NewContext(out var captured);
        context.Interruption.Raise("SIGINT", 130);

        Call(context, "io.write_standard_output_line", Text("again"));

        Assert.Equal("again\n", captured.ReadStandardOutput());
    }

    [Fact]
    public void Long_running_filesystem_operations_observe_the_interruption()
    {
        using var workspace = new SystemTestWorkspace();
        workspace.Write("tree/a/f", "x");
        using var context = NewContext();
        context.Interruption.Raise("SIGINT", 130);

        Assert.Throws<InterruptionTermination>(() => Call(context, "filesystem.walk_directory", PathOf(workspace.Root)));
        Assert.Throws<InterruptionTermination>(() => Call(context, "filesystem.remove_directory_tree", PathOf(workspace.Combine("tree"))));
        Assert.True(File.Exists(workspace.Combine("tree", "a", "f")));
    }

    [Fact]
    public void A_real_sighup_delivered_to_the_process_is_recorded_without_killing_it()
    {
        // `nohup` and a non-interactive job start the process with SIGHUP ignored; the convention keeps it ignored, so
        // there is no delivery to observe (the gate runs under nohup in some harnesses).
        Assert.SkipUnless(
            !SignalDispositions.IsIgnored(Libc.SignalHangUp),
            "SIGHUP is ignored at startup (nohup or a background job), so it is never delivered");

        var forcedExits = new List<int>();
        using var context = ContextWithRecordedForcedExit(forcedExits, TimeSpan.FromSeconds(30));
        context.Interruption.ListenForSignals();

        Assert.Equal(0, Libc.Kill(Environment.ProcessId, Libc.SignalHangUp));

        Assert.True(WaitUntil(() => context.Interruption.IsInterrupted, 5000));
        Assert.Equal("SIGHUP", context.Interruption.Pending!.SignalName);
        Assert.Equal(InterruptionMonitor.HangUpExitCode, context.Interruption.Pending.ExitCode);
        Assert.Empty(forcedExits);
    }

    [Fact]
    public void An_ignored_sighup_is_kept_ignored_instead_of_being_handled()
    {
        Assert.SkipUnless(
            SignalDispositions.IsIgnored(Libc.SignalHangUp),
            "SIGHUP is not ignored at startup, so the inherited-disposition path cannot be exercised");

        var forcedExits = new List<int>();
        using var context = ContextWithRecordedForcedExit(forcedExits, TimeSpan.FromSeconds(30));
        context.Interruption.ListenForSignals();

        Assert.Equal(0, Libc.Kill(Environment.ProcessId, Libc.SignalHangUp));

        Assert.False(WaitUntil(() => context.Interruption.IsInterrupted, 200));
        Assert.Null(context.Interruption.Pending);
        Assert.Empty(forcedExits);
    }

    [Fact]
    public void Signal_dispositions_can_be_inspected_without_being_changed()
    {
        const int SignalUser2 = 12;
        const int SignalPipe = 13;

        Assert.False(SignalDispositions.IsIgnored(SignalUser2));
        Assert.True(SignalDispositions.IsIgnored(SignalPipe));
        Assert.True(SignalDispositions.IsIgnored(SignalPipe));
    }
}
