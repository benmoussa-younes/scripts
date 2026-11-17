using Scripts.Runtime.Platform;
using Scripts.Runtime.Stdlib;

namespace Scripts.Runtime.Processes;

/// <summary>One stage of <c>process.pipeline</c>/<c>process.pipeline_to_file_overwrite</c>: the request record flattened for the launcher.</summary>
internal sealed record PipelineStage(
    string Program,
    IReadOnlyList<string> Arguments,
    string StandardInput,
    string StandardInputFile,
    ProcessLaunchContext LaunchContext,
    long TimeoutMilliseconds);

/// <summary>
/// The pipeline module functions (SPEC-STDLIB §7.10): starts every stage, pumps raw bytes
/// from one stage's stdout to the next stage's stdin, then delivers the last stage's stdout to its destination — a
/// scratch file renamed over a path (<c>pipeline_to_file_overwrite</c>) or memory (<c>pipeline</c>). Launch order,
/// the EPIPE cascade, the per-stage deadlines and the signal handling are shared; only the last stage's sink and
/// the shape of the ok payload differ. A non-zero exit code is data.
/// </summary>
internal static class ProcessPipeline
{
    /// <summary>
    /// <c>process.pipeline_to_file_overwrite</c>: the last stage's stdout replaces <paramref name="destinationPath"/>
    /// atomically, and the result is one <c>process_result</c> per request.
    /// </summary>
    public static Value RunToFile(RuntimeContext context, IReadOnlyList<PipelineStage> requests, string destinationPath) =>
        Run(context, requests, () =>
        {
            var target = FileContentOperations.AtomicFileTarget.Create(destinationPath);
            return new FileDestination(target, destinationPath);
        });

    /// <summary>
    /// <c>process.pipeline</c>: the last stage's stdout is captured in memory and decoded strictly, and the result is
    /// a <c>pipeline_result</c>.
    /// </summary>
    public static Value RunToMemory(RuntimeContext context, IReadOnlyList<PipelineStage> requests) =>
        Run(context, requests, static () => new MemoryDestination());

    /// <summary>
    /// The shared core. The destination is created only once the request list is known to be non-empty, so an empty
    /// pipeline is <c>invalid_argument</c> before any destination path is touched.
    /// </summary>
    private static Value Run(RuntimeContext context, IReadOnlyList<PipelineStage> requests, Func<Destination> createDestination)
    {
        if (requests.Count == 0)
        {
            return ResultValue.Error(ErrorValues.Error(
                "invalid_argument",
                string.Empty,
                "the pipeline must contain at least one request"));
        }

        Destination destination;
        try
        {
            destination = createDestination();
        }
        catch (IoFailureException failure)
        {
            return SystemValues.IoFailure(failure);
        }

        var children = new List<ChildProcess>(requests.Count);
        var standardErrors = new List<BufferingSink>(requests.Count);
        var launchedAt = new List<long>(requests.Count);
        var timeouts = new List<long>(requests.Count);

        try
        {
            for (var index = 0; index < requests.Count; index++)
            {
                var stage = requests[index];
                if (stage.TimeoutMilliseconds < 0)
                {
                    throw ProcessFailureException.InvalidArgument(stage.Program, "the timeout must not be negative");
                }

                // The file input applies to the first stage only: every later stage reads the previous stage's
                // stdout through a pipe, so its own standard_input/standard_input_file fields are ignored.
                var inputFile = index == 0
                    ? StandardInputFile.Prepare(stage.Program, stage.StandardInput, stage.StandardInputFile, context.ThrowIfInterrupted)
                    : null;
                try
                {
                    children.Add(ProcessOperations.Launch(
                        context,
                        stage.Program,
                        stage.Arguments,
                        redirectInput: true,
                        redirectOutputs: true,
                        stage.LaunchContext,
                        inputFile?.Descriptor ?? -1));
                }
                finally
                {
                    inputFile?.Dispose();
                }

                launchedAt.Add(System.Diagnostics.Stopwatch.GetTimestamp());
                timeouts.Add(stage.TimeoutMilliseconds);
            }

            for (var index = 0; index < children.Count; index++)
            {
                var child = children[index];
                var standardError = new BufferingSink(child.Kill);
                standardErrors.Add(standardError);
                child.StartPumps(
                    index + 1 < children.Count
                        ? new PipelineLinkSink(child, children[index + 1])
                        : destination.CreateSink(() => KillAll(children)),
                    standardError);
            }

            // The first stage's fd 0 is already the opened file: there is no pipe to feed or close.
            if (requests[0].StandardInputFile.Length == 0)
            {
                if (requests[0].StandardInput.Length == 0)
                {
                    children[0].CloseStandardInput();
                }
                else
                {
                    children[0].FeedStandardInputAndClose(NativeText.ToBytes(requests[0].StandardInput));
                }
            }

            if (!WaitForStageCompletion(context, children, launchedAt, timeouts, out var expiredStage))
            {
                // A stage deadline expires (exit or pump drain): terminate that stage, kill the whole pipeline
                // (SIGKILL) and leave the destination untouched. No unbounded pump wait follows: a descendant that
                // still holds the pipe open must not outlive the deadline. The subject is that stage's program.
                children[expiredStage].Terminate();
                KillAll(children);
                return ResultValue.Error(ErrorValues.Error(
                    "timed_out",
                    requests[expiredStage].Program,
                    ProcessFailureException.TimedOut(requests[expiredStage].Program, timeouts[expiredStage]).Reason));
            }

            foreach (var child in children)
            {
                // A waitpid failure would leave ExitCode at its default 0: never report a silent success (audit F7).
                child.ThrowIfWaitFailed();
                child.ThrowIfPumpFailed();
            }

            if (destination.WriteFailure is { } writeFailure)
            {
                return writeFailure;
            }

            var results = new List<Value>(children.Count);
            for (var index = 0; index < children.Count; index++)
            {
                results.Add(new RecordValue(
                    "process_result",
                    IntegerValue.From(children[index].ExitCode),
                    new StringValue(string.Empty),
                    new StringValue(standardErrors[index].ReadText(requests[index].Program)),
                    new StringValue(requests[index].Program)));
            }

            // Every stage completed and its pumps reached EOF: this is a no-op (the streams are released by
            // TryRelease). On failure the pumps may still be blocked on a detached descendant; Dispose stops them
            // (P8e) instead of blocking this thread.
            foreach (var child in children)
            {
                child.StopReadingPumps();
            }

            // Complete only after every stage has completed and the results are built: a fatal failure or a
            // signal before this point leaves the file destination untouched.
            return destination.Complete(results, requests);
        }
        catch (ProcessFailureException failure)
        {
            KillAll(children);

            // Argument validation (empty program, NUL in an argument) keeps its kind; a launch
            // failure becomes spawn_failed, whose message carries the cause once (no duplicated subject).
            return failure.Kind is ProcessErrorKinds.InvalidArgument or ProcessErrorKinds.InvalidEncoding or ProcessErrorKinds.TimedOut
                ? SystemValues.ProcessFailure(failure)
                : ResultValue.Error(ErrorValues.Error("spawn_failed", failure.Program, failure.Reason));
        }
        catch (IoFailureException failure)
        {
            KillAll(children);
            return SystemValues.IoFailure(failure);
        }
        catch
        {
            KillAll(children);
            throw;
        }
        finally
        {
            destination.Dispose();
            foreach (var child in children)
            {
                // Each stage releases its own captured pipes once its pumps reach EOF (`TryRelease`); on failure
                // Dispose asks the poll-based pumps to stop, so a detached writer cannot keep the read ends.
                child.Dispose();
            }
        }
    }

