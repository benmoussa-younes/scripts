using System.Diagnostics;
using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

internal static class SystemScripts
{
    public static string CreateExecutable(SystemTestWorkspace workspace, string name, string content)
    {
        var path = workspace.Write(name, content);
        File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("755", 8));
        return path;
    }

    public static RecordValue Exec(RuntimeContext context, string program, params string[] arguments) =>
        Ok<RecordValue>(ExecResult(context, program, arguments));

    public static Value ExecResult(RuntimeContext context, string program, params string[] arguments) =>
        RunRequest(context, program, arguments);

    public static IntegerValue Inherit(RuntimeContext context, string program, params string[] arguments) =>
        Ok<IntegerValue>(InheritResult(context, program, arguments));

    public static Value InheritResult(RuntimeContext context, string program, params string[] arguments) =>
        RunInheritRequest(context, program, arguments);

    public static RecordValue Shell(RuntimeContext context, string script) => Exec(context, "/bin/sh", "-c", script);
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemExecTests
{
    [Fact]
    public void Returns_exit_code_and_both_streams_separately()
    {
        using var context = NewContext();

        var outcome = SystemScripts.Shell(context, "echo out; echo err >&2; exit 7");

        Assert.Equal("process_result", outcome.TypeName);
        Assert.Equal(7, outcome.GetInteger("exit_code"));
        Assert.Equal("out\n", outcome.GetText("standard_output"));
        Assert.Equal("err\n", outcome.GetText("standard_error"));
        Assert.Equal("/bin/sh", outcome.GetText("program"));
    }

    [Fact]
    public void Non_zero_exit_is_not_an_error_and_death_by_signal_is_128_plus_signal()
    {
        using var context = NewContext();

        Assert.Equal(0, SystemScripts.Exec(context, "echo", "hello").GetInteger("exit_code"));
        Assert.Equal(1, SystemScripts.Exec(context, "false").GetInteger("exit_code"));
        Assert.Equal(137, SystemScripts.Shell(context, "kill -9 $$").GetInteger("exit_code"));
    }

    [Fact]
    public void Arguments_are_passed_verbatim_and_never_reparsed()
    {
        using var context = NewContext();

        var outcome = SystemScripts.Exec(context, "printf", "%s|", "a b", "$HOME", "*", string.Empty, "`id`", "; echo pwned", "line\nbreak", "-n", "é😀", "'\"\\");

        Assert.Equal("a b|$HOME|*||`id`|; echo pwned|line\nbreak|-n|é😀|'\"\\|", outcome.GetText("standard_output"));
    }

    [Fact]
    public void Stdin_is_closed_immediately()
    {
        using var context = NewContext();

        var outcome = SystemScripts.Exec(context, "cat");

        Assert.Equal(0, outcome.GetInteger("exit_code"));
        Assert.Equal(string.Empty, outcome.GetText("standard_output"));
        Assert.Equal(0, context.ChildProcesses.Count);
    }

    [Fact]
    public void The_child_argv0_is_the_resolved_absolute_path()
    {
        using var context = NewContext();
        var resolved = Ok<PathValue>(Call(context, "process.find_program_path", Text("cat"))).Text;

        var commandLine = SystemScripts.Exec(context, "cat", "/proc/self/cmdline").GetText("standard_output");

        Assert.Equal(resolved + "\0/proc/self/cmdline\0", commandLine);
    }

    [Fact]
    public void Large_simultaneous_stdout_and_stderr_do_not_deadlock()
    {
        using var context = NewContext();

        var outcome = SystemScripts.Shell(
            context,
            """(head -c 4000000 /dev/zero | tr '\0' x) & (head -c 4000000 /dev/zero | tr '\0' y >&2) & wait""");

        Assert.Equal(4_000_000, outcome.GetText("standard_output").Length);
        Assert.Equal(4_000_000, outcome.GetText("standard_error").Length);
        Assert.True(outcome.GetText("standard_output").All(character => character == 'x'));
        Assert.True(outcome.GetText("standard_error").All(character => character == 'y'));
    }

    [Fact]
    public void Rejects_invalid_utf8_without_stripping_the_bom()
    {
        using var context = NewContext();

        AssertProcessError(SystemScripts.ExecResult(context, "printf", "\\xff"), "invalid_encoding", "printf");
        AssertProcessError(SystemScripts.ExecResult(context, "printf", "a\\xe2\\x82"), "invalid_encoding", "printf");
        Assert.Equal("\uFEFFabc", SystemScripts.Exec(context, "printf", "\\xef\\xbb\\xbfabc").GetText("standard_output"));
        Assert.Equal("é😀", SystemScripts.Exec(context, "printf", "é😀").GetText("standard_output"));
    }

    [Fact]
    public void Children_inherit_the_environment_including_set_and_remove()
    {
        using var context = NewContext();
        Environment.SetEnvironmentVariable("SCRIPTS_SYSTEM_TEST_VARIABLE", "value é");
        try
        {
            var present = SystemScripts.Exec(context, "/usr/bin/env").GetText("standard_output").Split('\n');
            Environment.SetEnvironmentVariable("SCRIPTS_SYSTEM_TEST_VARIABLE", null);
            var absent = SystemScripts.Exec(context, "/usr/bin/env").GetText("standard_output").Split('\n');

            Assert.Contains("SCRIPTS_SYSTEM_TEST_VARIABLE=value é", present);
            Assert.DoesNotContain(absent, line => line.StartsWith("SCRIPTS_SYSTEM_TEST_VARIABLE=", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SCRIPTS_SYSTEM_TEST_VARIABLE", null);
        }
    }

    [Fact]
    public void Program_resolution_errors()
    {
        using var workspace = new SystemTestWorkspace();
        var plain = workspace.Write("plain", "x");
        File.SetUnixFileMode(plain, (UnixFileMode)Convert.ToInt32("644", 8));
        using var context = NewContext();

        AssertProcessError(SystemScripts.ExecResult(context, "scripts-programme-inexistant"), "not_found", "scripts-programme-inexistant");
        AssertProcessError(SystemScripts.ExecResult(context, workspace.Combine("missing")), "not_found", workspace.Combine("missing"));
        AssertProcessError(SystemScripts.ExecResult(context, plain + "/below"), "not_found");
        AssertProcessError(SystemScripts.ExecResult(context, plain), "permission_denied", plain);
        AssertProcessError(SystemScripts.ExecResult(context, workspace.Root), "permission_denied", workspace.Root);
        AssertProcessError(SystemScripts.ExecResult(context, string.Empty), "invalid_argument", string.Empty);
        AssertProcessError(SystemScripts.ExecResult(context, "a\0b"), "invalid_argument");
        AssertProcessError(SystemScripts.ExecResult(context, "echo", "ok", "bad\0arg"), "invalid_argument", "echo");
    }

    [Fact]
    public void Executable_script_without_a_shebang_is_not_run_through_sh()
    {
        using var workspace = new SystemTestWorkspace();
        var script = SystemScripts.CreateExecutable(workspace, "no-shebang", "echo should-not-run\n");
        using var context = NewContext();

        var result = SystemScripts.ExecResult(context, script);

        AssertProcessError(result, "other", script);
    }

    [Fact]
    public void Script_with_an_unavailable_interpreter_is_not_found_and_a_valid_shebang_runs()
    {
        using var workspace = new SystemTestWorkspace();
        var broken = SystemScripts.CreateExecutable(workspace, "broken", "#!/scripts-interpreteur-inexistant\n");
        var working = SystemScripts.CreateExecutable(workspace, "working", "#!/bin/sh\necho \"args:$1:$2\"\n");
        using var context = NewContext();

        AssertProcessError(SystemScripts.ExecResult(context, broken), "not_found", broken);
        Assert.Equal("args:a b:$HOME\n", SystemScripts.Exec(context, working, "a b", "$HOME").GetText("standard_output"));
    }

    [Fact]
    public void Program_containing_a_slash_is_relative_to_the_current_directory()
    {
        using var workspace = new SystemTestWorkspace();
        SystemScripts.CreateExecutable(workspace, "tool", "#!/bin/sh\necho relatif\n");
        using var context = NewContext();

        WithWorkingDirectory(workspace.Root, () =>
        {
            Assert.Equal("relatif\n", SystemScripts.Exec(context, "./tool").GetText("standard_output"));
            AssertProcessError(SystemScripts.ExecResult(context, "tool"), "not_found", "tool");
        });
    }

    [Fact]
    public void Bare_program_names_are_searched_only_in_path_never_in_the_current_directory()
    {
        using var workspace = new SystemTestWorkspace();
        SystemScripts.CreateExecutable(workspace, "scripts-local-tool", "#!/bin/sh\necho local\n");
        using var context = NewContext();

        WithWorkingDirectory(workspace.Root, () =>
        {
            foreach (var pathValue in new[] { "/usr/bin:/bin", ".:/usr/bin", "::/usr/bin", "relative:/usr/bin", string.Empty })
            {
                WithEnvironment("PATH", pathValue, () =>
                    AssertProcessError(SystemScripts.ExecResult(context, "scripts-local-tool"), "not_found", "scripts-local-tool"));
            }

            WithEnvironment("PATH", null, () =>
                AssertProcessError(SystemScripts.ExecResult(context, "scripts-local-tool"), "not_found"));
            WithEnvironment("PATH", workspace.Root + ":/usr/bin", () =>
                Assert.Equal("local\n", SystemScripts.Exec(context, "scripts-local-tool").GetText("standard_output")));
        });
    }

    [Fact]
    public void Path_search_takes_the_first_executable_regular_file_in_order_and_skips_others()
    {
        using var workspace = new SystemTestWorkspace();
        SystemScripts.CreateExecutable(workspace, "first/tool", "#!/bin/sh\necho first\n");
        SystemScripts.CreateExecutable(workspace, "second/tool", "#!/bin/sh\necho second\n");
        workspace.Write("noexec/tool", "x");
        File.SetUnixFileMode(workspace.Combine("noexec", "tool"), (UnixFileMode)Convert.ToInt32("644", 8));
        Directory.CreateDirectory(workspace.Combine("dirs", "tool"));
        using var context = NewContext();

        WithEnvironment("PATH", string.Join(':', workspace.Combine("dirs"), workspace.Combine("noexec"), workspace.Combine("first"), workspace.Combine("second")), () =>
            Assert.Equal("first\n", SystemScripts.Exec(context, "tool").GetText("standard_output")));
        WithEnvironment("PATH", string.Join(':', workspace.Combine("dirs"), workspace.Combine("noexec")), () =>
            AssertProcessError(SystemScripts.ExecResult(context, "tool"), "not_found"));
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemExecWithInputTests
{
    [Fact]
    public void Feeds_utf8_input_including_nul_and_closes_stdin()
    {
        using var context = NewContext();

        Assert.Equal("abc", Ok<RecordValue>(RunRequest(context, "cat", [], "abc")).GetText("standard_output"));
        Assert.Equal("é😀\0z", Ok<RecordValue>(RunRequest(context, "cat", [], "é😀\0z")).GetText("standard_output"));
        Assert.Equal(string.Empty, Ok<RecordValue>(RunRequest(context, "cat", [], string.Empty)).GetText("standard_output"));
    }

    [Fact]
    public void Large_input_with_large_output_does_not_deadlock()
    {
        using var context = NewContext();
        var input = new string('z', 6_000_000);

        var outcome = Ok<RecordValue>(RunRequest(context, "cat", [], input));

        Assert.Equal(input, outcome.GetText("standard_output"));
    }

    [Fact]
    public void A_child_that_never_reads_its_input_does_not_fail_the_call()
    {
        using var context = NewContext();

        var outcome = Ok<RecordValue>(RunRequest(context, "true", [], new string('q', 6_000_000)));

        Assert.Equal(0, outcome.GetInteger("exit_code"));
    }

    [Fact]
    public void Reports_launch_failures()
    {
        using var context = NewContext();

        AssertProcessError(RunRequest(context, "scripts-programme-inexistant", [], "x"), "not_found");
        AssertProcessError(RunRequest(context, string.Empty, [], "x"), "invalid_argument");
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemExecWithTimeoutTests
{
    [Fact]
    public void Times_out_a_long_running_child_quickly()
    {
        using var context = NewContext();
        var stopwatch = Stopwatch.StartNew();

        var result = RunRequest(context, "sleep", ["30"], timeoutMilliseconds: 100);

        AssertProcessError(result, "timed_out", "sleep");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public void A_child_ignoring_sigterm_is_killed_after_two_seconds_and_does_not_survive()
    {
        using var workspace = new SystemTestWorkspace();
        var pidFile = workspace.Combine("pid");
        var script = SystemScripts.CreateExecutable(workspace, "stubborn", $"#!/bin/sh\necho $$ > '{pidFile}'\ntrap '' TERM\nexec sleep 30\n");
        using var context = NewContext();
        var stopwatch = Stopwatch.StartNew();

        var result = RunRequest(context, script, [], timeoutMilliseconds: 300);

        AssertProcessError(result, "timed_out");
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6));
        var processIdentifier = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.True(WaitUntil(() => !IsAlive(processIdentifier)));
    }

    [Fact]
    public void A_child_finishing_before_the_deadline_is_ok_even_with_a_huge_timeout()
    {
        using var context = NewContext();

        var quick = Ok<RecordValue>(RunRequest(context, "echo", ["vite"], timeoutMilliseconds: 60_000));
        var huge = Ok<RecordValue>(RunRequest(context, "echo", ["vite"], timeoutMilliseconds: long.MaxValue));

        Assert.Equal("vite\n", quick.GetText("standard_output"));
        Assert.Equal("vite\n", huge.GetText("standard_output"));
    }

    [Fact]
    public void Negative_timeout_is_invalid_argument_before_launching_and_zero_means_no_deadline()
    {
        using var workspace = new SystemTestWorkspace();
        var marker = workspace.Combine("marker");
        var script = SystemScripts.CreateExecutable(workspace, "touch-marker", $"#!/bin/sh\ntouch '{marker}'\n");
        using var context = NewContext();

        AssertProcessError(RunRequest(context, script, [], timeoutMilliseconds: -5), "invalid_argument");
        Assert.False(File.Exists(marker));

        var outcome = Ok<RecordValue>(RunRequest(context, script, [], timeoutMilliseconds: 0));

        Assert.Equal(0, outcome.GetInteger("exit_code"));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void Launch_failures_are_reported_with_their_kind()
    {
        using var context = NewContext();

        AssertProcessError(RunRequest(context, "scripts-missing-program", [], timeoutMilliseconds: 1000), "not_found");
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemExecInheritTests
{
    [Fact]
    public void In_capture_mode_child_streams_are_pumped_in_order_with_io_write_standard_output_line()
    {
        using var context = NewContext(out var captured);

        Call(context, "io.write_standard_output_line", Text("before"));
        Assert.Equal(0, Ok<IntegerValue>(SystemScripts.InheritResult(context, "echo", "middle")).Number);
        Assert.Equal(3, Ok<IntegerValue>(SystemScripts.InheritResult(context, "/bin/sh", "-c", "echo error >&2; exit 3")).Number);
        Call(context, "io.write_standard_output_line", Text("after"));

        Assert.Equal("before\nmiddle\nafter\n", captured.ReadStandardOutput());
        Assert.Equal("error\n", captured.ReadStandardError());
    }

    [Fact]
    public void Capture_mode_decodes_invalid_bytes_and_chunked_multibyte_characters()
    {
        using var context = NewContext(out var captured);

        Ok<IntegerValue>(SystemScripts.InheritResult(context, "printf", "\\xff-\\xf0\\x9f\\x98\\x80-\\xe2\\x82"));

        Assert.Equal("\uFFFD-😀-\uFFFD", captured.ReadStandardOutput());
    }

    [Fact]
    public void Capture_mode_handles_large_simultaneous_output()
    {
        using var context = NewContext(out var captured);

        Ok<IntegerValue>(RunInheritRequest(
            context,
            "/bin/sh",
            ["-c", """(head -c 3000000 /dev/zero | tr '\0' x) & (head -c 3000000 /dev/zero | tr '\0' y >&2) & wait"""]));

        Assert.Equal(3_000_000, captured.ReadStandardOutput().Length);
        Assert.Equal(3_000_000, captured.ReadStandardError().Length);
    }

    [Fact]
    public void Console_mode_returns_the_exit_code_of_a_child_sharing_the_streams()
    {
        using var context = new RuntimeContext();

        Assert.Equal(0, Ok<IntegerValue>(SystemScripts.InheritResult(context, "true")).Number);
        Assert.Equal(3, Ok<IntegerValue>(SystemScripts.InheritResult(context, "/bin/sh", "-c", "exit 3")).Number);
    }

    [Fact]
    public void Reports_launch_failures()
    {
        using var context = NewContext();

        AssertProcessError(SystemScripts.InheritResult(context, "scripts-programme-inexistant"), "not_found");
        AssertProcessError(SystemScripts.InheritResult(context, string.Empty), "invalid_argument");
    }

    [Fact]
    public void Returns_the_exit_code_and_rejects_a_non_empty_standard_input_before_launching()
    {
        using var workspace = new SystemTestWorkspace();
        var marker = workspace.Combine("marker");
        var script = SystemScripts.CreateExecutable(workspace, "touch-marker", $"#!/bin/sh\ntouch '{marker}'\n");
        using var context = NewContext();

        Assert.Equal(0, SystemScripts.Inherit(context, "true").Number);
        Assert.Equal(3, SystemScripts.Inherit(context, "/bin/sh", "-c", "exit 3").Number);
        AssertProcessError(RunInheritRequest(context, script, [], "not-empty"), "invalid_argument", script);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void Honors_its_deadline_and_terminates_the_child()
    {
        using var context = NewContext();
        var stopwatch = Stopwatch.StartNew();

        var result = RunInheritRequest(context, "sleep", ["30"], timeoutMilliseconds: 100);

        AssertProcessError(result, "timed_out", "sleep");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), stopwatch.Elapsed.ToString());
        Assert.Equal(0, context.ChildProcesses.Count);
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemWhichTests
{
    [Fact]
    public void Returns_the_absolute_path_found_in_path()
    {
        var located = Ok<PathValue>(Call("process.find_program_path", Text("sh"))).Text;

        Assert.StartsWith("/", located, StringComparison.Ordinal);
        Assert.EndsWith("/sh", located, StringComparison.Ordinal);
    }

    [Fact]
    public void Honors_path_order_and_ignores_empty_relative_and_non_executable_entries()
    {
        using var workspace = new SystemTestWorkspace();
        SystemScripts.CreateExecutable(workspace, "a/tool", "#!/bin/sh\n");
        SystemScripts.CreateExecutable(workspace, "b/tool", "#!/bin/sh\n");
        workspace.Write("c/tool", "x");
        File.SetUnixFileMode(workspace.Combine("c", "tool"), (UnixFileMode)Convert.ToInt32("644", 8));

        WithEnvironment("PATH", $":relative:{workspace.Combine("c")}:{workspace.Combine("b")}:{workspace.Combine("a")}", () =>
            Assert.Equal(workspace.Combine("b", "tool"), Ok<PathValue>(Call("process.find_program_path", Text("tool"))).Text));
        WithEnvironment("PATH", string.Empty, () => AssertProcessError(Call("process.find_program_path", Text("tool")), "not_found", "tool"));
        WithEnvironment("PATH", null, () => AssertProcessError(Call("process.find_program_path", Text("tool")), "not_found", "tool"));
        WithEnvironment("PATH", "relative", () => AssertProcessError(Call("process.find_program_path", Text("tool")), "not_found", "tool"));
    }

    [Fact]
    public void Programs_with_a_slash_are_tested_as_given_and_made_absolute_without_normalization()
    {
        using var workspace = new SystemTestWorkspace();
        var executable = SystemScripts.CreateExecutable(workspace, "run", "#!/bin/sh\n");
        var plain = workspace.Write("plain", "x");
        File.SetUnixFileMode(plain, (UnixFileMode)Convert.ToInt32("644", 8));

        Assert.Equal(executable, Ok<PathValue>(Call("process.find_program_path", Text(executable))).Text);
        AssertProcessError(Call("process.find_program_path", Text(plain)), "permission_denied", plain);
        AssertProcessError(Call("process.find_program_path", Text(workspace.Root)), "permission_denied");
        AssertProcessError(Call("process.find_program_path", Text(workspace.Combine("missing"))), "not_found");
        AssertProcessError(Call("process.find_program_path", Text(plain + "/below")), "not_found");
        WithWorkingDirectory(workspace.Root, () =>
            Assert.Equal(Path.GetFullPath(workspace.Root) + "/./run", Ok<PathValue>(Call("process.find_program_path", Text("./run"))).Text));
    }

    [Fact]
    public void Invalid_names_are_invalid_argument()
    {
        AssertProcessError(Call("process.find_program_path", Text(string.Empty)), "invalid_argument");
        AssertProcessError(Call("process.find_program_path", Text("a\0b")), "invalid_argument");
        AssertProcessError(Call("process.find_program_path", Text("scripts-programme-inexistant")), "not_found", "scripts-programme-inexistant");
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemProcessMiscTests
{
    [Fact]
    public void Process_id_is_the_current_process()
    {
        Assert.Equal(Environment.ProcessId, Assert.IsType<IntegerValue>(Call("process.identifier")).Number);
    }

    [Fact]
    public void Script_arguments_are_returned_in_order_including_dash_words()
    {
        using var context = NewContext("a b", "--", "-x", string.Empty, "é");

        var list = Assert.IsType<ListValue>(Call(context, "process.arguments"));

        Assert.Equal(["a b", "--", "-x", string.Empty, "é"], list.Items.Select(item => Assert.IsType<StringValue>(item).Text));
        Assert.Empty(Assert.IsType<ListValue>(Call("process.arguments")).Items);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(249)]
    [InlineData(255)]
    public void Exit_script_raises_script_exit_for_codes_0_to_255(int code)
    {
        var exit = Assert.Throws<ScriptExit>(() => Call("process.exit_with_code", Integer(code)));

        Assert.Equal(code, exit.ExitCode);
    }

    [Theory]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public void Exit_script_rejects_other_codes_with_runtime_0016(long code)
    {
        var failure = Assert.Throws<RuntimeFailure>(() => Call("process.exit_with_code", Integer(code)));

        Assert.Equal("SCRIPTS-RUNTIME-0016", failure.Code);
        Assert.Equal(new[] { ("code", code.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("range", "0..255") }, failure.Arguments);
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemRequireSuccessTests
{
    private static RecordValue Result(long exitCode, string stderr, string program) =>
        new("process_result", Integer(exitCode), Text(string.Empty), Text(stderr), Text(program));

    [Fact]
    public void Zero_exit_code_returns_the_receiver_unchanged()
    {
        using var context = NewContext();

        var ok = Ok<RecordValue>(Call(context, "process_result.require_success", Result(0, string.Empty, "prog")));

        Assert.Equal(0, ok.GetInteger("exit_code"));
        Assert.Equal("prog", ok.GetText("program"));
    }

    [Fact]
    public void Non_zero_exit_code_subject_is_the_program_and_message_cites_the_code()
    {
        using var context = NewContext();

        var failure = ErrorRecord(Call(context, "process_result.require_success", Result(1, string.Empty, "false")), "error");

        Assert.Equal("other", failure.GetText("kind"));
        Assert.Equal("false", failure.GetText("subject"));
        Assert.Equal("the program `false` exited with code 1", failure.GetText("message"));
    }

    [Fact]
    public void Non_zero_exit_code_message_appends_the_first_non_blank_stderr_line()
    {
        using var context = NewContext();

        var failure = ErrorRecord(
            Call(context, "process_result.require_success", Result(3, "\n  boom happened  \nsecond line\n", "/bin/sh")),
            "error");

        Assert.Equal("/bin/sh", failure.GetText("subject"));
        Assert.Equal("the program `/bin/sh` exited with code 3: boom happened", failure.GetText("message"));
    }

    [Fact]
    public void A_long_stderr_line_is_truncated()
    {
        using var context = NewContext();

        var failure = ErrorRecord(Call(context, "process_result.require_success", Result(1, new string('x', 200) + "\n", "prog")), "error");

        Assert.Equal("the program `prog` exited with code 1: " + new string('x', 120) + "…", failure.GetText("message"));
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemPipelineRequireSuccessTests
{
    private static RecordValue Pipeline(RuntimeContext context, params RecordValue[] requests) =>
        Ok<RecordValue>(Call(context, "process.pipeline", new ListValue([.. requests])));

    private static Value Require(RuntimeContext context, params RecordValue[] requests) =>
        Call(context, "pipeline_result.require_success", Pipeline(context, requests));

    [Fact]
    public void Every_stage_at_zero_returns_the_receiver_unchanged()
    {
        using var context = NewContext();
        var pipeline = Pipeline(context, Request(context, "true", []), Request(context, "true", []));

        var ok = Ok<RecordValue>(Call(context, "pipeline_result.require_success", pipeline));

        Assert.Same(pipeline, ok);
    }

    [Fact]
    public void An_empty_stage_list_is_ok_and_returns_the_receiver()
    {
        using var context = NewContext();
        var pipeline = new RecordValue("pipeline_result", new ListValue(), Text(string.Empty));

        var ok = Ok<RecordValue>(Call(context, "pipeline_result.require_success", pipeline));

        Assert.Same(pipeline, ok);
    }

    [Fact]
    public void The_first_failing_stage_in_request_order_is_the_subject()
    {
        using var context = NewContext();

        var middle = ErrorRecord(
            Require(context, Request(context, "printf", ["x\n"]), Request(context, "false", []), Request(context, "cat", [])),
            "error");
        var first = ErrorRecord(
            Require(context, Request(context, "false", []), Request(context, "cat", [])),
            "error");

        Assert.Equal("other", middle.GetText("kind"));
        Assert.Equal("false", middle.GetText("subject"));
        Assert.Equal("the program `false` exited with code 1", middle.GetText("message"));
        Assert.Equal("false", first.GetText("subject"));
        Assert.Equal("the program `false` exited with code 1", first.GetText("message"));
    }

    [Fact]
    public void The_message_appends_the_first_non_blank_stderr_line_of_that_stage()
    {
        using var context = NewContext();

        var failure = ErrorRecord(
            Require(context, Request(context, "/bin/sh", ["-c", "echo boom >&2; exit 3"])),
            "error");

        Assert.Equal("/bin/sh", failure.GetText("subject"));
        Assert.Equal("the program `/bin/sh` exited with code 3: boom", failure.GetText("message"));
    }

    [Fact]
    public void A_long_stderr_line_is_truncated_at_120_code_units()
    {
        using var context = NewContext();

        var failure = ErrorRecord(
            Require(context, Request(context, "/bin/sh", ["-c", "printf '%200s' '' | tr ' ' x >&2; exit 3"])),
            "error");

        Assert.Equal("the program `/bin/sh` exited with code 3: " + new string('x', 120) + "…", failure.GetText("message"));
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemUmaskTests
{
    [Theory]
    [InlineData(-1L)]
    [InlineData(0x200L)]
    [InlineData(0x1000L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void Rejects_a_mask_outside_the_permission_bits(long mask) =>
        AssertIoError(Call("process.set_umask", Integer(mask)), "invalid_argument");
}
