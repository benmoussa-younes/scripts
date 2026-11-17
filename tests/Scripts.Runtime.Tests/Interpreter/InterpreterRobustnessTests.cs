using Scripts.Runtime.Interpreter;

namespace Scripts.Runtime.Tests.InterpreterRuns;

/// <summary>Statically invalid programs (the checker would reject them) must end in an internal error, never in a hang or a foreign exception.</summary>
public sealed class InterpreterRobustnessTests
{
    [Theory]
    [InlineData("let value = 1 + \"a\"\n", "unsupported operands")]
    [InlineData("let value = missing_name\n", "missing_name")]
    [InlineData("let value = missing_function(1)\n", "missing_function")]
    [InlineData("function two(first: integer, second: integer) -> integer {\n    return first\n}\nlet value = two(1)\n", "two")]
    [InlineData("let value = \"text\".no_such_method()\n", "no_such_method")]
    [InlineData("if 1 {\n    io.write_standard_output_line(\"x\")\n}\n", "boolean")]
    [InlineData("let value = [1][\"key\"]\n", "index")]
    [InlineData("let value = 5.field\n", "field")]
    [InlineData("let value = 5(1)\n", "function")]
    [InlineData("for item in 5 {\n    io.write_standard_output_line(\"x\")\n}\n", "list")]
    [InlineData("let value = 3?\n", "result")]
    [InlineData("break\n", "loop")]
    [InlineData("let value = unknown_alias.thing(1)\n", "unknown")]
    public void InvalidProgramsRaiseInternalErrors(string program, string expectedMessageFragment)
    {
        var loaded = InterpreterHarness.Load(program);
        using var context = RuntimeContext.CreateForCapture([], out _, listenForSignals: false);

        var exception = Assert.Throws<InvalidOperationException>(() => ProgramInterpreter.Run(loaded, context));

        Assert.Contains(expectedMessageFragment, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InfiniteRecursionOfAnInvalidProgramStillStopsAtTheDepthLimit()
    {
        var run = InterpreterHarness.RunOnLargeStack("function forever() -> integer {\n    return forever()\n}\nlet value = forever()\n");

        Assert.Equal("SCRIPTS-RUNTIME-0011", run.FailureCode);
    }

    [Fact]
    public void LargeStackThreadReturnsTheValueFromAnotherThread()
    {
        var callerThread = Environment.CurrentManagedThreadId;

        var workerThread = LargeStackThread.Run(() => Environment.CurrentManagedThreadId);

        Assert.NotEqual(callerThread, workerThread);
    }

    [Fact]
    public void LargeStackThreadPropagatesExceptionsToTheCaller()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => LargeStackThread.Run<int>(() => throw new InvalidOperationException("from worker")));

        Assert.Equal("from worker", exception.Message);
    }

    [Fact]
    public void LargeStackThreadSupportsVeryDeepManagedRecursion()
    {
        static int Descend(int remaining) => remaining == 0 ? 0 : 1 + Descend(remaining - 1);

        Assert.Equal(200_000, LargeStackThread.Run(() => Descend(200_000)));
    }

    [Fact]
    public void ConsecutiveRunsOnOneContextAreIndependentPrograms()
    {
        using var context = RuntimeContext.CreateForCapture([], out var captured, listenForSignals: false);
        var first = ProgramInterpreter.Run(InterpreterHarness.Load("let mutable counter = 1\ncounter += 1\nio.write_standard_output_line(\"{counter}\")\n"), context);
        var second = ProgramInterpreter.Run(InterpreterHarness.Load("let mutable counter = 10\ncounter += 1\nio.write_standard_output_line(\"{counter}\")\n"), context);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal("2\n11\n", captured.ReadStandardOutput());
    }
}