    /// <summary>
    /// Waits until every stage is complete — the direct process exited AND its output pumps reached EOF — watching
    /// each stage's own deadline measured from its successful launch. A descendant can keep stdout open after the
    /// direct child exits, so the exit signal alone is not enough. Returns false and the stage index when a deadline
    /// expires; the caller terminates that stage and the pipeline. Waits are chunked to fit
    /// <see cref="WaitHandle.WaitAny(WaitHandle[], TimeSpan)"/> and values beyond <see cref="TimeSpan.MaxValue"/> mean
    /// no deadline, exactly like <c>process.run</c>.
    /// </summary>
    private static bool WaitForStageCompletion(
        RuntimeContext context,
        List<ChildProcess> children,
        List<long> launchedAt,
        List<long> timeouts,
        out int expiredStage)
    {
        while (true)
        {
            context.Interruption.ThrowIfInterrupted();
            var waitHandles = new List<WaitHandle> { context.Interruption.WaitHandle };
            TimeSpan? nearest = null;
            var allComplete = true;
            for (var index = 0; index < children.Count; index++)
            {
                var child = children[index];
                if (!child.HasExited)
                {
                    waitHandles.Add(child.ExitSignal.WaitHandle);
                }

                if (!child.PumpsCompleted.IsSet)
                {
                    waitHandles.Add(child.PumpsCompleted.WaitHandle);
                }

                if (child.HasExited && child.PumpsCompleted.IsSet)
                {
                    continue;
                }

                allComplete = false;
                if (ProcessTimings.ToTimeout(timeouts[index]) is not { } timeout)
                {
                    continue;
                }

                var remaining = timeout - System.Diagnostics.Stopwatch.GetElapsedTime(launchedAt[index]);
                if (remaining <= TimeSpan.Zero)
                {
                    expiredStage = index;
                    return false;
                }

                if (nearest is null || remaining < nearest)
                {
                    nearest = remaining;
                }
            }

            if (allComplete)
            {
                expiredStage = -1;
                return true;
            }

            var chunk = nearest is null ? Timeout.InfiniteTimeSpan : ProcessTimings.ClampWaitChunk(nearest.Value);
            WaitHandle.WaitAny([.. waitHandles], chunk);
        }
    }

    private static void KillAll(List<ChildProcess> children)
    {
        foreach (var child in children)
        {
            child.Kill();
        }
    }

