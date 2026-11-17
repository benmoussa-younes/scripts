using System.Diagnostics;
using System.Runtime.Versioning;

// The probes use POSIX process trees and Unix file modes: the whole suite targets Linux (DESIGN §11).
[assembly: SupportedOSPlatform("linux")]

namespace Scripts.EndToEnd.Tests;

/// <summary>
/// F8 on the real process (DESIGN §6): a <c>process.run_inherit</c> child is registered like every other child, so on
/// scripts termination it receives SIGTERM (then SIGKILL after 2 s) together with its still-attached descendants. The
/// probes write the descendant's pid to a file, so the test proves the descendant is gone, not merely that scripts
/// returned. Every documented boundary of DESIGN §6 is asserted too; the wording of that section was corrected from
/// these measurements (a detached descendant can still be reached, a child that exits on its own is never cleaned
/// up).
/// </summary>
[CollectionDefinition(InheritDescendantsEndToEndTests.CollectionName, DisableParallelization = true)]
public sealed class InheritDescendantsEndToEndTestsCollection
{
}

/// <summary>Serialized: the probes wait on process trees, and a loaded machine made the 8 s window flaky (release audit M8).</summary>
[Collection(InheritDescendantsEndToEndTests.CollectionName)]
public sealed class InheritDescendantsEndToEndTests
{
    public const string CollectionName = "inherit-descendants";

    /// <summary>
    /// The child run with inherited stdio starts a grandchild that ignores SIGTERM, reports its pid, then waits.
    /// When scripts ends, both must be gone within the SIGTERM+SIGKILL grace period.
    /// </summary>
    [Fact]
    public void A_run_inherit_child_and_its_attached_descendant_are_terminated_at_the_request_deadline()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var pidFile = Path.Combine(directory, "descendant.pid");
            var child = WriteStubbornChild(directory, pidFile);
            var script = WriteScriptWithEmptyMap(
                directory,
                "inherit.scs",
                $"""
                let inheritance = process.request("{child}", []).with_timeout(2000)
                let code = process.run_inherit(inheritance)?
                io.write_standard_output_line("code done")
                """);

            var pid = RunAndReadDescendantPid(script, pidFile);

            Assert.True(
                WaitUntil(() => !IsAlive(pid), TimeSpan.FromSeconds(30)),
                $"the descendant {pid} of the run_inherit child survived the termination of scripts");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Same contract under <c>--json-out</c>, where the child is captured through pipes and may get its own group.</summary>
    [Fact]
    public void A_run_inherit_child_and_its_attached_descendant_are_terminated_at_the_deadline_under_capture()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var pidFile = Path.Combine(directory, "descendant.pid");
            var child = WriteStubbornChild(directory, pidFile);
            var script = WriteScriptWithEmptyMap(
                directory,
                "inherit-capture.scs",
                $"""
                let inheritance = process.request("{child}", []).with_timeout(2000)
                let code = process.run_inherit(inheritance)?
                io.write_standard_output_line("code done")
                """);

            var pid = RunAndReadDescendantPid(script, pidFile, jsonOut: true);

            Assert.True(
                WaitUntil(() => !IsAlive(pid), TimeSpan.FromSeconds(30)),
                $"the descendant {pid} of the run_inherit child survived the termination of scripts under --json-out");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// No request deadline: scripts itself is signalled while the inherited child and its SIGTERM-ignoring grandchild
    /// are still running. The shutdown path must terminate both and exit 143 (the coverage the 30 s deadline test
    /// used to provide by accident).
    /// </summary>
    [Fact]
    public void A_run_inherit_child_and_its_attached_descendant_are_terminated_when_bcs_is_signalled()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var pidFile = Path.Combine(directory, "descendant.pid");
            var child = WriteStubbornChild(directory, pidFile);
            var script = WriteScriptWithEmptyMap(
                directory,
                "inherit-signalled.scs",
                $"""
                let inheritance = process.request("{child}", [])
                let code = process.run_inherit(inheritance)?
                io.write_standard_output_line("code done")
                """);
            var startInfo = new ProcessStartInfo(ScriptsProcess.Binary)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add(script);
            using var run = Process.Start(startInfo)!;

