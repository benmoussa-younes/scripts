using System.Collections.Concurrent;
using Scripts.Runtime.Platform;
using Scripts.Runtime.Processes;

namespace Scripts.Runtime.Parallel;

/// <summary>
/// Runs one child process for <c>parallel.run_all</c> and remembers every live child so an abort can kill
/// them all (DESIGN §7.10.1). A non-zero exit code is a value; a child that cannot be started, a malformed
/// request or an expired deadline is an error.
/// </summary>
internal sealed class ParallelChildRunner
{
    // Every child currently alive; an abort kills them all so a worker blocked on a drain returns at once
    // (its ReadToEnd sees EOF, its WaitForExit returns) instead of waiting for the child's own exit.
    private readonly ConcurrentDictionary<int, ChildProcess> live = new();
    private int nextSlot;

    /// <summary>Runs one request in its own context and returns its <c>process_result</c>; failures are thrown.</summary>
    public RecordValue Run(RuntimeContext context, RunRequestData request)
    {
        if (request.TimeoutMilliseconds < 0)
        {
            throw ProcessFailureException.InvalidArgument(request.Program, "the timeout must not be negative");
        }

        var timeout = ProcessTimings.ToTimeout(request.TimeoutMilliseconds);
        var inputFile = StandardInputFile.Prepare(
            request.Program,
            request.StandardInput,
            request.StandardInputFile,
            context.ThrowIfInterrupted);
        ChildProcess child;
        try
        {
            child = Start(context, request, inputFile?.Descriptor ?? -1);
        }
        finally
        {
            inputFile?.Dispose();
        }

        var slot = Interlocked.Increment(ref nextSlot);
        live[slot] = child;
        try
        {
            // Close the race: if the abort fired while this child was being started, the kill callback missed
            // its slot. Killing it here guarantees no child outlives the call.
            if (Volatile.Read(ref abortRequested) != 0)
            {
                child.Kill();
            }

            var standardOutput = new BufferingSink(child.Kill);
            var standardError = new BufferingSink(child.Kill);
            child.StartPumps(standardOutput, standardError);
            if (request.StandardInputFile.Length == 0)
            {
                FeedAndCloseInput(child, request.StandardInput);
            }

            // Wait for the exit AND the end of both pumps (like process.run), so the captured text is complete. An
            // abort or a signal kills the child; a deadline expiry terminates it and interrupts the call.
            if (!child.WaitForCompletion(context.Interruption, timeout))
            {
                child.Terminate();
                throw ProcessFailureException.TimedOut(request.Program, request.TimeoutMilliseconds);
            }

            child.ThrowIfPumpFailed();
            return new RecordValue(
                "process_result",
                IntegerValue.From(child.ExitCode),
                new StringValue(standardOutput.ReadText(request.Program)),
                new StringValue(standardError.ReadText(request.Program)),
                new StringValue(request.Program));
        }
        finally
        {
            live.TryRemove(slot, out _);
            child.Dispose();
        }
    }

    /// <summary>Kills every live child and its subtree (SIGKILL); called from inside the failing worker and again after the join.</summary>
    public void KillAll()
    {
        Volatile.Write(ref abortRequested, 1);
        foreach (var child in live.Values)
        {
            child.Kill();
        }
    }

    private int abortRequested;

    // The launch logic (working directory, environment overlay, PATH resolution, errno mapping) is shared with
    // process.run so both entry points stay consistent.
    private static ChildProcess Start(RuntimeContext context, RunRequestData request, int inputFileDescriptor) =>
        ProcessOperations.Launch(
            context,
            request.Program,
            request.Arguments,
            redirectInput: true,
            redirectOutputs: true,
            request.LaunchContext,
            inputFileDescriptor);

    private static void FeedAndCloseInput(ChildProcess child, string standardInput)
    {
        if (standardInput.Length == 0)
        {
            child.CloseStandardInput();
            return;
        }

        child.FeedStandardInputAndClose(NativeText.ToBytes(standardInput));
    }
}
