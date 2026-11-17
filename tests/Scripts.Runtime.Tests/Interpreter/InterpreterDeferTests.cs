using Scripts.Core.Reporting;
using Scripts.Runtime.Interpreter;
using Scripts.Runtime.Output;
using Scripts.Runtime.Tests.SystemFunctions;

namespace Scripts.Runtime.Tests.InterpreterRuns;

/// <summary>
/// process.defer (DESIGN §1.5): the registered actions run at the end of the run on every termination path, in LIFO
/// order. A RuntimeFailure from an action only replaces a successful run (code 253); it is swallowed after a
/// return/exit_script/error/signal, which keeps its own outcome.
/// </summary>
[Collection(SystemTestCollection.Name)]
public sealed class InterpreterDeferTests
{
    private static ExecutionOutcome Run(string source, out string standardOutput, out string standardError)
    {
        var program = InterpreterHarness.Load(source);
        using var context = RuntimeContext.CreateForCapture([], out var captured, listenForSignals: false);
        var outcome = ProgramInterpreter.Run(program, context);
        context.Shutdown();
        standardOutput = captured.ReadStandardOutput();
        standardError = captured.ReadStandardError();
        return outcome;
    }

    [Fact]
    public void Actions_run_in_reverse_registration_order_after_the_script()
    {
        var outcome = Run(
            "" +
            "process.defer(() => io.write_standard_output_line(\"first\"))\n" +
            "process.defer(() => io.write_standard_output_line(\"second\"))\n" +
            "process.defer(() => io.write_standard_output_line(\"third\"))\n" +
            "io.write_standard_output_line(\"body\")\n",
            out var standardOutput,
            out _);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(RunTermination.Normal, outcome.Termination);
        Assert.Equal("body\nthird\nsecond\nfirst\n", standardOutput);
    }

    [Fact]
    public void Actions_run_after_a_top_level_return()
    {
        var outcome = Run(
            "io.write_standard_output_line(\"before\")\nprocess.defer(() => io.write_standard_output_line(\"after\"))\nreturn 0\n",
            out var standardOutput,
            out _);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(RunTermination.Normal, outcome.Termination);
        Assert.Equal("before\nafter\n", standardOutput);
    }

    [Fact]
    public void Actions_run_before_exit_script_takes_effect()
    {
        var outcome = Run(
            "io.write_standard_output_line(\"before\")\nprocess.defer(() => io.write_standard_output_line(\"after\"))\nprocess.exit_with_code(3)\n",
            out var standardOutput,
            out _);

        Assert.Equal(3, outcome.ExitCode);
        Assert.Equal(RunTermination.ExitScript, outcome.Termination);
        Assert.Equal("before\nafter\n", standardOutput);
    }

