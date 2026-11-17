using Scripts.Core.Reporting;
using Scripts.Runtime.Interpreter;
using Scripts.Runtime.Output;
using Scripts.Runtime.Tests.SystemFunctions;

namespace Scripts.Runtime.Tests.InterpreterRuns;

/// <summary>Signals, broken pipes, write failures and out-of-memory mapped to outcomes (DESIGN 6, ERRORS SCRIPTS-RUNTIME-0015, -0018, -0019).</summary>
[Collection(SystemTestCollection.Name)]
public sealed class InterpreterTerminationTests
{
    private static RuntimeContext NewContext(ITrackedOutputChannel standardOutput, ITrackedOutputChannel standardError) =>
        new(standardOutput, standardError, [], capturesChildStreams: false, forceExit: _ => { }, watchdogDelay: TimeSpan.FromMinutes(10));

    [Theory]
    [InlineData("SIGINT", 130)]
    [InlineData("SIGTERM", 143)]
    [InlineData("SIGHUP", 129)]
    public void InterruptionBeforeTheFirstStatementIsReportedAtThatStatement(string signalName, int exitCode)
    {
        var program = InterpreterHarness.Load("io.write_standard_output_line(\"never\")\nlet value = 1\n");
        var standardOutput = new MemoryOutputChannel();
        using var context = NewContext(standardOutput, new MemoryOutputChannel());
        context.Interruption.Raise(signalName, exitCode);

        var outcome = ProgramInterpreter.Run(program, context);

        Assert.Equal(exitCode, outcome.ExitCode);
        Assert.Equal(RunTermination.Signal, outcome.Termination);
        Assert.Equal("SCRIPTS-RUNTIME-0018", outcome.Failure!.Code);
        Assert.Equal((1, 1), outcome.Failure.Start);
        Assert.Contains(signalName, outcome.Failure.Message, StringComparison.Ordinal);
        Assert.Contains(exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture), outcome.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardOutput.ReadText());
    }

    [Fact]
    public void InterruptionWhileALoopRunsStopsTheScript()
    {
        var program = InterpreterHarness.Load("let mutable counter = 0\nio.write_standard_output_line(\"armed\")\nwhile true {\n    counter += 1\n}\n");
        var standardOutput = new MemoryOutputChannel();
        using var context = NewContext(standardOutput, new MemoryOutputChannel());
        ExecutionOutcome? outcome = null;
        var worker = new Thread(() => outcome = ProgramInterpreter.Run(program, context));
        worker.Start();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!standardOutput.ReadText().Contains("armed", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        context.Interruption.Raise("SIGINT", 130);
        worker.Join();

        Assert.NotNull(outcome);
        Assert.Equal(130, outcome.ExitCode);
        Assert.Equal(RunTermination.Signal, outcome.Termination);
        Assert.Equal("SCRIPTS-RUNTIME-0018", outcome.Failure!.Code);
        Assert.InRange(outcome.Failure.Start.Line, 3, 4);
    }

    [Fact]
    public void InterruptionWhileACallbackLoopRunsStopsTheScript()
    {
        var program = InterpreterHarness.Load(
            "function spin(item: integer) -> integer {\n    return item\n}\nlet mutable numbers = [0]\nwhile true {\n    numbers = numbers.transform(spin)\n}\n");
        using var context = NewContext(new MemoryOutputChannel(), new MemoryOutputChannel());
        ExecutionOutcome? outcome = null;
        var worker = new Thread(() => outcome = ProgramInterpreter.Run(program, context));
        worker.Start();
        Thread.Sleep(150);

        context.Interruption.Raise("SIGTERM", 143);
        worker.Join();

        Assert.NotNull(outcome);
        Assert.Equal(143, outcome.ExitCode);
        Assert.Equal("SCRIPTS-RUNTIME-0018", outcome.Failure!.Code);
    }

    [Fact]
    public void WriteFailureOnStandardOutputIsCodeNineteenAtTheStatement()
    {
        var program = InterpreterHarness.Load("io.write_standard_output_line(\"ok\")\nio.write_standard_output_line(\"boom\")\n");
        using var context = NewContext(
            new FailingChannel(() => new RuntimeFailure("SCRIPTS-RUNTIME-0019", ("stream", "stdout"), ("reason", "disque plein"))),
            new MemoryOutputChannel());

        var outcome = ProgramInterpreter.Run(program, context);

        Assert.Equal(253, outcome.ExitCode);
        Assert.Equal(RunTermination.RuntimeError, outcome.Termination);
        Assert.Equal("SCRIPTS-RUNTIME-0019", outcome.Failure!.Code);
        Assert.Equal((1, 1), outcome.Failure.Start);
        Assert.Contains("stdout", outcome.Failure.Message, StringComparison.Ordinal);
        Assert.Contains("disque plein", outcome.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RealWriteFailureOnDevFullIsCodeNineteenAtTheFlushingStatement()
    {
        if (!File.Exists("/dev/full"))
        {
            return;
        }

        var program = InterpreterHarness.Load("io.write_standard_output_line(\"x\")\nio.write_standard_output_line(\"y\")\nio.write_standard_error_line(\"z\")\nio.write_standard_output_line(\"never\")\n");
        using var handle = File.OpenHandle("/dev/full", FileMode.Open, FileAccess.Write);
        var standardOutput = new FileDescriptorOutputChannel((int)handle.DangerousGetHandle(), "stdout", brokenPipeEndsSilently: true);
        var standardError = new MemoryOutputChannel();
        using var context = NewContext(standardOutput, standardError);

        var outcome = ProgramInterpreter.Run(program, context);

        Assert.Equal(253, outcome.ExitCode);
        Assert.Equal("SCRIPTS-RUNTIME-0019", outcome.Failure!.Code);
        Assert.Equal((3, 1), outcome.Failure.Start);
        Assert.Contains("stdout", outcome.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ReadText());
    }

    [Fact]
    public void BrokenPipeEndsSilentlyWithCode141()
    {
        var program = InterpreterHarness.Load("io.write_standard_output_line(\"x\")\nio.write_standard_error_line(\"y\")\nio.write_standard_output_line(\"never\")\n");
        using var pipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        pipe.ClientSafePipeHandle.Dispose();
        var standardOutput = new FileDescriptorOutputChannel((int)pipe.SafePipeHandle.DangerousGetHandle(), "stdout", brokenPipeEndsSilently: true);
        using var context = NewContext(standardOutput, new MemoryOutputChannel());

        var outcome = ProgramInterpreter.Run(program, context);

        Assert.Equal(141, outcome.ExitCode);
        Assert.Equal(RunTermination.Signal, outcome.Termination);
        Assert.Null(outcome.Failure);
        Assert.Null(outcome.Result);
    }

    [Fact]
    public void BrokenPipeRaisedByAChannelIsSilent()
    {
        var program = InterpreterHarness.Load("io.write_standard_output_line(\"x\")\n");
        using var context = NewContext(new FailingChannel(() => new BrokenPipeTermination()), new MemoryOutputChannel());

        var outcome = ProgramInterpreter.Run(program, context);

        Assert.Equal(141, outcome.ExitCode);
        Assert.Null(outcome.Failure);
    }

    [Fact]
    public void OutOfMemoryIsCodeFifteenWithExitCode70()
    {
        var program = InterpreterHarness.Load("io.write_standard_output_line(\"first\")\nio.write_standard_output_line(\"second\")\n");
        using var context = NewContext(new FailingChannel(() => new OutOfMemoryException()), new MemoryOutputChannel());

        var outcome = ProgramInterpreter.Run(program, context);

        Assert.Equal(253, outcome.ExitCode);
        Assert.Equal(RunTermination.RuntimeError, outcome.Termination);
        Assert.Equal("SCRIPTS-RUNTIME-0015", outcome.Failure!.Code);
        Assert.Equal((1, 1), outcome.Failure.Start);
    }

    private sealed class FailingChannel(Func<Exception> createFailure) : ITrackedOutputChannel
    {
        public bool IsAtLineStart => true;

        public void Write(string text) => throw createFailure();

        public void Flush()
        {
        }

        public bool TryFlush(TimeSpan timeout) => true;
    }
}
