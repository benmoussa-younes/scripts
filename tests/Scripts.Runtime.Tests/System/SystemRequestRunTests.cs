using System.Diagnostics;
using Scripts.Runtime.Tests.InterpreterRuns;

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>
/// <c>process.request</c> / <c>process.run</c> (DESIGN §7.3): each request runs in its own working directory and
/// environment overlay, resolves a bare program against the effective PATH and honours its own deadline.
/// </summary>
[Collection(SystemTestCollection.Name)]
public sealed class SystemRequestRunTests
{
    private static string Output(string source) => InterpreterHarness.Output(source);

    [Fact]
    public void Run_waits_for_eof_so_a_descendant_written_after_the_child_is_captured()
    {
        // Audit R1: a bounded post-exit drain dropped this late line, and could cut a valid UTF-8 sequence in two.
        var output = Output(
            """
            let result = process.run(process.request("sh", ["-c", "echo early; (sleep 0.4; echo late) &"]))?
            io.write_standard_output_line("stdout=<<{result.standard_output.trim_whitespace()}>>")
            """);

        Assert.Equal("stdout=<<early\nlate>>\n", output);
    }

    [Fact]
    public void Run_uses_the_absolute_and_relative_working_directory()
    {
        using var workingDirectory = new TemporaryWorkingDirectory();
        Directory.CreateDirectory(Path.Combine(workingDirectory.Path, "sub"));

        var output = Output(
            """
            let here = filesystem.get_current_directory()?
            let absolute = process.run(process.request("pwd", []).with_working_directory(here))?
            io.write_standard_output_line(absolute.standard_output.trim_whitespace())
            let relative = process.run(process.request("pwd", []).with_working_directory(p"sub"))?
            io.write_standard_output_line(relative.standard_output.trim_whitespace())
            """);

        Assert.Equal(
            workingDirectory.Path + "\n" + Path.Combine(workingDirectory.Path, "sub") + "\n",
            output);
    }

    [Fact]
    public void Run_applies_a_non_empty_environment_overlay_and_the_empty_map_inherits()
    {
        var output = Output(
            """
            let overlaid = process.run(process.request(
                "sh", ["-c", "printf %s \"$SCRIPTS_REQUEST_OVERLAY\""]).with_environment({"SCRIPTS_REQUEST_OVERLAY": "yes"}))?
            io.write_standard_output_line("{overlaid.standard_output}|{overlaid.program}")
            let inherited = process.run(process.request("sh", ["-c", "printf %s inherited"]))?
            io.write_standard_output_line(inherited.standard_output)
            """);

        Assert.Equal("yes|sh\ninherited\n", output);
    }

    [Fact]
    public void Run_searches_a_bare_program_in_the_overlay_path_not_the_process_path()
    {
        using var workingDirectory = new TemporaryWorkingDirectory();
        var tool = Path.Combine(workingDirectory.Path, "scripts-overlay-tool");
        File.WriteAllText(tool, "#!/bin/sh\nprintf from-overlay\n");
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var output = Output(
            $$"""
            let found = process.run(process.request("scripts-overlay-tool", []).with_environment({"PATH": "{{workingDirectory.Path}}"}))?
            io.write_standard_output_line(found.standard_output.trim_whitespace())
            match process.run(process.request("sh", []).with_environment({"PATH": "{{workingDirectory.Path}}"})) {
                ok(_) => io.write_standard_output_line("unexpected ok")
                error(e) => io.write_standard_output_line(e.kind)
            }
            """);

        Assert.Equal("from-overlay\nnot_found\n", output);
    }

