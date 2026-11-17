using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Scripts.Runtime.Tests.InterpreterRuns;
using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>
/// <c>process.pipeline</c> and <c>process.pipeline_to_file_overwrite</c> (SPEC-STDLIB §7.10): one shared core,
/// raw byte streaming between stages, one result per stage in request order, a non-zero exit code as data, and a
/// destination that is either an atomic file or the last stage's stdout captured in memory.
/// </summary>
[Collection(SystemTestCollection.Name)]
public sealed class SystemPipelineTests
{
    [Fact]
    public void Raw_bytes_flow_unchanged_and_replace_the_destination_atomically()
    {
        using var workspace = new SystemTestWorkspace();
        var input = workspace.Write("input.bin", string.Empty);
        byte[] content = [0x00, 0xFF, 0x41, 0x0A];
        File.WriteAllBytes(input, content);
        var destination = workspace.Write("output.bin", "ancien");
        using var context = NewContext();

        var results = Ok<ListValue>(Call(
            context,
            "process.pipeline_to_file_overwrite",
            Requests(Request(context, "cat", input), Request(context, "cat")),
            PathOf(destination)));

        Assert.Equal(2, results.Items.Count);
        foreach (var result in results.Items)
        {
            var record = Assert.IsType<RecordValue>(result);
            Assert.Equal(0, record.GetInteger("exit_code"));
            Assert.Equal(string.Empty, record.GetText("standard_output"));
            Assert.Equal(string.Empty, record.GetText("standard_error"));
            Assert.Equal("cat", record.GetText("program"));
        }

        Assert.Equal(content, File.ReadAllBytes(destination));
        Assert.Empty(ScratchFiles(workspace));
    }

    [Fact]
    public void Pipeline_stages_do_not_leak_descriptors()
    {
        // Audit R3: each stage leaked its two captured read ends (2 fds per stage per call, EMFILE at ~60 calls).
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var before = Directory.GetFiles("/proc/self/fd").Length;
        for (var round = 0; round < 10; round++)
        {
            var destination = workspace.Combine($"round-{round}.txt");
            _ = Ok<ListValue>(Call(
                context,
                "process.pipeline_to_file_overwrite",
                Requests(Request(context, "printf", "a\n"), Request(context, "cat")),
                PathOf(destination)));
        }

        var after = Directory.GetFiles("/proc/self/fd").Length;
        Assert.True(after - before <= 4, $"descriptors grew from {before} to {after} over 10 two-stage pipelines");
    }

    [Fact]
    public void A_failed_pipeline_releases_its_descriptors()
    {
        // Runtime audit #1: the failure paths (deadline, spawn_failed, invalid_encoding) used to keep the
        // captured pipes until the GC; 20 timed-out pipelines reached EMFILE under `ulimit -n 40`.
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var before = Directory.GetFiles("/proc/self/fd").Length;
        for (var round = 0; round < 10; round++)
        {
            _ = Call(
                context,
                "process.pipeline",
                Requests(SystemCalls.Request(context, "sh", ["-c", "sleep 30"], timeoutMilliseconds: 20)));
        }

        Thread.Sleep(2000);
        var after = Directory.GetFiles("/proc/self/fd").Length;
        Assert.True(after - before <= 6, $"descriptors grew from {before} to {after} over 10 failed pipelines");
    }

    [Fact]
    public void A_failed_launch_releases_the_descriptors_of_the_stages_already_started()
    {
        // F1 audit: all stages are launched before any pump. A later launch failure used to leave the earlier
        // stages' three pipes to the finalizer; 200 such failures reach EMFILE under a modest RLIMIT_NOFILE.
        using var context = NewContext();
        var before = Directory.GetFiles("/proc/self/fd").Length;
        for (var round = 0; round < 10; round++)
        {
            var result = Call(
                context,
                "process.pipeline",
                Requests(
                    SystemCalls.Request(context, "/bin/true", []),
                    SystemCalls.Request(context, "/bin/true", []),
                    SystemCalls.Request(context, "/nonexistent-pipeline-stage", []),
                    SystemCalls.Request(context, "/bin/true", [])));
            AssertProcessError(result, "spawn_failed", "/nonexistent-pipeline-stage");
            var now = Directory.GetFiles("/proc/self/fd").Length;
            Assert.True(now - before <= 6, $"descriptors grew from {before} to {now} after round {round}");
        }

        var after = Directory.GetFiles("/proc/self/fd").Length;
        Assert.True(after - before <= 6, $"descriptors grew from {before} to {after} over 10 failed launches");
    }

