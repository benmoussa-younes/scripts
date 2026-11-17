using System.Diagnostics;
using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

[Collection(SystemTestCollection.Name)]
public sealed class SystemSpawnProcessTests
{
    private static RecordValue Spawn(RuntimeContext context, string program, params string[] arguments) =>
        Ok<RecordValue>(Call(context, "process.spawn", Text(program), TextList(arguments)));

    [Fact]
    public void Spawn_then_wait_returns_the_buffered_outputs()
    {
        using var context = NewContext();

        var handle = Spawn(context, "/bin/sh", "-c", "echo fils; echo err >&2; exit 5");
        var outcome = Ok<RecordValue>(Call(context, "process.wait_for_process", handle, Integer(0)));

        Assert.Equal("process_handle", handle.TypeName);
        Assert.True(handle.GetInteger("process_identifier") > 0);
        Assert.Equal(5, outcome.GetInteger("exit_code"));
        Assert.Equal("fils\n", outcome.GetText("standard_output"));
        Assert.Equal("err\n", outcome.GetText("standard_error"));
        Assert.Equal("/bin/sh", outcome.GetText("program"));
    }

    [Fact]
    public void Wait_consumes_the_handle()
    {
        using var context = NewContext();
        var handle = Spawn(context, "echo", "once");

        Ok<RecordValue>(Call(context, "process.wait_for_process", handle, Integer(0)));

        AssertProcessError(Call(context, "process.wait_for_process", handle, Integer(0)), "invalid_argument", string.Empty);
        AssertProcessError(Call(context, "process.terminate", handle), "invalid_argument", string.Empty);
    }

    [Fact]
    public void Output_larger_than_the_pipe_buffer_is_kept_while_the_script_does_other_things()
    {
        using var context = NewContext();
        var handle = Spawn(context, "/bin/sh", "-c", """head -c 3000000 /dev/zero | tr '\0' x; head -c 3000000 /dev/zero | tr '\0' y >&2""");

        Thread.Sleep(300);
        var outcome = Ok<RecordValue>(Call(context, "process.wait_for_process", handle, Integer(0)));

        Assert.Equal(3_000_000, outcome.GetText("standard_output").Length);
        Assert.Equal(3_000_000, outcome.GetText("standard_error").Length);
    }

    [Fact]
    public void Kill_sends_sigterm_and_invalidates_the_handle()
    {
        using var context = NewContext();
        var handle = Spawn(context, "sleep", "30");
        var processIdentifier = (int)handle.GetInteger("process_identifier");

        OkUnit(Call(context, "process.terminate", handle));
        var second = Call(context, "process.terminate", handle);

        Assert.True(WaitUntil(() => !IsAlive(processIdentifier)));
        AssertProcessError(second, "invalid_argument");
        AssertProcessError(Call(context, "process.wait_for_process", handle, Integer(0)), "invalid_argument");
    }

    [Fact]
    public void Kill_of_a_finished_but_unwaited_child_is_ok_without_effect_and_wait_still_works()
    {
        using var context = NewContext();
        var handle = Spawn(context, "echo", "fini");
        var processIdentifier = (int)handle.GetInteger("process_identifier");
        Assert.True(WaitUntil(() => !IsAlive(processIdentifier)));
        Thread.Sleep(200);

        OkUnit(Call(context, "process.terminate", handle));
        var outcome = Ok<RecordValue>(Call(context, "process.wait_for_process", handle, Integer(0)));

        Assert.Equal("fini\n", outcome.GetText("standard_output"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(99_999_999)]
    [InlineData(long.MaxValue)]
    public void Unknown_handles_are_invalid_argument(long identifier)
    {
        using var context = NewContext();
        var handle = new RecordValue("process_handle", Integer(identifier));

        AssertProcessError(Call(context, "process.wait_for_process", handle, Integer(0)), "invalid_argument");
        AssertProcessError(Call(context, "process.terminate", handle), "invalid_argument");
    }

    [Fact]
    public void Spawn_reports_launch_failures()
    {
        using var context = NewContext();

        AssertProcessError(Call(context, "process.spawn", Text("scripts-programme-inexistant"), TextList()), "not_found", "scripts-programme-inexistant");
        AssertProcessError(Call(context, "process.spawn", Text(string.Empty), TextList()), "invalid_argument");
        AssertProcessError(Call(context, "process.spawn", Text("echo"), TextList("a\0")), "invalid_argument");
    }

    [Fact]
    public void Several_children_can_run_concurrently()
    {
        using var context = NewContext();

        var handles = Enumerable.Range(0, 8).Select(index => Spawn(context, "/bin/sh", "-c", $"sleep 0.2; echo {index}")).ToArray();
        var outputs = handles.Select(handle => Ok<RecordValue>(Call(context, "process.wait_for_process", handle, Integer(0))).GetText("standard_output")).ToArray();

        Assert.Equal(Enumerable.Range(0, 8).Select(index => index + "\n"), outputs);
    }

    [Fact]
    public void Shutdown_terminates_unwaited_children_with_sigterm()
    {
        var context = NewContext();
        var identifiers = Enumerable.Range(0, 3).Select(_ => (int)Spawn(context, "sleep", "30").GetInteger("process_identifier")).ToArray();

        context.Shutdown();

        Assert.All(identifiers, identifier => Assert.True(WaitUntil(() => !IsAlive(identifier))));
        Assert.Equal(0, context.ChildProcesses.Count);
        context.Dispose();
    }

    [Fact]
    public void Shutdown_kills_children_that_ignore_sigterm_after_two_seconds()
    {
        using var workspace = new SystemTestWorkspace();
        var marker = workspace.Combine("armed");
        var script = SystemScripts.CreateExecutable(workspace, "stubborn", $"#!/bin/sh\ntrap '' TERM\ntouch '{marker}'\nexec sleep 30\n");
        var context = NewContext();
        var identifier = (int)Spawn(context, script).GetInteger("process_identifier");
        Assert.True(WaitUntil(() => File.Exists(marker)), "the child never installed its TERM trap");
        var stopwatch = Stopwatch.StartNew();

        context.Shutdown();

        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(6));
        Assert.True(WaitUntil(() => !IsAlive(identifier)));
        context.Dispose();
    }
}