    [Fact]
    public void Run_terminates_a_request_whose_deadline_expires()
    {
        var stopwatch = Stopwatch.StartNew();
        var output = Output(
            """
            match process.run(process.request("sleep", ["5"]).with_timeout(100)) {
                ok(_) => io.write_standard_output_line("ok")
                error(e) => io.write_standard_output_line("{e.kind} {e.subject}")
            }
            """);
        stopwatch.Stop();

        Assert.Equal("timed_out sleep\n", output);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"the deadline took {stopwatch.Elapsed}");
    }

    [Fact]
    public void Run_rejects_a_negative_deadline()
    {
        var output = Output(
            """
            match process.run(process.request("true", []).with_timeout(-1)) {
                ok(_) => io.write_standard_output_line("ok")
                error(e) => io.write_standard_output_line("{e.kind} {e.subject}")
            }
            """);

        Assert.Equal("invalid_argument true\n", output);
    }

    [Theory]
    [InlineData("\"\"", "\"x\"")]
    [InlineData("\"A=B\"", "\"x\"")]
    [InlineData("\"A\"", "\"x\\0y\"")]
    public void Run_rejects_a_malformed_environment_entry(string key, string value)
    {
        var output = Output(
            $$"""
            match process.run(process.request("true", []).with_environment({ {{key}}: {{value}} })) {
                ok(_) => io.write_standard_output_line("ok")
                error(e) => io.write_standard_output_line("{e.kind} {e.subject}")
            }
            """);

        Assert.Equal("invalid_argument true\n", output);
    }

    [Fact]
    public void Run_reports_a_missing_or_non_directory_working_directory()
    {
        using var workingDirectory = new TemporaryWorkingDirectory();
        File.WriteAllText(Path.Combine(workingDirectory.Path, "not-a-directory"), "x");

        var output = Output(
            """
            match process.run(process.request("true", []).with_working_directory(p"absent")) {
                ok(_) => io.write_standard_output_line("unexpected ok")
                error(e) => io.write_standard_output_line("{e.kind}|{e.message}")
            }
            match process.run(process.request("true", []).with_working_directory(p"not-a-directory")) {
                ok(_) => io.write_standard_output_line("unexpected ok")
                error(e) => io.write_standard_output_line("{e.kind}|{e.message}")
            }
            """);

        Assert.Equal(
            "not_found|working directory not found\nnot_a_directory|working directory is not a directory\n",
            output);
    }

    [Fact]
    public void Run_names_the_working_directory_when_it_is_not_accessible()
    {
        using var workspace = new SystemTestWorkspace();
        var locked = workspace.Combine("locked");
        Directory.CreateDirectory(locked);
        var inaccessible = Path.Combine(locked, "sub");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        var output = Output(
            $$"""
            match process.run(process.request("true", []).with_working_directory("{{inaccessible}}".to_path())) {
                ok(_) => io.write_standard_output_line("unexpected ok")
                error(e) => io.write_standard_output_line("{e.kind}|{e.message}")
            }
            """);

        Assert.Equal("permission_denied|working directory not accessible\n", output);
    }

    [Fact]
    public void Run_resolves_a_program_containing_a_slash_relative_to_the_working_directory()
    {
        using var workingDirectory = new TemporaryWorkingDirectory();
        Directory.CreateDirectory(Path.Combine(workingDirectory.Path, "sub"));
        var tool = Path.Combine(workingDirectory.Path, "sub", "relative-tool");
        File.WriteAllText(tool, "#!/bin/sh\nprintf relative\n");
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var output = Output(
            """
            let from_process_directory = process.run(process.request("./sub/relative-tool", []))?
            io.write_standard_output_line(from_process_directory.standard_output.trim_whitespace())
            let from_request_directory = process.run(process.request("./relative-tool", []).with_working_directory(p"sub"))?
            io.write_standard_output_line(from_request_directory.standard_output.trim_whitespace())
            """);

        Assert.Equal("relative\nrelative\n", output);
    }

    [Fact]
    public void Pipeline_uses_each_stage_working_directory_and_environment()
    {
        using var workingDirectory = new TemporaryWorkingDirectory();
        Directory.CreateDirectory(Path.Combine(workingDirectory.Path, "sub"));

        var output = Output(
            """
            let here = filesystem.get_current_directory()?
            let destination = here / "pipeline-context.txt"
            let stages = [
                process.request("pwd", []).with_working_directory(p"sub"),
                process.request("sh", ["-c", "cat; printf \":%s\" \"$SCRIPTS_STAGE_TWO\""]).with_environment({"SCRIPTS_STAGE_TWO": "two"}),
            ]
            let results = process.pipeline_to_file_overwrite(stages, destination)?
            io.write_standard_output_line("{results[0].standard_output.length()}")
            io.write_standard_output_line(filesystem.read_file(destination)?)
            """);

        Assert.Equal(
            "0\n" + Path.Combine(workingDirectory.Path, "sub") + "\n:two\n",
            output);
    }

    [Fact]
    /// <summary>
    /// The deadline is on the FIRST stage: whichever way the launch order falls, the pipeline is
    /// killed before any output reaches the destination. The destination is removed first: the run's
    /// working directory is the test output directory, and a file left by an earlier run made this
    /// assertion fail on a machine that had run it once (it passed on a clean checkout).
    /// </summary>
    public void Pipeline_stage_deadline_kills_the_pipeline_and_leaves_the_destination_untouched()
    {
        File.Delete(Path.Combine(Directory.GetCurrentDirectory(), "timed-pipeline.txt"));
        var stopwatch = Stopwatch.StartNew();
        var output = Output(
            """
            let here = filesystem.get_current_directory()?
            let destination = here / "timed-pipeline.txt"
            let stages = [
                process.request("sleep", ["30"]).with_timeout(100),
                process.request("sleep", ["30"]),
            ]
            match process.pipeline_to_file_overwrite(stages, destination) {
                ok(_) => io.write_standard_output_line("ok")
                error(e) => io.write_standard_output_line("{e.kind} {e.subject} {filesystem.path_exists(destination)}")
            }
            """);
        stopwatch.Stop();

        Assert.Equal("timed_out sleep false\n", output);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"the stage deadline took {stopwatch.Elapsed}");
    }

    [Fact]
    public void Exec_all_uses_each_request_working_directory_and_environment()
    {
        using var workingDirectory = new TemporaryWorkingDirectory();
        Directory.CreateDirectory(Path.Combine(workingDirectory.Path, "sub"));

        var output = Output(
            """
            let requests = [
                process.request("pwd", []).with_working_directory(p"sub"),
                process.request("sh", ["-c", "printf %s \"$SCRIPTS_EXEC_ALL\""]).with_environment({"SCRIPTS_EXEC_ALL": "overlay"}),
            ]
            let results = parallel.run_all(requests, 4)?
            for result in results {
                io.write_standard_output_line(result.standard_output.trim_whitespace())
            }
            """);

        Assert.Equal(Path.Combine(workingDirectory.Path, "sub") + "\noverlay\n", output);
    }

    [Fact]
    public void Exec_all_deadline_interrupts_the_call_and_returns_timed_out()
    {
        var stopwatch = Stopwatch.StartNew();
        var output = Output(
            """
            let requests = [
                process.request("sleep", ["30"]),
                process.request("sleep", ["30"]).with_timeout(100),
            ]
            match parallel.run_all(requests, 4) {
                ok(_) => io.write_standard_output_line("ok")
                error(e) => io.write_standard_output_line("{e.kind} {e.subject}")
            }
            """);
        stopwatch.Stop();

        Assert.Equal("timed_out sleep\n", output);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"the request deadline took {stopwatch.Elapsed}");
    }

    /// <summary>
    /// BLOCKER-1 regression: the direct child exits immediately but a background descendant keeps stdout open, so
    /// the pump does not see EOF. The stage deadline must cover the drain, not only the direct process exit.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Pipeline_deadline_covers_the_pump_drain_after_the_direct_child_exits()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workingDirectory = new TemporaryWorkingDirectory();
        var destination = Path.Combine(workingDirectory.Path, "pump-drain.txt");
        var stopwatch = Stopwatch.StartNew();
        var output = await Task.Run(() => Output(
            """
            let here = filesystem.get_current_directory()?
            let destination = here / "pump-drain.txt"
            match process.pipeline_to_file_overwrite([
                process.request("sh", ["-c", "sleep 30 & exit 0"]).with_timeout(100),
            ], destination) {
                ok(_) => io.write_standard_output_line("ok")
                error(e) => io.write_standard_output_line("{e.kind} {e.subject}")
            }
            """));
        stopwatch.Stop();

        Assert.Equal("timed_out sh\n", output);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"the pump-drain deadline took {stopwatch.Elapsed}");
        Assert.False(File.Exists(destination));
    }

    /// <summary>
    /// MAJOR-1 regression: a downstream stage that stops reading must not leave an endless upstream writer behind.
    /// The upstream stage is killed, so both stages complete quickly and the bound exit code is data (137 or 141).
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Pipeline_downstream_that_stops_reading_kills_the_upstream_stage()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workingDirectory = new TemporaryWorkingDirectory();
        var destination = Path.Combine(workingDirectory.Path, "bounded.txt");
        var stopwatch = Stopwatch.StartNew();
        var output = await Task.Run(() => Output(
            """
            let here = filesystem.get_current_directory()?
            let destination = here / "bounded.txt"
            let results = process.pipeline_to_file_overwrite([
                process.request("cat", ["/dev/zero"]),
                process.request("head", ["-c", "5"]),
            ], destination)?
            io.write_standard_output_line("{results[0].exit_code},{results[1].exit_code}")
            io.write_standard_output_line("{filesystem.read_file(destination)?.length()}")
            """));
        stopwatch.Stop();

        var lines = output.Split('\n');
        var codes = lines[0].Split(',');
        Assert.Equal("0", codes[1]);
        Assert.True(codes[0] is "137" or "141", $"the upstream exit code was {codes[0]}");
        Assert.Equal("5", lines[1]);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"the bounded pipeline took {stopwatch.Elapsed}");
        Assert.Equal(5, new FileInfo(destination).Length);
    }

    /// <summary>
    /// MAJOR (second re-audit): the direct upstream child exits while a detached descendant keeps writing. Killing
    /// the already-exited child is a no-op, so the pump itself must stop reading and close the pipe; otherwise the
    /// pipeline drains the detached writer forever.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Pipeline_downstream_that_stops_reading_stops_a_detached_writer()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var workingDirectory = new TemporaryWorkingDirectory();
        var destination = Path.Combine(workingDirectory.Path, "detached-writer.txt");
        var stopwatch = Stopwatch.StartNew();
        var output = await Task.Run(() => Output(
            """
            let here = filesystem.get_current_directory()?
            let destination = here / "detached-writer.txt"
            let results = process.pipeline_to_file_overwrite([
                process.request("sh", ["-c", "cat /dev/zero & exit 0"]),
                process.request("head", ["-c", "5"]),
            ], destination)?
            io.write_standard_output_line("{results[0].exit_code},{results[1].exit_code}")
            io.write_standard_output_line("{filesystem.read_file(destination)?.length()}")
            io.write_standard_output_line("after")
            """));
        stopwatch.Stop();

        var lines = output.Split('\n');
        var codes = lines[0].Split(',');
        Assert.Equal("0", codes[1]);
        Assert.Equal("5", lines[1]);
        Assert.Equal("after", lines[2]);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"the detached-writer pipeline took {stopwatch.Elapsed}");
        Assert.Equal(5, new FileInfo(destination).Length);
    }

    /// <summary>
    /// BLOCKER-2 regression: the 2^28 output limit is fatal (SCRIPTS-RUNTIME-0013) in <c>parallel.run_all</c> for
    /// every worker count, never mapped to <c>spawn_failed</c> on the parallel path.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Exec_all_output_limit_is_fatal_for_every_worker_count(int workers)
    {
        var run = InterpreterHarness.Run(
            $$"""
            let requests = [
                process.request("head", ["-c", "268435457", "/dev/zero"]),
                process.request("true", []),
            ]
            match parallel.run_all(requests, {{workers}}) {
                ok(_) => io.write_standard_output_line("ok")
                error(e) => io.write_standard_output_line("error {e.kind}")
            }
            io.write_standard_output_line("after")
            """);

        Assert.Equal(253, run.ExitCode);
        Assert.Equal("SCRIPTS-RUNTIME-0013", run.FailureCode);
        Assert.DoesNotContain("after", run.StandardOutput);
    }

    /// <summary>
    /// BLOCKER-3 regression: a deadline beyond <see cref="int.MaxValue"/> milliseconds must not overflow the wait
    /// chunk, and a value beyond <see cref="TimeSpan.MaxValue"/> means no deadline, like <c>process.run</c>.
    /// </summary>
    [Theory]
    [InlineData(2147483651L)]
    [InlineData(2592000000L)]
    [InlineData(9223372036854775807L)]
    public void Pipeline_large_deadline_does_not_overflow(long timeoutMilliseconds)
    {
        using var workingDirectory = new TemporaryWorkingDirectory();
        var output = Output(
            $$"""
            let here = filesystem.get_current_directory()?
            let destination = here / "large-deadline.txt"
            let results = process.pipeline_to_file_overwrite([
                process.request("sleep", ["0.05"]).with_timeout({{timeoutMilliseconds}}),
            ], destination)?
            io.write_standard_output_line("{results.length()}")
            """);

        Assert.Equal("1\n", output);
    }
}