    [Fact]
    public void A_failed_pipeline_releases_its_descriptors_before_a_detached_writer_exits()
    {
        // The setsid writer leaves the group and survives KillAll; the poll-based pump is stopped by Dispose, so
        // the read end is released at once instead of waiting for the writer (P8d companion lot).
        using var context = NewContext();
        var before = Directory.GetFiles("/proc/self/fd").Length;
        for (var round = 0; round < 3; round++)
        {
            _ = Call(
                context,
                "process.pipeline",
                Requests(SystemCalls.Request(
                    context,
                    "/bin/sh",
                    ["-c", "setsid sleep 5 & printf x; exit 0"],
                    timeoutMilliseconds: 20)));
        }

        Thread.Sleep(500);
        var after = Directory.GetFiles("/proc/self/fd").Length;
        Assert.True(after - before <= 6, $"descriptors grew from {before} to {after} over 3 aborted detached writers");
    }

    [Fact]
    public void A_downstream_that_stops_reading_closes_the_link_and_the_pipeline_completes()
    {
        using var context = NewContext();

        var outcome = Call(
            context,
            "process.pipeline",
            Requests(
                SystemCalls.Request(context, "/usr/bin/yes", []),
                SystemCalls.Request(context, "/usr/bin/head", ["-1"])));

        var result = Ok<RecordValue>(outcome);
        var stages = (ListValue)result.Get("stage_results");
        Assert.Equal(0, ((RecordValue)stages.Items[1]).GetInteger("exit_code"));
        Assert.Equal("y\n", result.GetText("standard_output"));
    }

    [Fact]
    public void A_failed_pipeline_releases_its_descriptors_when_a_detached_writer_exits()
    {
        // DESIGN §7.3: a detached descendant keeps the writer open after the direct child exited, so the read
        // ends live until it finishes; they must be released then, not only at the next GC.
        using var context = NewContext();
        var before = Directory.GetFiles("/proc/self/fd").Length;
        for (var round = 0; round < 3; round++)
        {
            _ = Call(
                context,
                "process.pipeline",
                Requests(SystemCalls.Request(
                    context,
                    "sh",
                    ["-c", "sleep 1 & printf x; exit 0"],
                    timeoutMilliseconds: 20)));
        }

        Thread.Sleep(2500);
        var after = Directory.GetFiles("/proc/self/fd").Length;
        Assert.True(after - before <= 6, $"descriptors grew from {before} to {after} over 3 detached writers");
    }

    [Fact]
    public void A_failing_middle_stage_is_data_and_every_stage_still_runs()
    {
        using var workspace = new SystemTestWorkspace();
        var destination = workspace.Combine("middle.txt");
        using var context = NewContext();

        var results = Ok<ListValue>(Call(
            context,
            "process.pipeline_to_file_overwrite",
            Requests(
                Request(context, "printf", "x\n"),
                Request(context, "false"),
                Request(context, "cat")),
            PathOf(destination)));

        Assert.Equal([0L, 1L, 0L], results.Items.Select(item => Assert.IsType<RecordValue>(item).GetInteger("exit_code")));
        Assert.Equal(string.Empty, File.ReadAllText(destination));
        Assert.Empty(ScratchFiles(workspace));
    }

