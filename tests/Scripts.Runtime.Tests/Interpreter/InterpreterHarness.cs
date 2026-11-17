using Scripts.Core;
using Scripts.Core.Reporting;
using Scripts.Runtime.Interpreter;
using Scripts.Syntax;

namespace Scripts.Runtime.Tests.InterpreterRuns;

/// <summary>Everything a test needs to know about one finished run.</summary>
public sealed record ProgramRun(string StandardOutput, string StandardError, ExecutionOutcome Outcome)
{
    public int ExitCode => Outcome.ExitCode;

    public string? FailureCode => Outcome.Failure?.Code;

    /// <summary>"line:column" of the failure diagnostic.</summary>
    public string FailurePosition => Outcome.Failure is { } failure ? $"{failure.Start.Line}:{failure.Start.Column}" : "none";
}

/// <summary>Runs scripts text through the real parser and loader (no checker), then the interpreter on an in-memory capture context.</summary>
public static class InterpreterHarness
{
    public const string EntryPath = "/main.scs";

    public static LoadedProgram Load(string source, params (string Path, string Text)[] modules)
    {
        var provider = new InMemorySourceProvider().AddText(EntryPath, source);
        foreach (var (path, text) in modules)
        {
            provider.AddText(path, text);
        }

        var loaded = ProgramLoader.Load(EntryPath, provider);
        Assert.True(loaded.Error is null, loaded.Error is null ? string.Empty : $"{loaded.Error.Code} {loaded.Error.Message} at {loaded.Error.Start}");
        return loaded.Program!;
    }

    public static ProgramRun Run(string source, params (string Path, string Text)[] modules) =>
        RunWithArguments(source, [], modules);

    public static ProgramRun RunWithArguments(string source, string[] scriptArguments, params (string Path, string Text)[] modules)
    {
        var program = Load(source, modules);
        using var context = RuntimeContext.CreateForCapture(scriptArguments, out var captured, listenForSignals: false);
        var outcome = ProgramInterpreter.Run(program, context);
        context.Shutdown();
        return new ProgramRun(captured.ReadStandardOutput(), captured.ReadStandardError(), outcome);
    }

    /// <summary>Runs on a large-stack thread (needed for call depths close to 10000).</summary>
    public static ProgramRun RunOnLargeStack(string source, params (string Path, string Text)[] modules) =>
        LargeStackThread.Run(() => Run(source, modules));

    /// <summary>Output of a program that must succeed with exit code 0.</summary>
    public static string Output(string source, params (string Path, string Text)[] modules)
    {
        var run = Run(source, modules);
        Assert.True(
            run.ExitCode == 0 && run.Outcome.Failure is null,
            $"exit {run.ExitCode} {run.Outcome.Failure?.Code} {run.Outcome.Failure?.Message} at {run.FailurePosition}");
        Assert.Equal(RunTermination.Normal, run.Outcome.Termination);
        return run.StandardOutput;
    }

    public static ProgramRun AssertFailure(string source, string expectedCode, string expectedPosition, params (string Path, string Text)[] modules)
    {
        var run = Run(source, modules);
        Assert.Equal(expectedCode, run.FailureCode);
        Assert.Equal(expectedPosition, run.FailurePosition);
        Assert.Equal(ExitCodes.RuntimeFailure, run.ExitCode);
        Assert.Equal(RunTermination.RuntimeError, run.Outcome.Termination);
        return run;
    }

    /// <summary>Joins the lines with LF, the way print terminates each line.</summary>
    public static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + "\n"));
}
