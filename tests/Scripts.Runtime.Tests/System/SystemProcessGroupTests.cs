using System.Diagnostics;
using Scripts.Runtime.Signals;
using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>
/// A2 (major): a child that is interrupted, timed out or killed must not leave surviving descendants. Since P8d the
/// child is started through <c>posix_spawn</c> with its own process group created before <c>exec</c>, so <c>killpg</c>
/// is atomic; the /proc walk still runs first, so a descendant that called <c>setsid</c> but whose parent is alive is
/// reached too. The scripts below cover the SIGTERM-ignoring grandchild and the setsid grandchild.
/// </summary>
[Collection(SystemTestCollection.Name)]
public sealed class SystemProcessGroupTests
{
    /// <summary>A child that ignores SIGTERM and starts a grandchild (also ignoring SIGTERM) whose pid is written to a file.</summary>
    private static string StubbornTree(SystemTestWorkspace workspace, string pidFile) =>
        SystemScripts.CreateExecutable(
            workspace,
            "stubborn-tree",
            $"#!/bin/sh\ntrap '' TERM\nsh -c 'trap \"\" TERM; sleep 30' &\necho $! > '{pidFile}'\nwait\n");

    /// <summary>A child that starts a grandchild that does not ignore SIGTERM; the grandchild pid is written to a file.</summary>
    private static string TreeWithReachableGrandchild(SystemTestWorkspace workspace, string pidFile) =>
        SystemScripts.CreateExecutable(
            workspace,
            "reachable-tree",
            $"#!/bin/sh\nsleep 30 &\necho $! > '{pidFile}'\nwait\n");

