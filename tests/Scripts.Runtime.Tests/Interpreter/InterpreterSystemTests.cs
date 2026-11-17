using Scripts.Runtime.Tests.SystemFunctions;

namespace Scripts.Runtime.Tests.InterpreterRuns;

/// <summary>Programs calling the real system stdlib through the interpreter (shares process state with the system tests: serial collection).</summary>
[Collection(SystemTestCollection.Name)]
public sealed class InterpreterSystemTests
{
    [Fact]
    public void FilesAreWrittenAppendedReadAndRemoved()
    {
        using var workspace = new TemporaryWorkingDirectory();
        const string program = """
            let directory = process.arguments()[0].to_path()
            let target = directory / "note.txt"
            filesystem.write_file_overwrite("first\n", target)?
            filesystem.append_file("second\n", target)?
            let content = filesystem.read_file(target)?
            io.write_standard_output_line("{content.lines().length()} {filesystem.path_exists(target)}")
            let status = filesystem.get_path_status(target)?
            io.write_standard_output_line("{status.kind} {status.size_bytes}")
            filesystem.remove_file(target)?
            io.write_standard_output_line("{filesystem.path_exists(target)}")
            match filesystem.read_file(target) {
                ok(_) => io.write_standard_output_line("unexpected")
                error(failure) => io.write_standard_output_line("{failure.kind}")
            }
            """;

        var run = InterpreterHarness.RunWithArguments(program, [workspace.Path]);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(InterpreterHarness.Lines("2 true", "file 13", "false", "not_found"), run.StandardOutput);
    }

    [Fact]
    public void ExecPassesEveryArgumentVerbatimWithoutReparsing()
    {
        const string program = """
            let hostile = ["a b", "it's \"quoted\"", "two\nlines", "$HOME", "*", "`id`", ";rm -rf /", "-n", ""]
            let outcome = process.run(process.request("/usr/bin/printf", ["[%s]\n"] + hostile))?
            io.write_standard_output_line(outcome.standard_output)
            io.write_standard_output_line("{outcome.exit_code}")
            """;

        var run = InterpreterHarness.Run(program);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(
            InterpreterHarness.Lines("[a b]", "[it's \"quoted\"]", "[two", "lines]", "[$HOME]", "[*]", "[`id`]", "[;rm -rf /]", "[-n]", "[]") + "\n0\n",
            run.StandardOutput);
    }

    [Fact]
    public void ExecWithInputAndNonZeroExitCodesAreValuesNotFailures()
    {
        const string program = """
            let upper = process.run(process.request("tr", ["a-z", "A-Z"]).with_standard_input("hello world\n"))?
            let failed = process.run(process.request("false", []))?
            io.write_standard_output_line("{upper.standard_output.trim_whitespace()} {failed.exit_code}")
            match process.run(process.request("/nonexistent/program", [])) {
                ok(_) => io.write_standard_output_line("started")
                error(failure) => io.write_standard_output_line("{failure.kind}")
            }
            """;

        var run = InterpreterHarness.Run(program);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(InterpreterHarness.Lines("HELLO WORLD 1", "not_found"), run.StandardOutput);
    }

    [Fact]
    public void EnvironmentChangesAreSeenByChildrenAndCanBeSet()
    {
        const string program = """
            environment.set_variable("SCRIPTS_INTERPRETER_TEST", "value with spaces")?
            let child = process.run(process.request("printenv", ["SCRIPTS_INTERPRETER_TEST"]))?
            io.write_standard_output_line(child.standard_output.trim_whitespace())
            environment.set_variable("SCRIPTS_INTERPRETER_PIPED", "piped")?
            io.write_standard_output_line(environment.get_variable("SCRIPTS_INTERPRETER_PIPED").value_or("absent"))
            environment.remove_variable("SCRIPTS_INTERPRETER_TEST")?
            environment.remove_variable("SCRIPTS_INTERPRETER_PIPED")?
            io.write_standard_output_line(environment.get_variable("SCRIPTS_INTERPRETER_TEST").value_or("absent"))
            """;

        var run = InterpreterHarness.Run(program);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(InterpreterHarness.Lines("value with spaces", "piped", "absent"), run.StandardOutput);
    }

    [Fact]
    public void ChangeDirectoryAffectsRelativePathsAndChildren()
    {
        using var workspace = new TemporaryWorkingDirectory();
        const string program = """
            let directory = process.arguments()[0].to_path()
            filesystem.make_directory(directory / "inner")?
            filesystem.set_current_directory(directory / "inner")?
            filesystem.write_file_overwrite("x", p"relative.txt")?
            let listing = process.run(process.request("ls", []))?
            io.write_standard_output_line(listing.standard_output.trim_whitespace())
            io.write_standard_output_line("{(filesystem.get_current_directory()?).base_name()}")
            """;

        var run = InterpreterHarness.RunWithArguments(program, [workspace.Path]);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(InterpreterHarness.Lines("relative.txt", "inner"), run.StandardOutput);
    }

    [Fact]
    public void ExecInheritStreamsAreCapturedInCaptureMode()
    {
        var run = InterpreterHarness.Run("let code = process.run_inherit(process.request(\"echo\", [\"from child\"]))?\nio.write_standard_output_line(\"code {code}\")\n");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(InterpreterHarness.Lines("from child", "code 0"), run.StandardOutput);
    }

    /// <summary>A wrapper propagates a child's 255: the whole 0..255 range is available to a script.</summary>
    [Fact]
    public void Exit_script_propagates_a_child_exit_code_of_255()
    {
        var run = InterpreterHarness.Run("let code = process.run_inherit(process.request(\"/bin/sh\", [\"-c\", \"exit 255\"]))?\nprocess.exit_with_code(code)\n");

        Assert.Equal(255, run.ExitCode);
        Assert.Equal(Scripts.Core.Reporting.RunTermination.ExitScript, run.Outcome.Termination);
        Assert.Null(run.Outcome.Failure);
    }

    [Fact]
    public void SpawnedProcessesAreWaitedFor()
    {
        const string program = """
            let handle = process.spawn("printf", ["spawned %s", "ok"])?
            let finished = process.wait_for_process(handle, 0)?
            io.write_standard_output_line("{finished.standard_output} {finished.exit_code}")
            """;

        var run = InterpreterHarness.Run(program);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("spawned ok 0\n", run.StandardOutput);
    }

    [Fact]
    public void EprintGoesToStandardErrorAfterFlushingStandardOutput()
    {
        var run = InterpreterHarness.Run("io.write_standard_output_line(\"out\")\nio.write_standard_error_line(\"err\")\nio.write_standard_output_line(\"out again\")\n");

        Assert.Equal("out\nout again\n", run.StandardOutput);
        Assert.Equal("err\n", run.StandardError);
    }
}
