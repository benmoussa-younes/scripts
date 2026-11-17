using System.Diagnostics;
using Scripts.Runtime.Parallel;
using Scripts.Runtime.Tests.InterpreterRuns;

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>
/// <c>parallel</c> (DESIGN §7.10.1): bounded, synchronous, deterministic concurrency. Order, the worker
/// bound, the single-error abort and the group-kill are all observable from a real script.
/// </summary>
[Collection(SystemTestCollection.Name)]
public sealed class ParallelFunctionsTests
{
    private static string Output(string source) => InterpreterHarness.Output(source);

    [Fact]
    public void Map_returns_results_in_input_order_and_workers_one_is_identical()
    {
        var output = Output(
            """
            let par = parallel.transform([1, 2, 3, 4, 5, 6], 8, n => n * n)?
            let seq = parallel.transform([1, 2, 3, 4, 5, 6], 1, n => n * n)?
            io.write_standard_output_line(par.transform(n => "{n}").join(","))
            io.write_standard_output_line("{par == seq}")
            """);

        Assert.Equal("1,4,9,16,25,36\ntrue\n", output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    [InlineData(-1)]
    public void Map_rejects_an_out_of_range_worker_count(long workers)
    {
        var output = Output(
            $$"""
            match parallel.transform([1], {{workers}}, n => n) {
                ok(values) => io.write_standard_output_line("ok")
                error(e) => io.write_standard_output_line("{e.kind}")
            }
            """);

        Assert.Equal("invalid_worker_count\n", output);
    }

    [Fact]
    public void An_assertion_in_a_worker_is_a_transform_failure_that_keeps_its_message()
    {
        // SPEC-STDLIB: parallel.transform never returns a fatal SCRIPTS-RUNTIME, so a false assert inside the worker is
        // its transform_failed error — but the human message must survive, not just the code.
        var output = Output(
            """
            function check(n: integer) -> integer {
                assert(n != 2, "index 2 is forbidden")
                return n
            }
            match parallel.transform([1, 2, 3], 2, check) {
                ok(values) => io.write_standard_output_line("ok")
                error(e) => io.write_standard_output_line("{e.kind}|{e.message}")
            }
            """);

        Assert.Equal("transform_failed|assertion failed: index 2 is forbidden\n", output);
    }

    [Fact]
    public void Map_reports_one_transform_failure_and_no_partial_list()
    {
        var output = Output(
            """
            function risky(n: integer) -> integer {
                if n == 3 {
                    let values = [1]
                    return values[9]
                }
                return n
            }
            match parallel.transform([1, 2, 3, 4, 5], 4, risky) {
                ok(values) => io.write_standard_output_line("ok {values.length()}")
                error(e) => io.write_standard_output_line("{e.kind}")
            }
            """);

        Assert.Equal("transform_failed\n", output);
    }

    /// <summary>
    /// MAJOR-2 regression: the pool workers run on dedicated threads whose stack must still reach the
    /// documented call depth (DESIGN §5.8: ≤10000). The stack was cut from 512 MiB to 64 MiB per worker: a
    /// recursion near the limit must still hit the call-depth guard and yield the clean <c>transform_failed</c>
    /// (<c>SCRIPTS-RUNTIME-0011</c>), never a native stack overflow. Run on the large-stack harness thread because
    /// the fold itself recurses the same way.
    /// </summary>
    [Fact]
    public void Map_deep_recursion_in_a_worker_hits_the_call_depth_limit_cleanly()
    {
        var run = InterpreterHarness.RunOnLargeStack(
            """
            function recurse(n: integer) -> integer {
                if n == 0 {
                    return 0
                }
                return recurse(n - 1) + 1
            }
            let ok = parallel.transform([1, 2, 3, 4], 4, n => recurse(9500))?
            io.write_standard_output_line("{ok.length()}")
            match parallel.transform([1, 2, 3, 4], 4, n => recurse(50000)) {
                ok(values) => io.write_standard_output_line("ok {values.length()}")
                error(e) => io.write_standard_output_line("{e.kind}")
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("4\ntransform_failed\n", run.StandardOutput);
    }

    [Fact]
    public void Exec_all_keeps_input_order_and_a_nonzero_exit_is_a_value()
    {
        var output = Output(
            """
            let requests = [
                process.request("sh", ["-c", "sleep 0.2; echo first"]),
                process.request("sh", ["-c", "echo second"]),
                process.request("false", []),
            ]
            for result in parallel.run_all(requests, 4)? {
                io.write_standard_output_line("{result.exit_code} {result.program} [{result.standard_output.trim_whitespace()}]")
            }
            """);

        Assert.Equal("0 sh [first]\n0 sh [second]\n1 false []\n", output);
    }

    [Fact]
    public void Exec_all_reports_spawn_failed_and_no_partial_list()
    {
        var output = Output(
            """
            let requests = [
                process.request("true", []),
                process.request("no-such-program-scripts-test", []),
            ]
            match parallel.run_all(requests, 4) {
                ok(results) => io.write_standard_output_line("ok {results.length()}")
                error(e) => io.write_standard_output_line("{e.kind}")
            }
            """);

        Assert.Equal("spawn_failed\n", output);
    }

    [Fact]
    public void Exec_all_abort_kills_in_flight_children_instead_of_waiting()
    {
        // Twelve 30-second children plus a failing first spawn: a naive implementation waits 30 s per wave.
        // The abort must kill the live children from inside the failing worker, so the call returns at once.
        var stopwatch = Stopwatch.StartNew();
        var output = Output(
            """
            let mutable sleepers: list<run_request> = []
            let mutable index = 0
            while index < 12 {
                sleepers.append(process.request("sleep", ["30"]))
                index += 1
            }
            let requests = [process.request("no-such-program-scripts-test", [])] + sleepers
            match parallel.run_all(requests, 8) {
                ok(results) => io.write_standard_output_line("ok")
                error(e) => io.write_standard_output_line("{e.kind}")
            }
            """);
        stopwatch.Stop();

        Assert.Equal("spawn_failed\n", output);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"abort took {stopwatch.Elapsed}");
    }

    /// <summary>
    /// BLOCKER-2 regression: the failing request was dispatched inside a 16-item block, so a late failure was
    /// only reached after every earlier item of the block had <em>finished</em>: the abort waited for a
    /// <c>sleep 5</c> instead of killing it at once. Per-item dispatch reaches the failure immediately whatever
    /// its index, so both orders return well under the sleep.
    /// </summary>
    [Theory]
    [InlineData("last")]
    [InlineData("first")]
    public void Exec_all_aborts_promptly_wherever_the_failing_request_sits(string position)
    {
        var requests = position == "last"
            ? "[process.request(\"sleep\", [\"5\"]), process.request(\"no-such-program-scripts-test\", [])]"
            : "[process.request(\"no-such-program-scripts-test\", []), process.request(\"sleep\", [\"5\"])]";
        var stopwatch = Stopwatch.StartNew();
        var output = Output(
            $$"""
            match parallel.run_all({{requests}}, 4) {
                ok(results) => io.write_standard_output_line("ok {results.length()}")
                error(e) => io.write_standard_output_line("{e.kind}")
            }
            """);
        stopwatch.Stop();

        Assert.Equal("spawn_failed\n", output);
        // The guard is generous (the sleep is 5 s): the fix must return roughly at once, not after the child.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"{position} failure took {stopwatch.Elapsed}");
    }

    /// <summary>
    /// BLOCKER-2 regression, 15 sleepers + a failing spawn with the maximum bound. With the old 16-item block a
    /// single worker owned a whole block and a failure had to wait for the entire <c>sleep 5</c> wave; per-item
    /// claim reaches a failure as soon as a worker picks it up, and the call then returns at once (the abort kills
    /// the live children) instead of draining every wave.
    /// </summary>
    /// <remarks>
    /// Two cases, because the load ceiling changes what is reachable without lying about the contract.
    /// <list type="bullet">
    /// <item>failure at the <b>head</b>: the worker that claims it first aborts immediately — that is the strict
    /// promptness guard (&lt; 2 s), the BLOCKER-2 invariant;</item>
    /// <item>failure at the <b>end</b>: since <c>parallel</c> shares the <c>Core.Concurrency</c> context, the
    /// ceiling (logical cores for I/O) bounds the number of workers, so a failure placed after fifteen
    /// blocking items can only be claimed after a <c>sleep 5</c> wave. What remains guaranteed and guarded
    /// here: the failure does not wait for the <i>three</i> waves of a single block — it falls back to at most two waves
    /// (~10 s), which a block of 16 items per worker would not do.</item>
    /// </list>
    /// </remarks>
    [Fact]
    public void Exec_all_aborts_promptly_with_fifteen_sleepers_and_the_maximum_bound()
    {
        // Failure FIRST: the strict BLOCKER-2 promptness (the failing item is claimed at once).
        var firstStopwatch = Stopwatch.StartNew();
        var firstOutput = Output(
            """
            let mutable sleepers: list<run_request> = []
            let mutable index = 0
            while index < 15 {
                sleepers.append(process.request("sleep", ["5"]))
                index += 1
            }
            match parallel.run_all([process.request("no-such-program-scripts-test", [])] + sleepers, 256) {
                ok(results) => io.write_standard_output_line("ok {results.length()}")
                error(e) => io.write_standard_output_line("{e.kind}")
            }
            """);
        firstStopwatch.Stop();

        Assert.Equal("spawn_failed\n", firstOutput);
        Assert.True(firstStopwatch.Elapsed < TimeSpan.FromSeconds(2), $"the first-item failure took {firstStopwatch.Elapsed}");

        // Failure LAST: bounded by the ceiling (one wave), not the multi-wave drain a whole-block worker would pay.
        var lastStopwatch = Stopwatch.StartNew();
        var lastOutput = Output(
            """
            let mutable sleepers: list<run_request> = []
            let mutable index = 0
            while index < 15 {
                sleepers.append(process.request("sleep", ["5"]))
                index += 1
            }
            match parallel.run_all(sleepers + [process.request("no-such-program-scripts-test", [])], 256) {
                ok(results) => io.write_standard_output_line("ok {results.length()}")
                error(e) => io.write_standard_output_line("{e.kind}")
            }
            """);
        lastStopwatch.Stop();

        Assert.Equal("spawn_failed\n", lastOutput);

        // 15 items of 5 s at the I/O ceiling (logical cores ≥ 8 here) fit in at most two waves; three waves
        // (~15 s) would mean a worker owned a whole block, the regression this guards. The bound is generous.
        Assert.True(lastStopwatch.Elapsed < TimeSpan.FromSeconds(11), $"the last-item failure took {lastStopwatch.Elapsed}");
    }

    /// <summary>
    /// Worker-reuse regression (worker pool): the pool threads are reused across calls, where a per-call thread
    /// used to be created fresh. The interpreter keeps its per-thread state (call depth, current environment,
    /// return slot) thread-affine; a pooled worker that ran a deeply recursing <c>transform</c> must not carry
    /// that call depth into the next call. Here the first call fails deep in a recursion (leaving a non-zero
    /// depth), and the second recurses near the documented limit: without the per-call reset the second call is
    /// reported as a <c>transform_failed</c>, with it, it succeeds. Run on the large-stack harness thread
    /// because the fold itself recurses the same way.
    /// </summary>
    [Fact]
    public void Map_reusing_a_worker_resets_its_call_depth_between_calls()
    {
        var run = InterpreterHarness.RunOnLargeStack(
            """
            function deep(n: integer) -> integer {
                if n == 0 {
                    return 0
                }
                return deep(n - 1) + 1
            }
            match parallel.transform([1], 4, n => deep(20000)) {
                ok(values) => io.write_standard_output_line("unexpected ok")
                error(e) => io.write_standard_output_line("{e.kind}")
            }
            let after = parallel.transform([1, 2, 3, 4], 4, n => deep(5000))?
            io.write_standard_output_line("{after.length()}")
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("transform_failed\n4\n", run.StandardOutput);
    }

    /// <summary>
    /// Worker-reuse regression (worker pool): many small calls in a row must stay in input order and keep
    /// failing on the failing item, whatever pooled worker each index lands on. The pool grows only to the
    /// largest bound requested, so later calls run on warm threads; a wrong index mapping or a stale per-call
    /// state would show up as a mismatch here.
    /// </summary>
    [Fact]
    public void Map_many_small_calls_stay_in_order_and_report_each_failure()
    {
        var output = Output(
            """
            function risky(n: integer) -> integer {
                let values = [1]
                return values[9]
            }
            let mutable round = 1
            let mutable mismatches = 0
            while round <= 500 {
                let workers = 2 + (round % 4) * 2
                let values = parallel.transform([1, 2, 3, 4], workers, n => n * round)?
                if values != [round, round * 2, round * 3, round * 4] {
                    mismatches += 1
                }
                round += 1
            }
            match parallel.transform([1, 2, 3, 4], 8, risky) {
                ok(values) => io.write_standard_output_line("unexpected ok")
                error(e) => io.write_standard_output_line("{e.kind}")
            }
            io.write_standard_output_line("mismatches {mismatches}")
            """);

        Assert.Equal("transform_failed\nmismatches 0\n", output);
    }

    [Fact]
    public void Walk_matches_filesystem_walk_directory_for_every_worker_count()
    {
        using var workingDirectory = new TemporaryWorkingDirectory();
        var tree = Path.Combine(workingDirectory.Path, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "a", "c"));
        Directory.CreateDirectory(Path.Combine(tree, "empty"));
        File.WriteAllText(Path.Combine(tree, "b.txt"), string.Empty);
        File.WriteAllText(Path.Combine(tree, "a", "x.txt"), string.Empty);
        File.WriteAllText(Path.Combine(tree, "a", "c", "deep.txt"), string.Empty);
        File.CreateSymbolicLink(Path.Combine(tree, "link"), Path.Combine(tree, "a"));

        var scriptPath = Path.Combine(workingDirectory.Path, "walk.scs");
        File.WriteAllText(
            scriptPath,
            """
            function ignore(target: path) -> unit {
            }
            let root = "tree".to_path()
            let sequential = filesystem.walk_directory(root)?.entries.transform(entry => entry.path)
            let one = parallel.walk_directory(root, 1, ignore)?
            let many = parallel.walk_directory(root, 8, ignore)?
            io.write_standard_output_line("{sequential == one} {sequential == many}")
            io.write_standard_output_line("{many.transform(p => p.to_string()).join(",")}")
            """);

        var run = InterpreterFiles.RunFile(scriptPath);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(
            "true true\ntree/a,tree/a/c,tree/a/c/deep.txt,tree/a/x.txt,tree/b.txt,tree/empty,tree/link\n",
            run.StandardOutput);
    }

    /// <summary>
    /// BLOCKER-1 regression: a worker used to enqueue after the fold had already completed the queue, throwing
    /// <c>InvalidOperationException</c> on the worker thread and aborting the whole process (~7 % of single walks).
    /// Many repeats over a multi-directory tree make the race certain if it comes back; the harness would die with
    /// the process, so reaching the assertion at all is the proof, and the result must stay identical every time.
    /// </summary>
    [Fact]
    public void Walk_repeated_over_a_directory_tree_never_crashes_and_stays_identical()
    {
        using var workingDirectory = new TemporaryWorkingDirectory();
        var tree = Path.Combine(workingDirectory.Path, "ctree");
        Directory.CreateDirectory(Path.Combine(tree, "a"));
        // A wide tree under one directory gives the speculative workers many parallel enqueues to race against.
        for (var index = 0; index < 200; index++)
        {
            var directory = Path.Combine(tree, "a", $"d{index}");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "f1.txt"), string.Empty);
            File.WriteAllText(Path.Combine(directory, "f2.txt"), string.Empty);
        }

        var scriptPath = Path.Combine(workingDirectory.Path, "stress.scs");
        File.WriteAllText(
            scriptPath,
            """
            function ignore(target: path) -> unit {
            }
            let root = "ctree".to_path()
            let expected = filesystem.walk_directory(root)?.entries.transform(entry => entry.path)
            let mutable count = 0
            while count < 200 {
                let workers = 8 + (count % 3) * 8
                let result = parallel.walk_directory(root, workers, ignore)?
                if result != expected {
                    io.write_standard_output_line("MISMATCH at {count}")
                    return 1
                }
                count += 1
            }
            io.write_standard_output_line("identical {expected.length()}")
            """);

        var run = InterpreterFiles.RunFile(scriptPath);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("identical 601\n", run.StandardOutput);
    }

