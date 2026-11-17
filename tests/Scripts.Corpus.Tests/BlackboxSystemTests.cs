using System.Diagnostics;
using System.Text;

namespace Scripts.Corpus.Tests;

/// <summary>
/// xUnit port of the black-box limits-and-system axis: execution limits, closed
/// and full outputs, EPIPE, a FIFO without reader, forbidden removals, descriptor
/// pressure, and the standard-input contract (closed, empty, directory, invalid
/// bytes, NUL, paused and silent producers, oversize, arguments, consumed stream).
/// Every child is bounded and no crash may print a trace.
/// </summary>
[Collection("corpus")]
public sealed class BlackboxSystemTests
{
    private sealed record ExitCase(int ExitCode, string Source);

    private static readonly IReadOnlyDictionary<string, ExitCase> ExitTable =
        new Dictionary<string, ExitCase>(StringComparer.Ordinal)
        {
            ["recursion_over_limit"] = new(253, """
                function descend(n: integer) -> integer {
                    return descend(n + 1)
                }
                io.write_standard_output_line("{descend(0)}")
                """),
            ["range_too_large"] = new(253, """
                let xs = range(0, 20000000)
                io.write_standard_output_line("{xs.length()}")
                """),
            ["index_out_of_bounds"] = new(253, """
                let xs = [1, 2, 3]
                io.write_standard_output_line("{xs[5]}")
                """),
            ["missing_key_runtime"] = new(253, """
                let m = {"a": 1}
                io.write_standard_output_line("{m["absent"]}")
                """),
            ["get_out_of_bounds_is_valueerror"] = new(0, """
                match [1, 2].get_at_index(9) {
                    ok(v) => io.write_standard_output_line("ok")
                    error(e) => io.write_standard_output_line(e.kind)
                }
                """),
            ["ast_depth_rejected"] = new(251, """
                let v = [[[[[[[[[[[[[[[[[[[[1]]]]]]]]]]]]]]]]]]]]
                io.write_standard_output_line("{v}")
                """),
        };

    private const string TinySource = """
        io.write_standard_output_line("ok")
        """;

    private const string ClosedSource = """
        io.write_standard_output_line("x")
        """;

    private const string BigSource = """
        let mutable i = 0
        while i < 100000 {
            io.write_standard_output_line("ligne {i}")
            i += 1
        }
        """;

    private const string ManyExecSource = """
        let mutable i = 0
        let mutable failures = 0
        while i < 200 {
            match process.run(process.request("true", [])) {
                ok(_) => {
                    failures += 0
                }
                error(_) => {
                    failures += 1
                }
            }
            i += 1
        }
        io.write_standard_output_line("fini, echecs={failures}")
        """;

    private const string ParallelExecSource = """
        let mutable requests: list<run_request> = []
        for i in range(0, 400) {
            requests.append(process.request("true", []))
        }
        let results = parallel.run_all(requests, 8)?
        io.write_standard_output_line("{results.length()}")
        """;

    private const string ArgumentsSource = """
        for a in process.arguments() {
            io.write_standard_output_line("[{a}]")
        }
        """;

