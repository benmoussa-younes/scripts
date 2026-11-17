using System.Collections.Concurrent;
using System.Globalization;
using Scripts.Core;
using Scripts.Core.Reporting;

namespace Scripts.Runtime.Interpreter;

/// <summary>One run of a loaded program: constants of every file first, then the statements of the entry script.</summary>
internal sealed partial class ProgramExecution(
    LoadedProgram program,
    RuntimeContext context)
{
    private readonly Dictionary<LoadedFile, FileEnvironment> environments = [];
    private readonly ConcurrentDictionary<Expression, ResolvedStandardCall> standardCallCache = new(ReferenceEqualityComparer.Instance);
    private int deferredDrainRounds;

    /// <summary>
    /// Per-thread interpreter state (call depth, current diagnostic position, function return slot).
    /// <c>parallel</c> runs user lambdas on several threads, but a value observed on one thread must never see
    /// another's in-progress state, so this state — like that of a real call stack — is thread-affine. Emission of
    /// results stays deterministic: the pipeline re-sequences in input order, so nothing observable depends on
    /// which worker produced which item.
    /// </summary>
    [ThreadStatic]
    private static EvaluationState? evaluationState;

    private EvaluationState State => evaluationState ??= new EvaluationState();

    /// <summary>
    /// Clears the per-thread interpreter state. The main interpreter thread does this itself at the start of
    /// <see cref="Execute"/>; a pooled <c>parallel</c> worker must do the same when it starts a call, because
    /// it is reused across calls where a per-call thread used to be created fresh: without this the second call
    /// on the same thread would inherit the previous call's environment and call depth (MAJOR reuse hazard).
    /// </summary>
    internal static void ResetEvaluationState() => evaluationState = null;

    private int callDepth
    {
        get => State.CallDepth;
        set => State.CallDepth = value;
    }

    private FileEnvironment? currentEnvironment
    {
        get => State.CurrentEnvironment;
        set => State.CurrentEnvironment = value;
    }

    private Span currentSpan
    {
        get => State.CurrentSpan;
        set => State.CurrentSpan = value;
    }

    private Value returnValue
    {
        get => State.ReturnValue;
        set => State.ReturnValue = value;
    }

    private sealed class EvaluationState
    {
        public int CallDepth;

        public FileEnvironment? CurrentEnvironment;

        public Span CurrentSpan;

        public Value ReturnValue = UnitValue.Instance;
    }

    /// <summary>Cap on extra rounds of deferred actions registered during the drain (a self-registering action cannot loop).</summary>
    private const int MaximumDeferredDrainRounds = 64;

    public ExecutionOutcome Execute()
    {
        // The entry script's canonical path is an input of the run, fixed once (process.script_path, SPEC-STDLIB §7.3).
        context.ScriptPath = program.Entry.CanonicalPath;
        return RunGuarded(() =>
        {
            evaluationState = new EvaluationState();
            ConstantAnalysis.MarkConstants(program);
            context.FunctionInvoker = InvokeFromStandardLibrary;
            CreateEnvironments();
            EvaluateConstants();
            return ExecuteEntryScript();
        });
    }

    /// <summary>
    /// Runs <paramref name="body"/> and maps every termination of the interpreter to an outcome, then drains the
    /// deferred actions. Shared by the whole run so every path honours the same failure policy.
    /// </summary>
    private ExecutionOutcome RunGuarded(Func<Value?> body)
    {
        ExecutionOutcome outcome;
        try
        {
            outcome = new ExecutionOutcome(ExitCodes.Success, RunTermination.Normal, null, body());
        }
        catch (ScriptExit exit)
        {
            outcome = new ExecutionOutcome(exit.ExitCode, RunTermination.ExitScript, null, null);
        }
        catch (RuntimeFailure failure)
        {
            outcome = RuntimeErrorOutcome(failure.Code, failure.Arguments);
        }
        catch (OutOfMemoryException)
        {
            outcome = RuntimeErrorOutcome(ErrorCodes.Runtime0015, []);
        }
        catch (BrokenPipeTermination)
        {
            outcome = new ExecutionOutcome(ExitCodes.BrokenPipe, RunTermination.Signal, null, null);
        }
        catch (InterruptionTermination interruption)
        {
            var diagnostic = CreateDiagnostic(
                ErrorCodes.Runtime0018,
                ("signal", interruption.SignalName),
                ("exit_code", interruption.ExitCode.ToString(CultureInfo.InvariantCulture)));
            outcome = new ExecutionOutcome(interruption.ExitCode, RunTermination.Signal, diagnostic, null);
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Internal interpreter error: {exception.Message}", exception);
        }

        return DrainDeferredActions(outcome);
    }

    /// <summary>
    /// Runs the actions registered by <c>process.defer</c> (LIFO), on every termination path — normal end, top-level
    /// <c>return</c>, <c>process.exit_with_code(n)</c>, fatal <c>SCRIPTS-RUNTIME</c> error or signal — and returns the outcome of the run.
    /// The run's own outcome always wins, except when it was a success and an action failed: a <c>RuntimeFailure</c> from a
    /// deferred action then turns the run into a runtime error (code 253). A <c>BrokenPipeTermination</c> is recorded on
    /// the context (the CLI maps it to the silent code 141 for a successful run) and the remaining LIFO actions still run;
    /// a <c>ScriptExit</c>/<c>InterruptionTermination</c>/<c>OutOfMemoryException</c> from an action is swallowed and only
    /// logged: it cannot replace a decision the script already made. Actions always run last, after <c>Shutdown()</c>'s
    /// flush, so their output is preserved even when the run itself failed.
    /// </summary>
    private ExecutionOutcome DrainDeferredActions(ExecutionOutcome outcome)
    {
        var actions = context.TakeDeferredActionsInReverseOrder();
        if (actions.Count == 0)
        {
            return outcome;
        }

        // A pending signal would abort every action that touches the stdlib (InvokeFromStandardLibrary -> ThrowIfInterrupted);
        // the initial outcome already carries the signal diagnostic and exit code, so interrupts are suspended for the drain.
        // Children launched by a cleanup must also be allowed to run (the child table would otherwise kill them on sight).
        context.AllowChildrenForDeferredActions();
        using (context.SuspendingInterruptionForDeferredActions())
        {
            var batch = actions;
            while (batch.Count > 0)
            {
                foreach (var action in batch)
                {
                    RunDeferredAction(action, ref outcome);
                }

                // An action may register further deferred actions (like atexit); keep draining. Bounded so a self-
                // registering action cannot loop forever.
                batch = context.TakeDeferredActionsInReverseOrder();
                if (++deferredDrainRounds > MaximumDeferredDrainRounds)
                {
                    break;
                }
            }
        }

        return outcome;
    }

    private void RunDeferredAction(FunctionValue action, ref ExecutionOutcome outcome)
    {
        try
        {
            // No ThrowIfInterrupted at the entry: a deferred cleanup must run even when an interruption is pending.
            InvokeFunction(action, []);
        }
        catch (RuntimeFailure failure)
        {
            LogDeferredActionFailure($"{failure.Code} : {Diagnostics.Create(failure.Code, null, default, failure.Arguments).Message}");
            if (outcome is { ExitCode: ExitCodes.Success, Termination: RunTermination.Normal })
            {
                outcome = RuntimeErrorOutcome(failure.Code, failure.Arguments);
            }
        }
        catch (ScriptExit exit)
        {
            LogDeferredActionFailure($"process.exit_with_code({exit.ExitCode}) ignored");
        }
        catch (BrokenPipeTermination)
        {
            // EPIPE is not a failure (io.write_standard_output/io.write_standard_output_line: silent 141): record it and keep running the LIFO chain.
            context.RecordDeferredBrokenPipe();
        }
        catch (InterruptionTermination interruption)
        {
            LogDeferredActionFailure($"interruption by {interruption.SignalName} ignored");
        }
        catch (OutOfMemoryException)
        {
            LogDeferredActionFailure("out of memory (SCRIPTS-RUNTIME-0015) ignored");
        }
    }

    /// <summary>Best-effort report of a deferred-action failure on stderr; never throws (a closed stderr must not stop the drain).</summary>
    private void LogDeferredActionFailure(string reason)
    {
        try
        {
            context.StandardError.Flush();
            context.StandardError.Write($"scripts: deferred action: {reason}\n");
        }
        catch (Exception)
        {
            // The report itself failed (closed stderr, broken pipe, out of memory): nothing more can be said.
        }
    }

    private static InvalidOperationException InternalError(string message) => new($"Internal interpreter error: {message}");

    private ExecutionOutcome RuntimeErrorOutcome(string code, (string Name, string Value)[] placeholders) =>
        new(ExitCodes.RuntimeFailure, RunTermination.RuntimeError, CreateDiagnostic(code, placeholders), null);

    private Diagnostic CreateDiagnostic(string code, params (string Name, string Value)[] placeholders) =>
        Diagnostics.Create(code, currentEnvironment?.Source, currentSpan, placeholders);

    private void CreateEnvironments()
    {
        foreach (var file in program.AllFilesInLoadOrder())
        {
            environments[file] = new FileEnvironment(file);
        }

        foreach (var environment in environments.Values)
        {
            foreach (var statement in environment.LoadedFile.Program.Statements)
            {
                if (statement is FunctionDeclaration declaration)
                {
                    environment.Functions[declaration.Name] =
                        new UserFunctionValue(declaration.Name, declaration.Parameters, declaration.Body, environment.ConstantScope);
                }
            }

            foreach (var (alias, module) in environment.LoadedFile.Imports)
            {
                environment.Imports[alias] = environments[module];
            }
        }
    }

    private void EvaluateConstants()
    {
        foreach (var file in program.AllFilesInLoadOrder())
        {
            var environment = environments[file];
            foreach (var statement in file.Program.Statements)
            {
                if (statement is LetStatement { IsConstant: true } constant)
                {
                    EvaluateConstant(constant, environment);
                }
            }
        }
    }

    private void EvaluateConstant(LetStatement constant, FileEnvironment environment)
    {
        currentEnvironment = environment;
        currentSpan = constant.Span;
        try
        {
            var value = EvaluateOwned(constant.Initializer, environment.ConstantScope);
            environment.ConstantScope.Declare(constant.Name, new Binding(value, false));
        }
        catch (RuntimeFailure failure)
        {
            var cause = Diagnostics.Create(failure.Code, null, default, failure.Arguments).Message;
            throw new RuntimeFailure(ErrorCodes.Runtime0020, ("name", constant.Name), ("cause_message", cause));
        }
    }

    private Value? ExecuteEntryScript()
    {
        var entryEnvironment = environments[program.Entry];
        var scriptScope = new Scope(entryEnvironment.ConstantScope, entryEnvironment);
        foreach (var statement in program.Entry.Program.Statements)
        {
            if (statement is FunctionDeclaration or LetStatement { IsConstant: true })
            {
                continue;
            }

            var status = ExecuteStatement(statement, scriptScope);
            if (status == ExecutionStatus.Return)
            {
                return TakeReturnValue();
            }

            if (status != ExecutionStatus.Normal)
            {
                throw InternalError("break or continue outside a loop");
            }
        }

        return null;
    }

    private Value TakeReturnValue()
    {
        var value = returnValue;
        returnValue = UnitValue.Instance;
        return value;
    }
}