    [Fact]
    public void A_failing_action_turns_a_successful_run_into_a_runtime_error()
    {
        var outcome = Run(
            "" +
            "function boom() -> unit {\n    let mutable zero = 0\n    io.write_standard_output_line(\"{1 / zero}\")\n}\n" +
            "process.defer(() => boom())\n" +
            "io.write_standard_output_line(\"body\")\n",
            out var standardOutput,
            out var standardError);

        Assert.Equal(253, outcome.ExitCode);
        Assert.Equal(RunTermination.RuntimeError, outcome.Termination);
        Assert.Equal("SCRIPTS-RUNTIME-0002", outcome.Failure!.Code);
        Assert.Equal("body\n", standardOutput);
        Assert.Contains("SCRIPTS-RUNTIME-0002", standardError, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failing_action_does_not_replace_process_exit_with_code()
    {
        var outcome = Run(
            "" +
            "function boom() -> unit {\n    let mutable zero = 0\n    io.write_standard_output_line(\"{1 / zero}\")\n}\n" +
            "process.defer(() => boom())\n" +
            "process.exit_with_code(3)\n",
            out _,
            out var standardError);

        Assert.Equal(3, outcome.ExitCode);
        Assert.Equal(RunTermination.ExitScript, outcome.Termination);
        Assert.Contains("SCRIPTS-RUNTIME-0002", standardError, StringComparison.Ordinal);
    }

    [Fact]
    public void An_action_that_raises_a_runtime_error_does_not_replace_an_earlier_failure()
    {
        var outcome = Run(
            "" +
            "function boom() -> unit {\n    let mutable zero = 0\n    io.write_standard_output_line(\"{1 / zero}\")\n}\n" +
            "process.defer(() => boom())\n" +
            "let mutable divisor = 0\n" +
            "io.write_standard_output_line(\"{5 % divisor}\")\n",
            out _,
            out _);

        Assert.Equal(253, outcome.ExitCode);
        Assert.Equal("SCRIPTS-RUNTIME-0002", outcome.Failure!.Code);
        // The first failure (the script's) is reported, not the deferred action's.
        Assert.Contains("5 % 0", outcome.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exit_script_raised_by_an_action_is_swallowed_and_the_other_actions_still_run()
    {
        // LIFO: the second action is registered last, so it runs first. A ScriptExit from an action is swallowed (the run
        // already succeeded), and the remaining action still runs; the run keeps its successful outcome.
        var outcome = Run(
            "" +
            "process.defer(() => io.write_standard_output_line(\"first\"))\n" +
            "process.defer(() => process.exit_with_code(4))\n" +
            "io.write_standard_output_line(\"body\")\n",
            out var standardOutput,
            out _);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(RunTermination.Normal, outcome.Termination);
        Assert.Equal("body\nfirst\n", standardOutput);
    }

    [Fact]
    public void Actions_still_run_when_the_script_was_interrupted_by_a_signal()
    {
        var program = InterpreterHarness.Load(
            "" +
            "function cleanup() -> unit {\n    io.write_standard_output_line(\"nettoye\")\n}\n" +
            "process.defer(() => cleanup())\n" +
            "io.write_standard_output_line(\"armed\")\n" +
            "let mutable counter = 0\n" +
            "while true {\n    counter += 1\n}\n");
        var standardOutput = new MemoryOutputChannel();
        var standardError = new MemoryOutputChannel();
        using var context = new RuntimeContext(
            standardOutput,
            standardError,
            [],
            capturesChildStreams: false,
            forceExit: _ => { },
            watchdogDelay: TimeSpan.FromMinutes(10));

        ExecutionOutcome? outcome = null;
        var worker = new Thread(() => outcome = ProgramInterpreter.Run(program, context));
        worker.Start();
        Assert.True(WaitForOutput(standardOutput, "armed"), "the deferred action was not registered");
        context.Interruption.Raise("SIGTERM", 143);
        worker.Join();

        Assert.NotNull(outcome);
        Assert.Equal(143, outcome.ExitCode);
        Assert.Equal(RunTermination.Signal, outcome.Termination);
        Assert.Equal("SCRIPTS-RUNTIME-0018", outcome.Failure!.Code);
        // The signal did not abort the deferred cleanup: it ran despite the pending interruption.
        Assert.Equal("armed\nnettoye\n", standardOutput.ReadText());
    }

    [Fact]
    public void A_subprocess_launched_by_an_action_runs_after_a_signal()
    {
        // Regression (adversarial audit M1): after a signal, the child table used to kill any child created during the
        // drain, so a cleanup that launches a subprocess never completed. The child must run and report its exit code.
        var program = InterpreterHarness.Load(
            "" +
            "function cleanup() -> unit {\n" +
            "    let result = process.run(process.request(\"/bin/true\", []))\n" +
            "    match result {\n        ok(r) => io.write_standard_output_line(\"fils={r.exit_code}\")\n        error(e) => io.write_standard_output_line(\"err {e.kind}\")\n    }\n" +
            "}\n" +
            "process.defer(() => cleanup())\n" +
            "io.write_standard_output_line(\"armed\")\n" +
            "let mutable counter = 0\n" +
            "while true {\n    counter += 1\n}\n");
        var standardOutput = new MemoryOutputChannel();
        using var context = new RuntimeContext(
            standardOutput,
            new MemoryOutputChannel(),
            [],
            capturesChildStreams: false,
            forceExit: _ => { },
            watchdogDelay: TimeSpan.FromMinutes(10));

        ExecutionOutcome? outcome = null;
        var worker = new Thread(() => outcome = ProgramInterpreter.Run(program, context));
        worker.Start();
        Assert.True(WaitForOutput(standardOutput, "armed"), "the deferred action was not registered");
        context.Interruption.Raise("SIGTERM", 143);
        worker.Join();

        Assert.NotNull(outcome);
        Assert.Equal(143, outcome.ExitCode);
        // The subprocess launched by the cleanup ran normally (exit code 0 reported), despite the signal.
        Assert.Equal("armed\nfils=0\n", standardOutput.ReadText());
    }
    private static bool WaitForOutput(MemoryOutputChannel channel, string text)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (channel.ReadText().Contains(text, StringComparison.Ordinal))
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return channel.ReadText().Contains(text, StringComparison.Ordinal);
    }

}