    /// <summary>
    /// MAJOR-3 regression: the walker kept every directory's expansion (its child list) for the whole call, so
    /// memory grew with the number of directories. Expansions are now released as the fold consumes them, so
    /// the number of retained child lists is bounded by the speculative queue capacity plus the live walk
    /// depth, not by the tree size. The diagnostic is reset and read around a walk whose directory count far
    /// exceeds that bound, so an unbounded (whole-call) retention is caught.
    /// </summary>
    [Fact]
    public void Walk_releases_expansions_instead_of_keeping_every_directory()
    {
        using var workingDirectory = new TemporaryWorkingDirectory();
        var tree = Path.Combine(workingDirectory.Path, "deep");
        Directory.CreateDirectory(Path.Combine(tree, "a"));
        // 3000 sibling directories: far more than the speculative queue bound (workers * 256 = 512), so keeping
        // every expansion for the whole walk would retain all 3001 lists.
        const int directories = 3000;
        for (var index = 0; index < directories; index++)
        {
            var directory = Path.Combine(tree, "a", $"d{index}");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "f.txt"), string.Empty);
        }

        var scriptPath = Path.Combine(workingDirectory.Path, "retention.scs");
        File.WriteAllText(
            scriptPath,
            """
            function ignore(target: path) -> unit {
            }
            let root = "deep".to_path()
            let result = parallel.walk_directory(root, 2, ignore)?
            io.write_standard_output_line("{result.length()}")
            """);

        ParallelWalkerProbe.Reset();
        var run = InterpreterFiles.RunFile(scriptPath);

        Assert.Equal(0, run.ExitCode);
        // a + 3000 dirs + 3000 files = 6001 paths (the root is excluded, like filesystem.walk_directory).
        Assert.Equal("6001\n", run.StandardOutput);
        // Backstop well above the queue bound (2 * 256 = 512) plus the fold depth, and far below the 3001
        // expansions an unbounded retention would keep alive.
        Assert.True(
            ParallelWalkerProbe.PeakRetained <= 1500,
            $"the walker retained {ParallelWalkerProbe.PeakRetained} expansions at once (expected <= 1500)");
    }

    /// <summary>
    /// Concurrent-run refcount (lifetime of the shared <c>Core.Concurrency</c> context). A long-lived host can run
    /// several scripts at once in one process: <see cref="ParallelPool.Quiesce"/> must only tear the context down
    /// when the LAST run ends, never while another run still has a job in flight. Each nested run here is a real
    /// <c>parallel.transform</c> (the harness brackets it with its own BeginRun/Quiesce), so this proves the worker
    /// threads survive an inner run ending, and that the next run gets a fresh context that still works.
    /// </summary>
    [Fact]
    public void Shared_context_survives_until_the_last_run_ends()
    {
        var source =
            """
            let values = parallel.transform([1, 2, 3, 4], 4, n => n * n)?
            io.write_standard_output_line(values.transform(n => "{n}").join(","))
            """;

        // Two runs "in flight": the inner run ends and must NOT dispose the shared context.
        ParallelPool.BeginRun();
        try
        {
            Assert.Equal("1,4,9,16\n", Output(source));
            // A second run right after still finds a usable context (workers reused, no teardown).
            Assert.Equal("1,4,9,16\n", Output(source));
        }
        finally
        {
            ParallelPool.Quiesce();
        }

        // Last run ended: the context is gone and is re-created lazily on the next call.
        Assert.Equal("1,4,9,16\n", Output(source));
    }
}

/// <summary>Reads the <c>ParallelWalker</c> MAJOR-3 retention diagnostic, which is internal to the runtime.</summary>
internal static class ParallelWalkerProbe
{
    public static void Reset() => ParallelWalker.ResetRetentionDiagnostics();

    public static int PeakRetained => ParallelWalker.PeakRetainedExpansions;
}
