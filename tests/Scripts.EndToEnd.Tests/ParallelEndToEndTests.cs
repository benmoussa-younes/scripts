using System.Diagnostics;

namespace Scripts.EndToEnd.Tests;

/// <summary>
/// Serializes the parallel end-to-end tests: they start real children and inspect live process state, and the timed
/// dispatch guard below must not compete with the rest of the suite for the host's CPU.
/// </summary>
[CollectionDefinition(ParallelEndToEndTests.CollectionName, DisableParallelization = true)]
public sealed class ParallelEndToEndTestsCollection
{
}

/// <summary>
/// <c>parallel</c> on the real process (DESIGN §7.10.1). The group-kill probes write unique markers so the
/// tests can prove the children are gone after the call, not merely that the call returned.
/// </summary>
[Collection(ParallelEndToEndTests.CollectionName)]
public sealed class ParallelEndToEndTests
{
    public const string CollectionName = "parallel-end-to-end";

    [Fact]
    public void Exec_all_abort_kills_in_flight_children_and_leaves_no_orphan()
    {        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var marker = $"scripts-parallel-{Guid.NewGuid():N}";
            var script = ScriptsProcess.WriteScript(
                directory,
                "terminate.scs",
                $$"""
                let mutable sleepers: list<run_request> = []
                let mutable index = 0
                while index < 12 {
                    sleepers.append(process.request("sh", ["-c", "sleep 30 # {{marker}}"]))
                    index += 1
                }
                let requests = [process.request("no-such-program-scripts-test", [])] + sleepers
                match parallel.run_all(requests, 8) {
                    ok(results) => io.write_standard_output_line("ok {results.length()}")
                    error(e) => io.write_standard_output_line("{e.kind}")
                }
                """);

            var stopwatch = Stopwatch.StartNew();
            var result = ScriptsProcess.Run("run", script);
            stopwatch.Stop();

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("spawn_failed\n", result.StandardOutput);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"abort took {stopwatch.Elapsed}");

            // The children are killed by the abort, not left running for their own 30 seconds. The probe's own
            // shell carries the marker in its argv, so it is the single allowed match.
            var survivors = CountMarkerProcesses(marker);
            Assert.True(survivors <= 1, $"{survivors - 1} child process(es) survived the abort");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// BLOCKER-2 regression on the real process: the failing request is <em>last</em>, behind a live
    /// <c>sleep 30</c>. With the old 16-item block the failure was only reached once the sleeper had finished,
    /// so the call waited for it and the child was left running; per-item dispatch reaches the failure at once
    /// and the group-kill removes the sleeper.
    /// </summary>
    [Fact]
    public void Exec_all_aborts_on_a_last_failure_and_kills_the_in_flight_child()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var marker = $"scripts-parallel-{Guid.NewGuid():N}";
            var script = ScriptsProcess.WriteScript(
                directory,
                "kill-last.scs",
                $$"""
                let requests = [
                    process.request("sh", ["-c", "sleep 30 # {{marker}}"]),
                    process.request("no-such-program-scripts-test", []),
                ]
                match parallel.run_all(requests, 4) {
                    ok(results) => io.write_standard_output_line("ok {results.length()}")
                    error(e) => io.write_standard_output_line("{e.kind}")
                }
                """);