            Assert.True(WaitUntil(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0, TimeSpan.FromSeconds(10)), "the child never started");
            var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(AssertSignal(run.Id), "SIGTERM could not be delivered to scripts");
            Assert.True(run.WaitForExit(10_000), "scripts did not end within the SIGTERM + SIGKILL grace period");
            run.StandardOutput.ReadToEnd();
            run.StandardError.ReadToEnd();

            Assert.Equal(143, run.ExitCode);
            Assert.True(WaitUntil(() => !IsAlive(pid), TimeSpan.FromSeconds(6)), $"the descendant {pid} survived the signal to scripts");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Measured boundary (DESIGN §6): a deadline makes scripts terminate the child and the descendants it can reach at
    /// that moment, including a detached one whose parent is still alive and in the chain.
    /// </summary>
    [Fact]
    public void A_deadline_terminates_the_descendants_still_reachable_at_that_moment()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var pidFile = Path.Combine(directory, "detached.pid");
            var child = WriteExecutable(
                directory,
                "detaching-child",
                $"#!/bin/sh\ntrap '' TERM\nsetsid sh -c 'trap \"\" INT TERM HUP; exec sleep 122' &\nsleep 1\necho $! > '{pidFile}'\nsleep 300\n");
            var script = WriteScriptWithEmptyMap(
                directory,
                "deadline.scs",
                $"""
                let inheritance = process.request("{child}", []).with_timeout(3000)
                let code = process.run_inherit(inheritance)?
                io.write_standard_output_line("code done")
                """);

            var pid = RunAndReadDescendantPid(script, pidFile);

            Assert.True(
                WaitUntil(() => !IsAlive(pid), TimeSpan.FromSeconds(30)),
                $"the descendant {pid} reachable at the deadline survived the termination of scripts");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Writes the probe script, replacing <c>EMPTY</c> with the environment sentinel of DESIGN §7.3 (<c>{}</c>, the
    /// empty map, meaning "inherit the environment"). The placeholder keeps the C# raw string free of brace escapes,
    /// which the compiler rejects inside an interpolated literal.
    /// </summary>
    private static string WriteScriptWithEmptyMap(string directory, string name, string template) =>
        ScriptsProcess.WriteScript(directory, name, template.Replace("EMPTY", "{ }", StringComparison.Ordinal));

    /// <summary>
    /// Runs the script, waits for the descendant pid file, and returns the pid once scripts has exited. The child keeps
    /// running until the deadline, so scripts can only end through its own timeout, which is what exercises termination.
    /// </summary>
    private static int RunAndReadDescendantPid(string script, string pidFile, bool jsonOut = false)
    {
        var arguments = jsonOut ? new[] { "run", "--json-out", script } : ["run", script];
        var start = Stopwatch.StartNew();
        var run = ScriptsProcess.Run(arguments);
        start.Stop();

        Assert.True(File.Exists(pidFile), $"the child did not report its descendant (stderr: {run.StandardError})");
        var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        // A normal end exits 0; a deadline reaches the top level as an unhandled error (253). Both are expected here.
        Assert.True(run.ExitCode is 0 or 253, $"unexpected exit {run.ExitCode}: {run.StandardError}");

        return pid;
    }

    /// <summary>A child that starts a SIGTERM-ignoring grandchild, writes its pid, and waits for it.</summary>
    private static string WriteStubbornChild(string directory, string pidFile) =>
        WriteExecutable(
            directory,
            "stubborn-child",
            $"#!/bin/sh\ntrap '' TERM\nsh -c 'trap \"\" TERM; sleep 30' &\necho $! > '{pidFile}'\nwait\n");

    /// <summary>Writes a probe and makes it executable: scripts resolves a path with <c>/</c> as is and needs the mode bits.</summary>
    private static string WriteExecutable(string directory, string name, string contents)
    {
        var path = ScriptsProcess.WriteScript(directory, name, contents);
        File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("755", 8));
        return path;
    }

    private static bool IsAlive(int processIdentifier)
    {
        try
        {
            using var process = Process.GetProcessById(processIdentifier);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool AssertSignal(int processIdentifier)
    {
        using var signal = Process.Start(new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            ArgumentList = { "-c", "kill -TERM \"$0\"", processIdentifier.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        })!;
        signal.WaitForExit();
        return signal.ExitCode == 0;
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return condition();
    }

}