    /// <summary>
    /// Where the last stage's raw stdout goes — the only axis that separates the two pipeline functions. The core
    /// owns the launch, the EPIPE cascade and the deadlines; a destination owns its sink, its write-failure mapping
    /// and the final payload.
    /// </summary>
    private abstract class Destination : IDisposable
    {
        /// <summary>The sink for the last stage's stdout; <paramref name="abort"/> terminates the pipeline on a write failure.</summary>
        public abstract IByteSink CreateSink(Action abort);

        /// <summary>The error to return for a write failure recorded by the sink, or null when there is none.</summary>
        public virtual ResultValue? WriteFailure => null;

        /// <summary>
        /// Builds the call's ok payload once every stage has completed and every result is known. A file destination
        /// commits its scratch file here, so a failure or a signal before this point leaves the path untouched.
        /// </summary>
        public abstract Value Complete(List<Value> stageResults, IReadOnlyList<PipelineStage> requests);

        public virtual void Dispose()
        {
        }
    }

    /// <summary>Streams the last stage's raw stdout into the atomic scratch file, renamed over the path on commit.</summary>
    private sealed class FileDestination(FileContentOperations.AtomicFileTarget target, string destinationPath) : Destination
    {
        private AtomicFileSink? sink;

        public override IByteSink CreateSink(Action abort) => sink = new AtomicFileSink(target, abort);

        public override ResultValue? WriteFailure
        {
            get
            {
                if (sink?.Failure is not { } failure)
                {
                    return null;
                }

                return failure is IoFailureException ioFailure
                    ? SystemValues.IoFailure(ioFailure)
                    : ResultValue.Error(ErrorValues.Error("other", destinationPath, failure.Message));
            }
        }

        public override Value Complete(List<Value> stageResults, IReadOnlyList<PipelineStage> requests)
        {
            try
            {
                target.Commit();
            }
            catch (IoFailureException failure)
            {
                return SystemValues.IoFailure(failure);
            }

            return ResultValue.Ok(new ListValue(stageResults));
        }

        public override void Dispose() => target.Dispose();
    }

    /// <summary>
    /// Captures the last stage's raw stdout in memory. Nothing is written to the filesystem, so a write never fails;
    /// the strict UTF-8 decoding (2^28 units, invalid bytes → <c>invalid_encoding</c>) happens at the end, once the
    /// bytes are complete.
    /// </summary>
    private sealed class MemoryDestination : Destination
    {
        private BufferingSink? sink;

        public override IByteSink CreateSink(Action abort) => sink = new BufferingSink(abort);

        public override Value Complete(List<Value> stageResults, IReadOnlyList<PipelineStage> requests)
        {
            // ReadText throws `invalid_encoding` (subject: the last stage's program) on invalid UTF-8 and the fatal
            // SCRIPTS-RUNTIME-0013 beyond 2^28 units, as DESIGN §6 requires of every captured stream.
            var standardOutput = sink!.ReadText(requests[^1].Program);
            return ResultValue.Ok(new RecordValue(
                "pipeline_result",
                new ListValue(stageResults),
                new StringValue(standardOutput)));
        }
    }

    /// <summary>
    /// Writes stage i's raw stdout to stage i+1's stdin. On the first EPIPE (downstream stopped reading), the
    /// upstream stage is killed (best effort) and the sink tells the stdout pump to stop and close its end: a detached
    /// descendant that holds the pipe open can ignore signals, but it must not keep the pipeline draining forever.
    /// The stderr pump keeps running: every stage's captured stderr is part of the result and must reach EOF.
    /// </summary>
    private sealed class PipelineLinkSink(ChildProcess upstream, ChildProcess downstream) : IByteSink
    {
        private bool stopped;

        public bool Append(ReadOnlySpan<byte> bytes)
        {
            if (stopped)
            {
                return false;
            }

            try
            {
                downstream.StandardInputBaseStream.Write(bytes);
                return true;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                stopped = true;
                upstream.Kill();
                // Stop only the pump feeding the dead link: the upstream's stderr was already written and the result
                // must carry it to EOF. A detached writer that keeps stderr open is covered by the stage deadline.
                upstream.StopReadingStandardOutputPump();
                return false;
            }
        }

        public void Complete()
        {
            if (!stopped)
            {
                downstream.CloseStandardInput();
            }
        }
    }

    /// <summary>Streams the last stage's raw stdout into the atomic scratch file; a write failure kills the pipeline.</summary>
    private sealed class AtomicFileSink(FileContentOperations.AtomicFileTarget target, Action abort) : IByteSink
    {
        public Exception? Failure { get; private set; }

        public bool Append(ReadOnlySpan<byte> bytes)
        {
            if (Failure is not null)
            {
                return false;
            }

            try
            {
                target.Append(bytes);
                return true;
            }
            catch (Exception exception)
            {
                Failure = exception;
                abort();
                return false;
            }
        }

        public void Complete()
        {
            // The destination is committed by the caller, after every stage has completed and the
            // results are built, so a fatal failure or a signal cannot publish a half-valid run.
        }
    }
}