    [Fact]
    public void The_first_stage_reads_its_standard_input_and_an_empty_one_closes_it()
    {
        using var workspace = new SystemTestWorkspace();
        var withInput = workspace.Combine("with-input.txt");
        var withoutInput = workspace.Combine("without-input.txt");
        using var context = NewContext();

        var first = Ok<ListValue>(Call(
            context,
            "process.pipeline_to_file_overwrite",
            Requests(RequestWithInput(context, "cat", "input"), Request(context, "cat")),
            PathOf(withInput)));
        var second = Ok<ListValue>(Call(
            context,
            "process.pipeline_to_file_overwrite",
            Requests(RequestWithInput(context, "cat", string.Empty), Request(context, "cat")),
            PathOf(withoutInput)));

        Assert.Equal(0, Assert.IsType<RecordValue>(first.Items[0]).GetInteger("exit_code"));
        Assert.Equal("input", File.ReadAllText(withInput));
        Assert.Equal(0, Assert.IsType<RecordValue>(second.Items[0]).GetInteger("exit_code"));
        Assert.Equal(string.Empty, File.ReadAllText(withoutInput));
    }

    [Fact]
    public void Standard_error_is_captured_per_stage()
    {
        using var workspace = new SystemTestWorkspace();
        var destination = workspace.Combine("stdout.txt");
        using var context = NewContext();

        var results = Ok<ListValue>(Call(
            context,
            "process.pipeline_to_file_overwrite",
            Requests(
                Request(context, "grep", "x", "scripts-spec-absent-file"),
                Request(context, "cat")),
            PathOf(destination)));

        var first = Assert.IsType<RecordValue>(results.Items[0]);
        Assert.Equal(2, first.GetInteger("exit_code"));
        Assert.NotEmpty(first.GetText("standard_error"));
        Assert.Equal(string.Empty, first.GetText("standard_output"));
        Assert.Equal(string.Empty, File.ReadAllText(destination));
    }

    [Fact]
    public void An_unlaunchable_stage_is_spawn_failed_and_leaves_the_destination_untouched()
    {
        using var workspace = new SystemTestWorkspace();
        var destination = workspace.Write("destination.txt", "ancien");
        using var context = NewContext();

        var result = Call(
            context,
            "process.pipeline_to_file_overwrite",
            Requests(Request(context, "scripts-spec-inexistant-program")),
            PathOf(destination));

        var failure = ErrorRecord(result, "error");
        Assert.Equal("spawn_failed", failure.GetText("kind"));
        Assert.Equal("scripts-spec-inexistant-program", failure.GetText("subject"));
        Assert.Equal("ancien", File.ReadAllText(destination));
        Assert.Empty(ScratchFiles(workspace));
    }

    [Fact]
    public void An_empty_request_list_is_invalid_argument()
    {
        using var workspace = new SystemTestWorkspace();
        var destination = workspace.Combine("empty.txt");
        using var context = NewContext();

        var result = Call(context, "process.pipeline_to_file_overwrite", new ListValue(), PathOf(destination));

        AssertIoError(result, "invalid_argument");
        Assert.False(File.Exists(destination));
        Assert.Empty(ScratchFiles(workspace));
    }

    [Fact]
    public void An_empty_program_is_invalid_argument_not_spawn_failed()
    {
        using var workspace = new SystemTestWorkspace();
        var destination = workspace.Combine("empty-program.txt");
        using var context = NewContext();

        var result = Call(
            context,
            "process.pipeline_to_file_overwrite",
            Requests(Request(context, "")),
            PathOf(destination));

        AssertIoError(result, "invalid_argument");
        Assert.False(File.Exists(destination));
        Assert.Empty(ScratchFiles(workspace));
    }