    public static TheoryData<string, int> ExitCases()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in ExitTable.Keys.Order(StringComparer.Ordinal))
        {
            data.Add(name, ExitTable[name].ExitCode);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ExitCases))]
    public void ExpectExit(string name, int expectedExitCode)
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        var result = BlackboxRunner.RunScript(work.Path, name, ExitTable[name].Source);
        Assert.False(result.TimedOut, $"{name}: timed out after {result.ElapsedMilliseconds}ms");
        Assert.True(
            result.ExitCode == expectedExitCode,
            $"{name}: expected exit={expectedExitCode} got={result.ExitCode}\n{result.StandardError}");
        BlackboxRunner.AssertNoTrace(result, name);
    }

    [Fact(DisplayName = "low_memory")]
    public void LowMemory()
    {
        if (!BlackboxRunner.SourceIsPublished)
        {
            return;
        }

        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        WriteSource(work.Path, "tiny.scs", TinySource);
        var result = RunShell(
            work.Path,
            "ulimit -v 1400000 2>/dev/null; exec \"$1\" run tiny.scs < /dev/null",
            10_000);
        Assert.False(
            result.CombinedOutput.Contains("internal error", StringComparison.Ordinal),
            $"low_memory: « internal error » instead of a clean failure\n{result.CombinedOutput}");
        BlackboxRunner.AssertNoTrace(result, "low_memory");
    }

    [Fact(DisplayName = "stdout_closed")]
    public void StdoutClosed()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        WriteSource(work.Path, "closed.scs", ClosedSource);
        var result = RunShell(work.Path, "exec \"$1\" run closed.scs < /dev/null >&- 2>/dev/null", 10_000);
        Assert.True(result.ExitCode == 253, $"stdout_closed: expected 253, got {result.ExitCode}");
    }

    [Fact(DisplayName = "dev_full")]
    public void DevFull()
    {
        if (!File.Exists("/dev/full"))
        {
            return;
        }

        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        WriteSource(work.Path, "closed.scs", ClosedSource);
        var result = RunShell(work.Path, "exec \"$1\" run closed.scs < /dev/null >/dev/full 2>/dev/null", 10_000);
        Assert.True(result.ExitCode == 253, $"dev_full: expected 253, got {result.ExitCode}");
    }

    [Fact(DisplayName = "epipe")]
    public void Epipe()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        WriteSource(work.Path, "big.scs", BigSource);
        var result = RunShell(
            work.Path,
            "\"$1\" run big.scs < /dev/null 2>/dev/null | head -1 >/dev/null; exit \"${PIPESTATUS[0]}\"",
            30_000);
        Assert.True(result.ExitCode == 141, $"epipe (141), got {result.ExitCode}");
    }

    [Fact(DisplayName = "fifo_without_reader")]
    public void FifoWithoutReader()
    {
        var mkfifo = new[] { "/usr/bin/mkfifo", "/bin/mkfifo" }.FirstOrDefault(File.Exists);
        if (mkfifo is null)
        {
            return;
        }

        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        var fifo = Path.Combine(work.Path, "fifo");
        var startInfo = new ProcessStartInfo(mkfifo)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(fifo);
        using (var mkfifoProcess = Process.Start(startInfo)!)
        {
            mkfifoProcess.WaitForExit();
            if (mkfifoProcess.ExitCode != 0)
            {
                return;
            }
        }

        var source = $$"""
            match filesystem.write_file_overwrite("data", p"{{fifo}}") {
                ok(_) => io.write_standard_output_line("ok")
                error(e) => io.write_standard_output_line("kind={e.kind}")
            }
            """;
        var result = BlackboxRunner.RunScript(work.Path, "fifo", source, timeoutMilliseconds: 10_000);
        Assert.False(result.TimedOut, $"fifo: timed out after {result.ElapsedMilliseconds}ms");
        Assert.Equal("kind=other\n", result.StandardOutput);
        BlackboxRunner.AssertNoTrace(result, "fifo");
    }

    [Fact(DisplayName = "refuse_proc")]
    public void RefuseProc()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        var source = $$"""
            match filesystem.remove_directory_tree(p"/proc/self/root{{work.Path}}") {
                ok(_) => io.write_standard_output_line("surprisingly ok")
                error(e) => io.write_standard_output_line(e.kind)
            }
            """;
        var result = BlackboxRunner.RunScript(work.Path, "refuse", source, timeoutMilliseconds: 10_000);
        Assert.False(result.TimedOut, $"refuse_proc: timed out after {result.ElapsedMilliseconds}ms");
        Assert.Equal("invalid_path\n", result.StandardOutput);
        BlackboxRunner.AssertNoTrace(result, "refuse_proc");
    }

    [Fact(DisplayName = "manyexec")]
    public void ManyExec()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        WriteSource(work.Path, "manyexec.scs", ManyExecSource);
        var result = RunShell(
            work.Path,
            "ulimit -n 96 || exit 99; exec \"$1\" run manyexec.scs < /dev/null",
            30_000);
        Assert.False(result.TimedOut, $"manyexec: timed out after {result.ElapsedMilliseconds}ms");
        Assert.True(result.ExitCode == 0, $"manyexec (exit {result.ExitCode})\n{result.StandardError}");
        BlackboxRunner.AssertNoTrace(result, "manyexec");
    }

    [Fact(DisplayName = "parallelexec")]
    public void ParallelExec()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        WriteSource(work.Path, "parallelexec.scs", ParallelExecSource);
        var result = RunShell(
            work.Path,
            "ulimit -n 512 || exit 99; exec \"$1\" run parallelexec.scs < /dev/null",
            60_000);
        Assert.False(result.TimedOut, $"parallelexec: timed out after {result.ElapsedMilliseconds}ms");
        Assert.True(
            result.ExitCode == 0 && result.StandardOutput == "400\n",
            $"parallelexec : expected 400, rc={result.ExitCode} stdout=[{result.StandardOutput}]\n{result.StandardError}");
        BlackboxRunner.AssertNoTrace(result, "parallelexec");
    }

    [Fact(DisplayName = "stdin_closed")]
    public void StdinClosed()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        var result = RunShell(work.Path, "exec \"$1\" run - <&-", 10_000);
        var bound = BlackboxRunner.SourceIsPublished ? 1000 : 5000;
        Assert.False(result.TimedOut, $"stdin_closed: timed out after {result.ElapsedMilliseconds}ms");
        Assert.True(
            result.ExitCode == 0 && result.ElapsedMilliseconds < bound,
            $"stdin_closed (rc={result.ExitCode}, {result.ElapsedMilliseconds}ms)");
        BlackboxRunner.AssertNoTrace(result, "stdin_closed");
    }

    [Fact(DisplayName = "stdin_empty")]
    public void StdinEmpty()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        var result = RunShell(work.Path, "exec \"$1\" run - </dev/null", 10_000);
        Assert.True(result.ExitCode == 0, $"stdin_empty (rc={result.ExitCode})");
        BlackboxRunner.AssertNoTrace(result, "stdin_empty");
    }

    [Fact(DisplayName = "stdin_directory")]
    public void StdinDirectory()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        var result = RunShell(work.Path, "exec \"$1\" run - < /", 10_000);
        Assert.True(
            result.ExitCode == 252 && result.StandardError.Contains("SCRIPTS-CLI-0010", StringComparison.Ordinal),
            $"stdin_directory (rc={result.ExitCode})\n{result.StandardError}");
        BlackboxRunner.AssertNoTrace(result, "stdin_directory");
    }

    [Fact(DisplayName = "stdin_directory_check")]
    public void StdinDirectoryCheck()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        var result = RunShell(work.Path, "exec \"$1\" check - < /", 10_000);
        Assert.True(result.ExitCode == 252, $"stdin_directory_check (rc={result.ExitCode})");
        BlackboxRunner.AssertNoTrace(result, "stdin_directory_check");
    }

    [Fact(DisplayName = "stdin_invalid_utf8")]
    public void StdinInvalidUtf8()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        var prefix = Encoding.UTF8.GetBytes("io.write_standard_output_line(\"abc");
        var suffix = Encoding.UTF8.GetBytes("\")\n");
        var bytes = new byte[prefix.Length + 1 + suffix.Length];
        prefix.CopyTo(bytes, 0);
        bytes[prefix.Length] = 0xFF;
        suffix.CopyTo(bytes, prefix.Length + 1);
        File.WriteAllBytes(Path.Combine(work.Path, "binlatin.scs"), bytes);
        var result = RunShell(work.Path, "exec \"$1\" run - < binlatin.scs", 10_000);
        Assert.True(
            result.ExitCode == 251
            && result.StandardError.Contains("SCRIPTS-PARSE-0001", StringComparison.Ordinal)
            && result.StandardError.Contains("<stdin>", StringComparison.Ordinal),
            $"stdin_invalid_utf8 (rc={result.ExitCode})\n{result.StandardError}");
        BlackboxRunner.AssertNoTrace(result, "stdin_invalid_utf8");
    }

    [Fact(DisplayName = "stdin_nul")]
    public void StdinNul()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        var prefix = Encoding.UTF8.GetBytes("io.write_standard_output_line(\"a");
        var suffix = Encoding.UTF8.GetBytes("b\")\n");
        var bytes = new byte[prefix.Length + 1 + suffix.Length];
        prefix.CopyTo(bytes, 0);
        bytes[prefix.Length] = 0x00;
        suffix.CopyTo(bytes, prefix.Length + 1);
        File.WriteAllBytes(Path.Combine(work.Path, "nul.scs"), bytes);
        var result = RunShell(work.Path, "exec \"$1\" run - < nul.scs", 10_000);
        Assert.True(
            result.ExitCode == 251
            && result.StandardError.Contains("SCRIPTS-PARSE-0002", StringComparison.Ordinal),
            $"stdin_nul (rc={result.ExitCode})\n{result.StandardError}");
        BlackboxRunner.AssertNoTrace(result, "stdin_nul");
    }

    [Fact(DisplayName = "stdin_paused_pipe")]
    public void StdinPausedPipe()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        const string command = """
            { printf '%s\n' 'io.write_standard_output_line("first")'; sleep 2; printf '%s\n' 'io.write_standard_output_line("second")'; } | "$1" run -
            """;
        var result = RunShell(work.Path, command, 20_000);
        Assert.False(result.TimedOut, $"stdin_paused_pipe: timed out after {result.ElapsedMilliseconds}ms");
        Assert.Equal("first\nsecond\n", result.StandardOutput);
        BlackboxRunner.AssertNoTrace(result, "stdin_paused_pipe");
    }

    [Fact(DisplayName = "stdin_silent_stream")]
    public void StdinSilentStream()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        const string command = """
            "$1" run - < <(sleep 3; printf '%s\n' 'io.write_standard_output_line("late")')
            """;
        var result = RunShell(work.Path, command, 10_000);
        Assert.False(result.TimedOut, $"stdin_silent_stream: timed out after {result.ElapsedMilliseconds}ms");
        Assert.True(
            result.ExitCode == 0 && result.ElapsedMilliseconds < 8000,
            $"stdin_silent_stream (rc={result.ExitCode}, {result.ElapsedMilliseconds}ms)");
        BlackboxRunner.AssertNoTrace(result, "stdin_silent_stream");
    }

    [Fact(DisplayName = "stdin_oversize")]
    public void StdinOversize()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        var oversize = new byte[4_194_305];
        Array.Fill(oversize, (byte)' ');
        File.WriteAllBytes(Path.Combine(work.Path, "oversize.scs"), oversize);
        var result = RunShell(work.Path, "exec \"$1\" run - < oversize.scs", 20_000);
        Assert.True(
            result.ExitCode == 251
            && result.StandardError.Contains("SCRIPTS-PARSE-0004", StringComparison.Ordinal),
            $"stdin_oversize (rc={result.ExitCode})\n{result.StandardError}");
        BlackboxRunner.AssertNoTrace(result, "stdin_oversize");
    }

    [Fact(DisplayName = "stdin_arguments")]
    public void StdinArguments()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        WriteSource(work.Path, "args_stdin.scs", ArgumentsSource);
        var result = RunShell(
            work.Path,
            "exec \"$1\" run - \"a b\" \"-n\" < args_stdin.scs",
            10_000);
        Assert.Equal("[a b]\n[-n]\n", result.StandardOutput);
        BlackboxRunner.AssertNoTrace(result, "stdin_arguments");
    }

    [Fact(DisplayName = "stdin_consumed")]
    public void StdinConsumed()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-system-");
        WriteSource(work.Path, "after.scs", """
            io.write_standard_output_line(io.read_standard_input()?)
            """);
        var result = RunShell(work.Path, "exec \"$1\" run - < after.scs", 10_000);
        Assert.Equal(string.Empty, result.StandardOutput.TrimEnd('\n'));
        BlackboxRunner.AssertNoTrace(result, "stdin_consumed");
    }

    private static BlackboxProcessResult RunShell(string work, string command, int timeoutMilliseconds)
    {
        return BlackboxRunner.RunShell(work, command, [BlackboxRunner.Binary], timeoutMilliseconds);
    }

    private static void WriteSource(string work, string name, string source)
    {
        File.WriteAllText(
            Path.Combine(work, name),
            source,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