            var stopwatch = Stopwatch.StartNew();
            var result = ScriptsProcess.Run("run", script);
            stopwatch.Stop();

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("spawn_failed\n", result.StandardOutput);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"last-item abort took {stopwatch.Elapsed}");

            var survivors = CountMarkerProcesses(marker);
            Assert.True(survivors <= 1, $"{survivors - 1} child process(es) survived the abort");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// MAJOR-1 regression: <c>workers</c> was inert for lists up to 16 items (one worker owned the whole
    /// 16-item block), so the map ran as fast at <c>workers=1</c> as at <c>workers=4</c>. Per-item dispatch
    /// must visibly use the requested concurrency on the real process.
    ///
    /// The workload is <em>sleep-based</em>, not CPU-bound on purpose: 16 items of 40 ms take at least 640 ms
    /// at <c>workers=1</c> and at least 160 ms at <c>workers=4</c>, and sleeps overlap regardless of the host's
    /// CPU load. A CPU-bound workload is the wrong probe here: when the host is loaded (another test suite
    /// running), both configurations slow down together and the ratio collapses on host contention instead
    /// of exposing a dispatch regression.
    ///
    /// Each configuration is measured <see cref="TimeMapAttempts"/> times and the best (smallest) sample is
    /// kept, as <c>src/tests/AGENTS.md</c> requires for timed tests: noise only adds time. The 0.7 ratio is
    /// a wide anti-regression guard, not a performance measurement.
    /// </summary>
    [Fact]
    public void Map_with_four_workers_is_faster_than_workers_one_for_sixteen_items()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var workersOne = BestTimeMap(directory, 1);
            var workersFour = BestTimeMap(directory, 4);
            Assert.True(
                workersFour < workersOne * 0.7,
                $"workers=4 took {workersFour} (best of {TimeMapAttempts}) vs workers=1 {workersOne} " +
                $"(best of {TimeMapAttempts}) (expected a clear margin)");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Attempts per configuration; the smallest sample is kept to filter scheduler noise.</summary>
    private const int TimeMapAttempts = 3;

    private static TimeSpan BestTimeMap(string directory, int workers)
    {
        var best = TimeSpan.MaxValue;
        for (var attempt = 0; attempt < TimeMapAttempts; attempt++)
        {
            var elapsed = TimeMap(directory, workers);
            if (elapsed < best)
            {
                best = elapsed;
            }
        }

        return best;
    }

    private static TimeSpan TimeMap(string directory, int workers)
    {
        var script = ScriptsProcess.WriteScript(
            directory,
            $"concurrency-{workers}.scs",
            $$"""
            function settle(n: integer) -> integer {
                match time.sleep_milliseconds(40) {
                    ok(_) => {
                    }
                    error(_) => {
                    }
                }
                return n
            }
            let values = parallel.transform(range(0, 16), {{workers}}, settle)?
            io.write_standard_output_line("{values.length()}")
            """);
        var stopwatch = Stopwatch.StartNew();
        var result = ScriptsProcess.Run("run", script);
        stopwatch.Stop();
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("16\n", result.StandardOutput);
        return stopwatch.Elapsed;
    }

    [Fact]
    public void Map_returns_in_input_order_on_the_real_process()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "map.scs",
                """
                let values = parallel.transform(range(0, 200), 8, n => (n * 7) % 200)?
                io.write_standard_output_line("{values.transform(n => "{n}").join(",")}")
                """);

            var result = ScriptsProcess.Run("run", script);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Join(",", Enumerable.Range(0, 200).Select(n => (n * 7) % 200)) + "\n", result.StandardOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_signal_during_exec_all_propagates_as_a_runtime_error_and_kills_the_children()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var marker = $"scripts-parallel-{Guid.NewGuid():N}";
            var script = ScriptsProcess.WriteScript(
                directory,
                "signal.scs",
                $$"""
                let mutable sleepers: list<run_request> = []
                let mutable index = 0
                while index < 6 {
                    sleepers.append(process.request("sh", ["-c", "sleep 30 # {{marker}}"]))
                    index += 1
                }
                match parallel.run_all(sleepers, 6) {
                    ok(results) => io.write_standard_output_line("ok")
                    error(e) => io.write_standard_output_line("result {e.kind}")
                }
                io.write_standard_output_line("apres")
                """);

            var startInfo = new ProcessStartInfo(ScriptsProcess.Binary)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add(script);
            using var process = Process.Start(startInfo)!;

            // Wait for the six children themselves, then interrupt: a signal before they start would let the
            // survivor assertion pass vacuously.
            Assert.True(WaitForMarkerProcesses(marker, 6, 10_000), "the six children never started");
            SendTermination(process.Id);
            process.WaitForExit();
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();

            Assert.Equal(143, process.ExitCode);
            Assert.DoesNotContain("spawn_failed", standardOutput, StringComparison.Ordinal);
            Assert.Contains("SCRIPTS-RUNTIME-0018", standardError, StringComparison.Ordinal);

            var survivors = CountMarkerProcesses(marker);
            Assert.True(survivors <= 1, $"{survivors - 1} child process(es) survived the signal");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool WaitForMarkerProcesses(string marker, int expected, int timeoutMilliseconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (CountMarkerProcesses(marker) >= expected)
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return CountMarkerProcesses(marker) >= expected;
    }

    private static void SendTermination(int processIdentifier)
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("kill -TERM \"$0\"");
        startInfo.ArgumentList.Add(processIdentifier.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var signaler = Process.Start(startInfo)!;
        signaler.WaitForExit();
    }

    /// <summary>Live processes whose command line carries the unique marker (the 30 s sleepers of the probes).</summary>
    private static int CountMarkerProcesses(string marker)
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("pgrep -f \"$0\" | wc -l");
        startInfo.ArgumentList.Add(marker);
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return int.TryParse(output, out var count) ? count : 0;
    }
}