    [Fact]
    public void Run_with_timeout_terminates_the_descendants_of_a_stubborn_child()
    {
        using var workspace = new SystemTestWorkspace();
        var pidFile = workspace.Combine("pid");
        var script = StubbornTree(workspace, pidFile);
        using var context = NewContext();
        var stopwatch = Stopwatch.StartNew();

        var result = RunRequest(context, script, [], timeoutMilliseconds: 300);

        AssertProcessError(result, "timed_out");
        // SIGTERM (ignored), then SIGKILL after the 2 s grace period.
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6));
        Assert.True(WaitUntil(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0));
        var grandchild = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.True(WaitUntil(() => !IsAlive(grandchild), 6000), "the grandchild survived the timeout (descendants not terminated)");
    }

    [Fact]
    public void Kill_process_terminates_the_descendants_of_the_child()
    {
        using var workspace = new SystemTestWorkspace();
        var pidFile = workspace.Combine("pid");
        var script = TreeWithReachableGrandchild(workspace, pidFile);
        using var context = NewContext();
        var handle = Ok<RecordValue>(Call(context, "process.spawn", Text(script), TextList()));
        var processIdentifier = (int)handle.GetInteger("process_identifier");
        Assert.True(WaitUntil(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0));

        OkUnit(Call(context, "process.terminate", handle));

        var grandchild = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.True(WaitUntil(() => !IsAlive(processIdentifier), 6000));
        Assert.True(WaitUntil(() => !IsAlive(grandchild), 6000), "the grandchild survived process.terminate (descendants not terminated)");
    }

    [Fact]
    public async Task An_interruption_terminates_the_descendants_of_a_stubborn_child()
    {
        using var workspace = new SystemTestWorkspace();
        var pidFile = workspace.Combine("pid");
        var script = StubbornTree(workspace, pidFile);
        using var context = new RuntimeContext(
            new Scripts.Runtime.Output.MemoryOutputChannel(),
            new Scripts.Runtime.Output.MemoryOutputChannel(),
            [],
            forceExit: _ => { },
            watchdogDelay: TimeSpan.FromSeconds(30));
        var raiser = Task.Run(() =>
        {
            WaitUntil(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0);
            Thread.Sleep(200);
            context.Interruption.Raise("SIGINT", 130);
        });

        Assert.Throws<InterruptionTermination>(() => SystemScripts.ExecResult(context, script));
        await raiser;

        var grandchild = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.True(WaitUntil(() => !IsAlive(grandchild), 6000), "the grandchild survived the interruption (descendants not terminated)");
    }

    [Fact]
    public void Shutdown_terminates_the_descendants_of_unwaited_children()
    {
        using var workspace = new SystemTestWorkspace();
        var pidFile = workspace.Combine("pid");
        var script = TreeWithReachableGrandchild(workspace, pidFile);
        var context = NewContext();
        Ok<RecordValue>(Call(context, "process.spawn", Text(script), TextList()));
        Assert.True(WaitUntil(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0));

        context.Shutdown();

        var grandchild = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.True(WaitUntil(() => !IsAlive(grandchild), 6000), "the grandchild survived the shutdown (descendants not terminated)");
        context.Dispose();
    }

    /// <summary>P8d: every captured child, including <c>run_inherit</c> under <c>--json-out</c>, gets its own group.</summary>
    [Fact]
    public void A_captured_child_is_in_its_own_process_group()
    {
        using var context = NewContext();

        var result = SystemScripts.Shell(context, "ps -o pid=,pgid= -p $$");

        var fields = result.GetText("standard_output").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, fields.Length);
        var childPid = int.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture);
        var childGroup = int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(childPid, childGroup);
    }

    [Fact]
    public void Process_run_inherit_capture_mode_keeps_the_group_of_bcs()
    {
        using var context = NewContext(out var captured);

        Ok<IntegerValue>(SystemScripts.InheritResult(context, "/bin/sh", "-c", "ps -o pid=,pgid= -p $$"));

        var fields = captured.ReadStandardOutput().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, fields.Length);
        var childGroup = int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(GetOwnProcessGroup(), childGroup);
    }

    [Fact]
    public void A_thousand_captured_runs_leave_the_descriptor_table_flat()
    {
        using var context = NewContext();
        var before = Directory.GetFiles("/proc/self/fd").Length;

        for (var round = 0; round < 1000; round++)
        {
            Assert.Equal(0, SystemScripts.Exec(context, "/bin/true").GetInteger("exit_code"));
        }

        var after = Directory.GetFiles("/proc/self/fd").Length;
        Assert.True(after - before <= 4, $"descriptors grew from {before} to {after} over 1000 captured runs");
    }

    [Fact]
    public void Run_with_timeout_terminates_a_setsid_descendant_still_in_the_chain()
    {
        using var workspace = new SystemTestWorkspace();
        var pidFile = workspace.Combine("pid");
        var script = SystemScripts.CreateExecutable(
            workspace,
            "setsid-tree",
            $"#!/bin/sh\ntrap '' TERM\nsetsid sh -c 'trap \"\" TERM; exec sleep 122' &\necho $! > '{pidFile}'\nsleep 300\n");
        using var context = NewContext();

        var result = RunRequest(context, script, [], timeoutMilliseconds: 300);

        AssertProcessError(result, "timed_out");
        Assert.True(WaitUntil(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0));
        var descendant = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.True(WaitUntil(() => !IsAlive(descendant), 6000), "the setsid descendant reachable through /proc survived the timeout");
    }

    /// <summary>DESIGN §6, normal mode: <c>process.run_inherit</c> shares scripts's terminal and therefore keeps scripts's process group.</summary>
    [Fact]
    public void Process_run_inherit_in_normal_mode_keeps_the_process_group_of_bcs()
    {
        using var workspace = new SystemTestWorkspace();
        var groupFile = workspace.Combine("pgid");
        var script = SystemScripts.CreateExecutable(
            workspace,
            "print-pgid",
            $"#!/bin/sh\nps -o pgid= -p $$ > '{groupFile}'\n");
        var standardOutput = new Scripts.Runtime.Output.MemoryOutputChannel();
        var standardError = new Scripts.Runtime.Output.MemoryOutputChannel();
        using var context = new RuntimeContext(standardOutput, standardError, []);

        Ok<IntegerValue>(SystemScripts.InheritResult(context, script));

        var childGroup = int.Parse(File.ReadAllText(groupFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(GetOwnProcessGroup(), childGroup);
    }

    [Fact]
    public void Killed_and_shutdown_children_are_reaped_not_zombies()
    {
        using var context = NewContext();
        var handles = new List<RecordValue>();
        var identifiers = new List<int>();
        for (var index = 0; index < 3; index++)
        {
            var handle = Ok<RecordValue>(Call(context, "process.spawn", Text("/bin/sleep"), TextList("30")));
            handles.Add(handle);
            identifiers.Add((int)handle.GetInteger("process_identifier"));
        }

        OkUnit(Call(context, "process.terminate", handles[0]));
        OkUnit(Call(context, "process.terminate", handles[1]));
        context.Shutdown();

        foreach (var identifier in identifiers)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
            while (DateTime.UtcNow < deadline)
            {
                if (!Directory.Exists($"/proc/{identifier}"))
                {
                    break;
                }

                Assert.False(IsZombie(identifier), $"child {identifier} is a zombie (waitpid did not reap it)");
                Thread.Sleep(20);
            }

            Assert.False(Directory.Exists($"/proc/{identifier}"), $"child {identifier} survived the termination");
        }
    }

    [Fact]
    public void Run_with_timeout_terminates_a_reparented_descendant_that_stayed_in_the_group()
    {
        using var workspace = new SystemTestWorkspace();
        var pidFile = workspace.Combine("pid");
        // The outer shell exits, so the inner shell is reparented to init but keeps the child's process group;
        // only killpg reaches it (the /proc walk from the reaped child finds nothing).
        var script = SystemScripts.CreateExecutable(
            workspace,
            "reparented-tree",
            $"#!/bin/sh\nsh -c 'trap \"\" TERM; exec sleep 122' &\necho $! > '{pidFile}'\nexit 0\n");
        using var context = NewContext();

        var result = RunRequest(context, script, [], timeoutMilliseconds: 300);

        AssertProcessError(result, "timed_out");
        Assert.True(WaitUntil(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0));
        var descendant = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.True(WaitUntil(() => !IsAlive(descendant), 6000), "the reparented descendant in the group survived the timeout");
    }

    private static bool IsZombie(int processIdentifier)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{processIdentifier}/stat");
            var close = stat.LastIndexOf(')');
            return close >= 0 && stat.Length > close + 2 && stat[close + 2] == 'Z';
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int GetOwnProcessGroup() => Scripts.Runtime.Platform.Libc.GetProcessGroup(Environment.ProcessId);
}
