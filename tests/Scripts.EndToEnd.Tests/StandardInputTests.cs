using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Scripts.EndToEnd.Tests;

/// <summary>
/// Serializes every test that starts a child whose standard input is closed (a file-descriptor test). The child is
/// launched through a <c>/bin/sh</c> helper script that re-executes the scripts binary in place, so the test process never
/// changes its own descriptor table: exactly like a shell redirection, only the child's descriptors are affected.
/// </summary>
[CollectionDefinition(StandardInputTests.CollectionName, DisableParallelization = true)]
public sealed class StandardInputTestsCollection
{
}

/// <summary>End-to-end checks of the script read from the standard input (<c>scripts run -</c>, DESIGN §9).</summary>
[Collection(StandardInputTests.CollectionName)]
public sealed class StandardInputTests
{
    public const string CollectionName = "standard-input";

    // `RunThroughShellScript` starts `/bin/sh script.sh 0<&- …`: the shell has no standard input itself, so `$0` is
    // empty and the binary is `$1`. Each helper closes its descriptor 0, then re-executes scripts in place.
    private static readonly string ClosedStandardInputScript = "exec \"$1\" run - 0<&-\n";

    [Fact]
    public void Run_reads_the_script_from_the_standard_input_and_exits_zero()
    {
        var result = ScriptsProcess.RunWithStandardInput("io.write_standard_output_line(\"hello\")\n", "run", "-");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello\n", result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Theory]
    [InlineData("io.read_standard_input_line()?")]
    [InlineData("io.read_standard_input()?")]
    public void A_script_blocked_in_a_standard_input_read_exits_130_on_sigint(string readCall)
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "blocked-read.scs",
                $"io.write_standard_output_line(\"waiting\")\nlet value = {readCall}\nio.write_standard_output_line(value)\n");
            var startInfo = new ProcessStartInfo("/usr/bin/env")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--default-signal=INT");
            startInfo.ArgumentList.Add(ScriptsProcess.Binary);
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add(script);
            using var process = Process.Start(startInfo)!;
            var output = new StringBuilder();
            var error = new StringBuilder();
            var gate = new object();
            process.OutputDataReceived += (_, eventArgs) => { lock (gate) { output.AppendLine(eventArgs.Data); } };
            process.ErrorDataReceived += (_, eventArgs) => { lock (gate) { error.AppendLine(eventArgs.Data); } };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            Assert.True(WaitForOutput(output, gate, "waiting"), "the script did not reach the blocked read");
            Assert.Equal(0, kill(process.Id, 2));
            Assert.True(process.WaitForExit(10_000), "the script did not end after SIGINT");
            process.WaitForExit();
            Assert.Equal(130, process.ExitCode);
            lock (gate)
            {
                Assert.Contains("SCRIPTS-RUNTIME-0018", error.ToString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool WaitForOutput(StringBuilder output, object gate, string text)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (gate)
            {
                if (output.ToString().Contains(text, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            Thread.Sleep(25);
        }

        lock (gate)
        {
            return output.ToString().Contains(text, StringComparison.Ordinal);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processIdentifier, int signalNumber);

    [Fact]
    public void An_empty_standard_input_is_an_empty_valid_script()
    {
        var run = ScriptsProcess.RunWithStandardInput(string.Empty, "run", "-");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(string.Empty, run.StandardOutput);
        Assert.Equal(string.Empty, run.StandardError);

        var check = ScriptsProcess.RunWithStandardInput(string.Empty, "check", "-");
        Assert.Equal(0, check.ExitCode);
        Assert.Equal(string.Empty, check.StandardOutput);
        Assert.Equal(string.Empty, check.StandardError);
    }

    [Fact]
    public void A_static_error_in_a_standard_input_script_is_reported_in_stdin_with_code_251()
    {
        var result = ScriptsProcess.RunWithStandardInput("let n = \"abc\".size()\n", "run", "-");

        Assert.Equal(251, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("SCRIPTS-TYPE-0008", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("<stdin>:1:", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("let n = \"abc\".size()", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_on_a_valid_standard_input_script_exits_zero_and_is_silent()
    {
        var result = ScriptsProcess.RunWithStandardInput("let n = 1\n", "check", "-");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public void Errors_json_on_check_of_an_invalid_standard_input_script_names_stdin()
    {
        var result = ScriptsProcess.RunWithStandardInput("let n = \"abc\".size()\n", "check", "--errors-json", "-");

        Assert.Equal(251, result.ExitCode);
        var line = result.StandardOutput.TrimEnd('\n');
        Assert.DoesNotContain('\n', line);
        using var document = JsonDocument.Parse(line);
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("<stdin>", document.RootElement.GetProperty("errors")[0].GetProperty("file").GetString());
    }

    [Fact]
    public void Json_out_captures_the_standard_output_of_a_standard_input_script()
    {
        var result = ScriptsProcess.RunWithStandardInput("io.write_standard_output_line(\"hello\")\nreturn 42\n", "run", "--json-out", "-");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(0, document.RootElement.GetProperty("exit_code").GetInt32());
        Assert.Equal("hello\n", document.RootElement.GetProperty("stdout").GetString());
        Assert.Equal(42, document.RootElement.GetProperty("result").GetInt32());
    }

    [Fact]
    public void Script_arguments_after_a_standard_input_script_reach_process_arguments()
    {
        var result = ScriptsProcess.RunWithStandardInput(
            "for argument in process.arguments() {\n    io.write_standard_output_line(argument)\n}\n", "run", "-", "one", "-two", "--three");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("one\n-two\n--three\n", result.StandardOutput);
    }

    [Fact]
    public void Double_dash_before_the_standard_input_script_is_accepted()
    {
        var result = ScriptsProcess.RunWithStandardInput("io.write_standard_output_line(\"dd\")\n", "run", "--", "-");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("dd\n", result.StandardOutput);
    }

    [Fact]
    public void Script_path_of_a_standard_input_script_is_the_empty_path()
    {
        var result = ScriptsProcess.RunWithStandardInput(
            "io.write_standard_output_line(\"[{process.script_path().to_string()}]\")\nio.write_standard_output_line(\"{process.script_path().is_absolute()}\")\n", "run", "-");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("[]\nfalse\n", result.StandardOutput);
    }

    [Fact]
    public void The_loader_consumes_the_standard_input_so_the_script_reads_an_empty_stream()
    {
        var result = ScriptsProcess.RunWithStandardInput(
            "match io.read_standard_input() {\n    ok(text) => io.write_standard_output_line(\"[{text}]\"),\n    error(e) => io.write_standard_output_line(e.kind)\n}\n",
            "run",
            "-");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("[]\n", result.StandardOutput);
    }

    [Fact]
    public void Explain_does_not_accept_the_standard_input()
    {
        var result = ScriptsProcess.RunWithStandardInput("io.write_standard_output_line(\"x\")\n", "explain", "-");

        Assert.Equal(250, result.ExitCode);
        Assert.Contains("SCRIPTS-CLI-0003", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("option `-`", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardOutput);
    }

    [Fact]
    public void Imports_of_a_standard_input_script_resolve_against_the_current_directory()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "lib"));
            ScriptsProcess.WriteScript(
                directory,
                Path.Combine("lib", "a.scs"),
                "public function greet(name: string) -> string {\n    return \"hello {name}\"\n}\n");

            var result = ScriptsProcess.RunInDirectoryWithStandardInput(
                directory, "import \"lib/a.scs\" as a\nio.write_standard_output_line(a.greet(\"Ada\"))\n", "run", "-");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("hello Ada\n", result.StandardOutput);
            Assert.Equal(string.Empty, result.StandardError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_closed_standard_input_is_an_empty_script()
    {
        // DESIGN §9: a closed standard input (`scripts run - <&-`) is an absent stream: an empty script, never CLI-0010.
        // Regression: the .NET host runs its startup before Main and, when descriptor 0 is closed, leaves one of its own
        // files there (measured: /dev/urandom, an endless stream of random bytes, which was then read as the script).
        // scripts restores descriptor 0 to /dev/null at the start of the binary, so the stream is really absent.
        var result = RunWithClosedStandardInput("run", "-");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);

        var check = RunWithClosedStandardInput("check", "-");
        Assert.Equal(0, check.ExitCode);
        Assert.Equal(string.Empty, check.StandardOutput);
        Assert.Equal(string.Empty, check.StandardError);
    }

    [Fact]
    public void Check_on_a_missing_script_under_errors_json_keeps_code_252()
    {
        // Regression: `check --errors-json` used to exit with 251 (the static-error code) for SCRIPTS-CLI-0009, whereas
        // DESIGN §9 / give an unreadable script the code 252 of both load errors.
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var missing = Path.Combine(directory, "absent.scs");
            var result = ScriptsProcess.Run("check", "--errors-json", missing);

            Assert.Equal(252, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.False(document.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("SCRIPTS-CLI-0009", document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ScriptsProcessResult RunWithClosedStandardInput(params string[] arguments)
    {
        // The helper (/bin/sh) closes its own standard input before re-executing scripts: exactly like a person writing
        // `scripts run - <&-`, scripts then starts with a standard input held by nobody else.
        return RunThroughHelper(ClosedStandardInputScript, arguments);
    }

    private static ScriptsProcessResult RunThroughHelper(string helperScript, IReadOnlyList<string> arguments)
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var helper = ScriptsProcess.WriteScript(directory, "stdin-helper.sh", helperScript);
            return ScriptsProcess.RunThroughShellScript(helper, [ScriptsProcess.Binary, .. arguments]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