    [Fact]
    public void A_nul_argument_is_invalid_argument_and_the_subject_is_the_program_once()
    {
        using var workspace = new SystemTestWorkspace();
        var destination = workspace.Combine("nul.txt");
        using var context = NewContext();

        var result = Call(
            context,
            "process.pipeline_to_file_overwrite",
            Requests(Request(context, "printf", "a\0b")),
            PathOf(destination));

        var failure = ErrorRecord(result, "error");
        Assert.Equal("invalid_argument", failure.GetText("kind"));
        Assert.Equal("printf", failure.GetText("subject"));
        Assert.DoesNotContain("printf : printf", failure.GetText("message"));
        Assert.False(File.Exists(destination));
        Assert.Empty(ScratchFiles(workspace));
    }

    [Fact]
    public void Memory_pipeline_captures_the_last_stage_and_reports_every_stage()
    {
        using var context = NewContext();

        var pipeline = Ok<RecordValue>(Call(
            context,
            "process.pipeline",
            Requests(Request(context, "printf", "a\nb\n"), Request(context, "grep", "b"))));

        Assert.Equal("b\n", pipeline.GetText("standard_output"));
        var results = Assert.IsType<ListValue>(pipeline.Get("stage_results"));
        Assert.Equal(2, results.Items.Count);
        Assert.Equal(
            [0L, 0L],
            results.Items.Select(item => Assert.IsType<RecordValue>(item).GetInteger("exit_code")));
        Assert.Equal(
            ["printf", "grep"],
            results.Items.Select(item => Assert.IsType<RecordValue>(item).GetText("program")));
        foreach (var result in results.Items)
        {
            // The intermediate bytes were streamed, and the last stage's are the captured stdout.
            Assert.Equal(string.Empty, Assert.IsType<RecordValue>(result).GetText("standard_output"));
            Assert.Equal(string.Empty, Assert.IsType<RecordValue>(result).GetText("standard_error"));
        }
    }

    [Fact]
    public void Memory_pipeline_captures_standard_error_per_stage()
    {
        using var context = NewContext();

        var pipeline = Ok<RecordValue>(Call(
            context,
            "process.pipeline",
            Requests(
                Request(context, "grep", "x", "scripts-spec-absent-file"),
                Request(context, "cat"))));

        var first = Assert.IsType<RecordValue>(Assert.IsType<ListValue>(pipeline.Get("stage_results")).Items[0]);
        Assert.Equal(2, first.GetInteger("exit_code"));
        Assert.NotEmpty(first.GetText("standard_error"));
        Assert.Equal(string.Empty, first.GetText("standard_output"));
        Assert.Equal(string.Empty, pipeline.GetText("standard_output"));
    }

    [Fact]
    public void A_failing_stage_in_a_memory_pipeline_is_data_and_every_stage_still_runs()
    {
        using var context = NewContext();

        var pipeline = Ok<RecordValue>(Call(
            context,
            "process.pipeline",
            Requests(
                Request(context, "printf", "x\n"),
                Request(context, "false"),
                Request(context, "cat"))));

        var results = Assert.IsType<ListValue>(pipeline.Get("stage_results"));
        Assert.Equal(
            [0L, 1L, 0L],
            results.Items.Select(item => Assert.IsType<RecordValue>(item).GetInteger("exit_code")));
        Assert.Equal(string.Empty, pipeline.GetText("standard_output"));
    }

    [Fact]
    public void An_empty_memory_pipeline_is_invalid_argument()
    {
        using var context = NewContext();

        var result = Call(context, "process.pipeline", new ListValue());

        AssertIoError(result, "invalid_argument");
    }

    [Fact]
    public void Invalid_utf8_on_the_last_stage_stdout_is_invalid_encoding_with_that_program()
    {
        using var context = NewContext();

        // `printf "\377"` emits the single byte 0xFF: the early stage is never decoded, the last one is.
        var alone = Call(context, "process.pipeline", Requests(Request(context, "printf", "\\377")));
        var throughCat = Call(context, "process.pipeline", Requests(Request(context, "printf", "\\377"), Request(context, "cat")));

        AssertProcessError(alone, "invalid_encoding", "printf");
        AssertProcessError(throughCat, "invalid_encoding", "cat");
    }

