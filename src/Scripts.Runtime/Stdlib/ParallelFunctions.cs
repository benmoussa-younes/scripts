using Core.Concurrency;
using Scripts.Core;
using Scripts.Runtime.Parallel;
using Scripts.Runtime.Platform;
using Scripts.Runtime.Processes;

namespace Scripts.Runtime.Stdlib;

/// <summary>
/// The <c>parallel</c> functions (DESIGN §7.10.1): bounded, synchronous, deterministic concurrency.
/// Failures are the unified <c>error</c> with the <c>kind</c> values <c>invalid_worker_count</c>,
/// <c>transform_failed</c> and <c>spawn_failed</c> (no <c>ParallelError</c> type).
/// </summary>
internal static class ParallelFunctions
{
    /// <summary>Closed worker bound of v1.x: outside 1..256 the call is rejected, never clamped.</summary>
    private const int MinimumWorkers = 1;

    private const int MaximumWorkers = 256;

    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["parallel.transform"] = Map;
        table["parallel.run_all"] = ExecAll;
        table["parallel.walk_directory"] = Walk;
    }

    /// <summary>Bounded parallel map, in input order; workers == 1 runs sequentially on the calling thread.</summary>
    private static Value Map(RuntimeContext context, Value[] arguments)
    {
        var items = ArgumentAccess.List(arguments, 0).Items;
        var workers = ArgumentAccess.Integer(arguments, 1);
        var transform = ArgumentAccess.Function(arguments, 2);

        if (!IsWorkerCountValid(workers))
        {
            return Error("invalid_worker_count", string.Empty, WorkerCountMessage(workers));
        }

        if (items.Count == 0)
        {
            return ResultValue.Ok(new ListValue());
        }

        // Snapshot the items once: a worker never observes a mutation of the caller's list (DESIGN §3.6).
        var snapshot = items.ToArray();

        // workers == 1 runs on the calling thread, byte-identical to any larger bound, and pays no pool setup.
        if (workers == 1)
        {
            var sequential = new Value[snapshot.Length];
            for (var index = 0; index < snapshot.Length; index++)
            {
                try
                {
                    sequential[index] = context.Invoke(transform, snapshot[index]);
                }
                catch (Exception exception) when (!IsControlFlow(exception))
                {
                    return Error("transform_failed", string.Empty, FailureMessage(exception));
                }
            }

            return ResultValue.Ok(new ListValue([.. sequential]));
        }

        var results = new Value[snapshot.Length];

        // Claim-one: the failing item must be claimed as soon as a worker is free, whatever its index, so a
        // late failure aborts promptly instead of waiting for a whole chunk (BLOCKER-2).
        var outcome = ParallelPool.Run(
            snapshot.Length,
            (int)workers,
            index => results[index] = context.Invoke(transform, snapshot[index]),
            WorkPartition.ClaimOne);

        if (outcome.FirstFailure is { } failure)
        {
            if (IsControlFlow(failure))
            {
                // Interruption, EPIPE, exit_script and out-of-memory keep their normal path (never swallowed).
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }

            return Error("transform_failed", string.Empty, FailureMessage(failure));
        }

        return ResultValue.Ok(new ListValue([.. results]));
    }

    /// <summary>Bounded parallel exec (the xargs -P answer), in input order; a non-zero exit is a value.</summary>
    private static Value ExecAll(RuntimeContext context, Value[] arguments)
    {
        var requests = ArgumentAccess.List(arguments, 0).Items;
        var workers = ArgumentAccess.Integer(arguments, 1);

        if (!IsWorkerCountValid(workers))
        {
            return Error("invalid_worker_count", string.Empty, WorkerCountMessage(workers));
        }

        if (requests.Count == 0)
        {
            return ResultValue.Ok(new ListValue());
        }

        var snapshot = requests.ToArray();
        var runner = new ParallelChildRunner();

        if (workers == 1)
        {
            var sequential = new Value[snapshot.Length];
            for (var index = 0; index < snapshot.Length; index++)
            {
                try
                {
                    sequential[index] = RunOne(context, runner, snapshot[index]);
                }
                catch (ProcessFailureException processError)
                {
                    return ProcessErrorResult(processError);
                }
            }

            return ResultValue.Ok(new ListValue([.. sequential]));
        }

        var results = new Value[snapshot.Length];

        // The library runs the abort action inside the failing worker (kills the live children so a blocked
        // worker drains at once) and again after the barrier; `runner.KillAll` is idempotent.
        var outcome = ParallelPool.Run(
            snapshot.Length,
            (int)workers,
            index => results[index] = RunOne(context, runner, snapshot[index]),
            WorkPartition.ClaimOne,
            _ => runner.KillAll());

        if (outcome.FirstFailure is { } failure)
        {
            // Belt and braces: nothing survives the call, even a child registered between the abort and the join.
            runner.KillAll();
            if (failure is RuntimeFailure || IsControlFlow(failure))
            {
                // A fatal limit (SCRIPTS-RUNTIME-0013) and a signal or a closed stdout are not spawn failures. They
                // keep their normal termination path, identical to the workers == 1 sequential branch.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }

            return failure is ProcessFailureException processError
                ? ProcessErrorResult(processError)
                : Error("spawn_failed", string.Empty, MessageOf(failure));
        }

        return ResultValue.Ok(new ListValue([.. results]));
    }

    /// <summary>Bounded parallel walk: expansion is parallel, the pre-order fold is sequential and deterministic.</summary>
    private static Value Walk(RuntimeContext context, Value[] arguments)
    {
        var rootPath = ArgumentAccess.PathTextOf(arguments, 0);
        var workers = ArgumentAccess.Integer(arguments, 1);
        var visit = ArgumentAccess.Function(arguments, 2);

        if (!IsWorkerCountValid(workers))
        {
            return Error("invalid_worker_count", string.Empty, WorkerCountMessage(workers));
        }

        try
        {
            var paths = workers == 1
                ? FileSystemQueries.WalkDirectory(rootPath, context.ThrowIfInterrupted)
                : new ParallelWalker((int)workers, context.ThrowIfInterrupted).Run(rootPath);

            // visit runs in pre-order on the calling thread, so its side effects are ordered too.
            var items = new List<Value>(paths.Count);
            foreach (var path in paths)
            {
                try
                {
                    context.Invoke(visit, new PathValue(path));
                }
                catch (Exception exception) when (!IsControlFlow(exception))
                {
                    return Error("transform_failed", string.Empty, FailureMessage(exception));
                }

                items.Add(new PathValue(path));
            }

            return ResultValue.Ok(new ListValue(items));
        }
        catch (IoFailureException failure)
        {
            return SystemValues.IoFailure(failure);
        }
    }

    /// <summary>
    /// True for failures that are not the transform's own: a signal, a closed stdout, <c>exit_script</c> or
    /// out-of-memory. They always keep their normal termination path and are never turned into a result error.
    /// </summary>
    private static bool IsControlFlow(Exception exception) =>
        exception is InterruptionTermination or BrokenPipeTermination or ScriptExit or OutOfMemoryException;

    /// <summary>Runs one child; a child that cannot be started throws ProcessFailureException (the caller maps it).</summary>
    private static Value RunOne(RuntimeContext context, ParallelChildRunner runner, Value request) =>
        runner.Run(context, RunRequestData.FromRecord((RecordValue)request));

    /// <summary>
    /// Maps a request failure: a launch failure stays <c>spawn_failed</c> (subject empty, the message carries the
    /// program), while a malformed request or an expired deadline keeps its own kind and the request's program as subject.
    /// </summary>
    private static ResultValue ProcessErrorResult(ProcessFailureException failure) => failure.Kind switch
    {
        ProcessErrorKinds.InvalidArgument => Error("invalid_argument", failure.Program, failure.Reason),
        ProcessErrorKinds.TimedOut => Error("timed_out", failure.Program, failure.Reason),
        ProcessErrorKinds.InvalidEncoding => Error("invalid_encoding", failure.Program, failure.Reason),
        _ => Error("spawn_failed", string.Empty, failure.UserMessage),
    };

    private static string MessageOf(Exception? failure) => failure switch
    {
        ProcessFailureException process => process.UserMessage,
        null => string.Empty,
        _ => failure.Message,
    };

    private static bool IsWorkerCountValid(long workers) => workers is >= MinimumWorkers and <= MaximumWorkers;

    private static string WorkerCountMessage(long workers) =>
        $"the worker count must be between {MinimumWorkers} and {MaximumWorkers}, got {SystemValues.Invariant(workers)}";

    private static string FailureMessage(Exception failure) =>
        failure is IoFailureException io ? io.UserMessage : failure switch
        {
            ProcessFailureException process => process.UserMessage,
            // A fatal failure inside a worker (assert, division by zero) keeps its rendered message
            // ("assertion failed: …") instead of the bare code (runtime audit #4).
            RuntimeFailure runtime => Diagnostics.Create(runtime.Code, null, default, runtime.Arguments).Message,
            _ => failure.Message,
        };

    private static ResultValue Error(string kind, string subject, string message) =>
        ResultValue.Error(ErrorValues.Error(kind, subject, message));
}