    [Fact]
    public void A_memory_pipeline_stage_deadline_is_timed_out_with_that_program()
    {
        var stopwatch = Stopwatch.StartNew();
        using var context = NewContext();

        var result = Call(
            context,
            "process.pipeline",
            Requests(
                SystemCalls.Request(context, "sleep", ["30"], timeoutMilliseconds: 100),
                SystemCalls.Request(context, "sleep", ["30"])));

        stopwatch.Stop();
        AssertProcessError(result, "timed_out", "sleep");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"the stage deadline took {stopwatch.Elapsed}");
    }

    [Fact]
    public void Three_stages_carry_three_mebibytes_into_the_captured_stdout()
    {
        using var workspace = new SystemTestWorkspace();
        var content = new string('a', 3 * 1024 * 1024);
        var input = workspace.Write("large.txt", content);
        using var context = NewContext();

        var pipeline = Ok<RecordValue>(Call(
            context,
            "process.pipeline",
            Requests(Request(context, "cat", input), Request(context, "cat"), Request(context, "cat"))));

        var standardOutput = pipeline.GetText("standard_output");
        Assert.Equal(content.Length, standardOutput.Length);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(standardOutput))));
        Assert.Equal(3, Assert.IsType<ListValue>(pipeline.Get("stage_results")).Items.Count);
    }

    [Fact]
    public void Memory_pipelines_do_not_leak_descriptors()
    {
        using var context = NewContext();
        var before = Directory.GetFiles("/proc/self/fd").Length;
        for (var round = 0; round < 10; round++)
        {
            _ = Ok<RecordValue>(Call(
                context,
                "process.pipeline",
                Requests(Request(context, "printf", "a\n"), Request(context, "cat"))));
        }

        var after = Directory.GetFiles("/proc/self/fd").Length;
        Assert.True(after - before <= 4, $"descriptors grew from {before} to {after} over 10 two-stage memory pipelines");
    }

    [Fact]
    public void A_surviving_detached_writer_receives_epipe_after_an_aborted_pipeline()
    {
        using var workspace = new SystemTestWorkspace();
        var status = workspace.Combine("writer-status");
        using var context = NewContext();
        var stopwatch = Stopwatch.StartNew();

        _ = Call(
            context,
            "process.pipeline",
            Requests(SystemCalls.Request(
                context,
                "/bin/sh",
                ["-c", $"setsid sh -c 'sleep 1; (printf x); echo $? > {status}' & printf start; exit 0"],
                timeoutMilliseconds: 20)));

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"the aborted pipeline waited {stopwatch.Elapsed.TotalMilliseconds:F0} ms for a detached writer");
        Assert.True(WaitUntil(() => File.Exists(status) && new FileInfo(status).Length > 0, 6000), "the writer never recorded its status");
        Assert.Equal("141", File.ReadAllText(status).Trim());
    }

    [Fact]
    public void Starting_and_stopping_pumps_in_a_tight_loop_never_throws()
    {
        // The P8e ObjectDisposedException race: a call that finishes while its pumps start must not observe a
        // disposed stream. Any outcome is legal here; an unexpected exception fails the call.
        using var context = NewContext();
        for (var round = 0; round < 100; round++)
        {
            _ = Call(
                context,
                "process.pipeline",
                Requests(SystemCalls.Request(context, "/bin/echo", ["x"], timeoutMilliseconds: 1)));
        }
    }

    [Fact]
    public void Every_stage_uses_its_own_working_directory_and_environment()
    {
        using var workspace = new SystemTestWorkspace();
        var directory = workspace.Combine("stage-one");
        Directory.CreateDirectory(directory);

        var run = InterpreterHarness.Run($$"""
            let first = process.request("sh", ["-c", "pwd >&2; echo go"]).with_working_directory("{{directory}}".to_path())
            let second = process.request("env", []).with_environment({"SCRIPTS_STAGE_TWO": "yes"})
            match process.pipeline([first, second]) {
                ok(result) => {
                    io.write_standard_output_line(result.stage_results[0].standard_error.trim_whitespace())
                    io.write_standard_output_line("{result.standard_output.contains("SCRIPTS_STAGE_TWO=yes")}")
                }
                error(e) => io.write_standard_output_line(e.kind)
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal($"{directory}\ntrue\n", run.StandardOutput);
    }

    [Fact]
    public void An_upstream_killed_by_a_closed_link_still_has_its_standard_error_captured()
    {
        // BUG-009: the EPIPE cascade used to stop both pumps of the upstream stage. When its stderr pump had not read
        // yet, the stage's diagnostics stayed in the pipe and the result reported an empty standard_error; the full
        // suite's CPU contention made that window visible. The busy threads reproduce the same contention without a
        // sleep, and the repeated runs make the race near-certain on the old code.
        using var context = NewContext();
        var stop = 0;
        var load = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => new Thread(() =>
        {
            long spin = 0;
            while (Volatile.Read(ref stop) == 0)
            {
                spin = (spin * 31) + 7;
            }

            GC.KeepAlive(spin);
        })).ToList();
        foreach (var thread in load)
        {
            thread.Start();
        }

        try
        {
            for (var round = 0; round < 50; round++)
            {
                // `exec` replaces the shell, so nothing but the requested line can reach stderr; `true` stops reading
                // at once and `yes` never ends, so the link EPIPEs and the upstream is killed with SIGKILL.
                var pipeline = Ok<RecordValue>(Call(
                    context,
                    "process.pipeline",
                    Requests(
                        Request(context, "sh", "-c", "echo diagnostic >&2; exec yes data"),
                        Request(context, "true"))));

                var stages = Assert.IsType<ListValue>(pipeline.Get("stage_results"));
                var upstream = Assert.IsType<RecordValue>(stages.Items[0]);
                var standardError = upstream.GetText("standard_error");
                Assert.True(
                    standardError.Trim() == "diagnostic",
                    $"round {round}: the upstream standard_error was \"{standardError.Replace("\n", "\\n")}\"");
                var exitCode = upstream.GetInteger("exit_code");
                Assert.True(exitCode >= 128, $"round {round}: the upstream exit code {exitCode} is not a signal code");
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            foreach (var thread in load)
            {
                thread.Join();
            }
        }
    }

    [Fact]
    public void An_empty_program_in_a_memory_pipeline_is_invalid_argument()
    {
        using var context = NewContext();
        var result = Call(context, "process.pipeline", Requests(Request(context, "", [])));

        AssertProcessError(result, "invalid_argument", string.Empty);
    }

    [Fact]
    public void A_last_stage_that_exceeds_the_output_limit_is_fatal()
    {
        var run = InterpreterHarness.Run("""
            match process.pipeline([process.request("head", ["-c", "268435457", "/dev/zero"])]) {
                ok(_) => io.write_standard_output_line("ok")
                error(failure) => io.write_standard_output_line("error {failure.kind}")
            }
            io.write_standard_output_line("after")
            """);

        Assert.Equal(253, run.ExitCode);
        Assert.Equal("SCRIPTS-RUNTIME-0013", run.FailureCode);
        Assert.DoesNotContain("after", run.StandardOutput);
    }

    private static ListValue Requests(params RecordValue[] requests) => new([.. requests]);
    private static RecordValue Request(RuntimeContext context, string program, params string[] arguments) =>
        RequestWithInput(context, program, string.Empty, arguments);

    private static RecordValue RequestWithInput(RuntimeContext context, string program, string standardInput, params string[] arguments)
    {
        var request = Assert.IsType<RecordValue>(Call(context, "process.request", Text(program), TextList(arguments)));
        return Assert.IsType<RecordValue>(Call(context, "run_request.with_standard_input", request, Text(standardInput)));
    }

    private static string[] ScratchFiles(SystemTestWorkspace workspace) =>
        [.. Directory.EnumerateFileSystemEntries(workspace.Root, ".scs-tmp-*")];
}
