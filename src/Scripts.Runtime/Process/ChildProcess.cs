using System.Diagnostics;
using System.Runtime.InteropServices;
using Scripts.Runtime.Platform;
using Scripts.Runtime.Signals;

namespace Scripts.Runtime.Processes;

/// <summary>A started child process: exit tracking on a waiter thread, optional output pumps, signal helpers.</summary>
internal sealed class ChildProcess : IDisposable
{
    private readonly ChildProcessTable table;
    private readonly string program;
    private readonly StreamWriter? standardInput;
    private readonly Stream? standardOutput;
    private readonly Stream? standardError;
    private readonly ManualResetEventSlim exited = new(false);
    private readonly ManualResetEventSlim pumpsCompleted = new(false);
    private readonly List<OutputPump> pumps = [];
    private OutputPump? standardOutputPump;
    private readonly object disposalGate = new();
    private readonly int processGroup;
    private int exitCode;
    private int waitError;
    private int released;
    private int streamsReleased;
    private int eventsDisposed;
    private bool pumpsStarted;
    private bool noPumpsExpected;

    private ChildProcess(SpawnedChild spawned, string program, ChildProcessTable table, int processGroup)
    {
        this.table = table;
        this.program = program;
        this.processGroup = processGroup;
        ProcessIdentifier = spawned.ProcessIdentifier;
        standardInput = spawned.StandardInput;
        standardOutput = spawned.StandardOutput;
        standardError = spawned.StandardError;
    }

    public int ProcessIdentifier { get; }

    public bool HasExited => exited.IsSet;

    public int ExitCode => exitCode;

    public ManualResetEventSlim ExitSignal => exited;

    /// <summary>Set once every started output pump has reached EOF (and failed, if it did); a pipeline deadline covers it.</summary>
    public ManualResetEventSlim PumpsCompleted => pumpsCompleted;

    /// <summary>The child's standard input as a raw byte stream (pipeline links write bytes here, never text).</summary>
    public Stream StandardInputBaseStream => standardInput!.BaseStream;

    /// <summary>
    /// Starts the child through <c>posix_spawn</c> and registers it for signal-driven termination. The own process
    /// group is created by the spawn attributes before <c>exec</c> (P8d), so <c>killpg</c> is atomic; it is false for
    /// process.run_inherit, which shares scripts's terminal and therefore must keep scripts's process group.
    /// </summary>
    public static ChildProcess Start(SpawnSpec spec, ChildProcessTable table, bool ownProcessGroup)
    {
        var errno = PosixSpawn.TryLaunch(spec, out var spawned);
        if (errno != 0)
        {
            throw ProcessFailureException.FromErrno(errno, spec.Program);
        }

        var child = new ChildProcess(
            spawned!,
            spec.Program,
            table,
            ownProcessGroup ? spawned!.ProcessIdentifier : 0);
        table.Register(child);
        new Thread(child.WaitForExit) { IsBackground = true, Name = "scripts-child-exit" }.Start();
        return child;
    }

    public void StartPumps(IByteSink standardOutputSink, IByteSink standardErrorSink)
    {
        standardOutputPump = new OutputPump(standardOutput!, standardOutputSink, "scripts-stdout-pump");
        pumps.Add(standardOutputPump);
        pumps.Add(new OutputPump(standardError!, standardErrorSink, "scripts-stderr-pump"));
        foreach (var pump in pumps)
        {
            pump.Start();
        }

        // Set only once the pumps are running: a child that exits earlier must not let TryRelease dispose the
        // pipes before their readers exist (the `parallel.run_all` immediate-child race).
        pumpsStarted = true;

        if (pumps.Count == 0)
        {
            pumpsCompleted.Set();
            return;
        }

        // One lightweight observer publishes the combined EOF signal once every pump has completed. A pipeline
        // deadline must cover the drain (a descendant can hold stdout open after the direct child exited).
        new Thread(() =>
        {
            foreach (var pump in pumps)
            {
                pump.Completed.Wait();
            }

            pumpsCompleted.Set();
            TryRelease();
            // The observer is the only waiter on the per-pump events; releasing them here closes the lazy
            // WaitHandle descriptors that a failed pipeline otherwise leaks until the GC (runtime audit #1).
            foreach (var pump in pumps)
            {
                pump.Dispose();
            }
        }) { IsBackground = true, Name = "scripts-pumps-completed" }.Start();
    }

    /// <summary>
    /// Asks both poll-based pumps to stop at their next tick (P8e), so a detached writer cannot pin the read ends.
    /// Used by <see cref="Dispose"/> after a failed or aborted call; the observer then releases the streams.
    /// </summary>
    public void StopReadingPumps()
    {
        // Ask the poll-based pumps to stop at their next tick; the observer then releases the pipes. Do not close a
        // descriptor under a blocked read: the old force-close could leave the read on a reusable fd number.
        foreach (var pump in pumps)
        {
            pump.Stop();
        }
    }

    /// <summary>
    /// Asks only the stdout pump to stop at its next tick and leaves the stderr pump to drain to EOF: a pipeline link
    /// whose downstream stopped reading has already seen the upstream's diagnostics, which are part of the result.
    /// </summary>
    public void StopReadingStandardOutputPump() => standardOutputPump?.Stop();

    public void CloseStandardInput()
    {
        if (standardInput is null)
        {
            // The launch wired a file descriptor to fd 0: there is no pipe to close.
            return;
        }

        try
        {
            standardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The child already closed its end, or the run already released the pipe.
        }
    }

    /// <summary>Feeds the bytes from a dedicated thread then closes the pipe; EPIPE (child stopped reading) is ignored.</summary>
    public void FeedStandardInputAndClose(byte[] content) =>
        new Thread(() =>
        {
            try
            {
                standardInput!.BaseStream.Write(content);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // EPIPE: the child closed its input before reading everything, or the run already released the pipe.
            }

            CloseStandardInput();
        }) { IsBackground = true, Name = "scripts-stdin-writer" }.Start();

    /// <summary>
    /// Waits for the child and for EOF on both captured streams, under the same deadline (DESIGN §6): the captured
    /// text is complete only at EOF, so a bounded drain would silently truncate a legitimate writer. A descendant that
    /// keeps a pipe open can therefore hold the call until its own end or the deadline; expiry is <c>timed_out</c>
    /// with partial output lost, exactly as the spec states. Throws InterruptionTermination on signal.
    /// </summary>
    public bool WaitForCompletion(InterruptionMonitor monitor, TimeSpan? timeout)
    {
        var started = Stopwatch.GetTimestamp();
        TimeSpan? Remaining() => timeout is { } limit ? limit - Stopwatch.GetElapsedTime(started) : null;

        if (!InterruptibleWait.Wait(exited, monitor, Remaining()))
        {
            return false;
        }

        ThrowIfWaitFailed();
        foreach (var pump in pumps)
        {
            if (!InterruptibleWait.Wait(pump.Completed, monitor, Remaining()))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Waits for the <b>direct child only</b>, which is all that <c>process.run_inherit</c> reports (its exit code): in
    /// normal mode it inherits scripts's streams, so there is nothing else to wait for; under capture the pumps are the
    /// child's pipes and a descendant that keeps them open would otherwise block the call until the descendant's own
    /// end (measured: 120 s for a detached <c>sleep</c>). The pumps keep running during the bounded drain; the
    /// caller's <see cref="Dispose"/> stops them after the budget.
    /// </summary>
    public bool WaitForExitOnly(InterruptionMonitor monitor, TimeSpan? timeout)
    {
        if (!InterruptibleWait.Wait(exited, monitor, timeout))
        {
            return false;
        }

        ThrowIfWaitFailed();
        return true;
    }

    /// <summary>
    /// Lets the capture pumps finish what the direct child already wrote, bounded by <paramref name="budget"/>: a
    /// descendant that keeps the pipe open must not hang the call, but the last bytes of the child must not be lost
    /// either. Returns once every pump is done or the budget is spent; the pumps keep running in the background and a
    /// later read sees whatever they still append.
    /// </summary>
    public void DrainPumpsWithin(TimeSpan budget)
    {
        if (pumps.Count == 0 || budget <= TimeSpan.Zero)
        {
            return;
        }

        // One combined event: the per-pump events are released by the observer as soon as both pumps reach EOF.
        pumpsCompleted.Wait(budget);
    }

    public void ThrowIfPumpFailed()
    {
        foreach (var pump in pumps)
        {
            if (pump.Failure is { } failure)
            {
                throw failure;
            }
        }
    }

    /// <summary>
    /// Sends SIGTERM to the child or to the descendants that outlived it; returns 0 when nothing remains or the errno
    /// of the underlying call. The direct child having exited does not mean the subtree is gone (audit F4).
    /// </summary>
    public int RequestTermination() => HasRemainingTree() ? SignalTermination(Libc.SignalTerminate) : 0;

    /// <summary>SIGTERM to the subtree, then SIGKILL after the grace period if anything still belongs to it.</summary>
    public void Terminate()
    {
        if (!HasRemainingTree())
        {
            return;
        }

        SignalTermination(Libc.SignalTerminate);
        var deadline = DateTime.UtcNow + ProcessTimings.KillGracePeriod;
        while (DateTime.UtcNow < deadline && HasRemainingTree())
        {
            Thread.Sleep(25);
        }

        KillRemainingTree();
        exited.Wait(ProcessTimings.KillGracePeriod);
    }

    /// <summary>
    /// Sends a signal to the child and its descendants. The /proc walk runs first, deepest first, so a descendant that
    /// called <c>setsid</c> is reached while its parent is alive; then <c>killpg</c> (own group, P8d) reaches the group
    /// atomically, including members already reparented. Without a group (<c>run_inherit</c>, both modes) only the
    /// walk and the direct child are used. The own group of scripts is never targeted.
    /// </summary>
    public int SignalTermination(int signalNumber)
    {
        // Walk first, while the parent chain is still intact: a descendant that called setsid has left the group
        // but is still reachable through /proc (DESIGN §6). The group signal then covers the members that have
        // already been reparented, atomically.
        var errno = ProcessTree.SignalDescendants(ProcessIdentifier, signalNumber);
        if (processGroup != 0)
        {
            if (Libc.KillProcessGroup(processGroup, signalNumber) == 0)
            {
                return errno;
            }

            var groupError = Marshal.GetLastPInvokeError();
            return groupError == Errno.NoSuchProcess ? errno : groupError;
        }

        if (Libc.Kill(ProcessIdentifier, signalNumber) == 0)
        {
            return errno;
        }

        var childError = Marshal.GetLastPInvokeError();
        return childError == Errno.NoSuchProcess ? errno : childError;
    }

    /// <summary>SIGKILL to the child and its descendants (A2) without the SIGTERM phase; errno is ignored.</summary>
    public void Kill() => KillRemainingTree();

    /// <summary>SIGKILL to whatever still belongs to the child's tree, even when the direct child already exited.</summary>
    public void KillRemainingTree()
    {
        if (HasRemainingTree())
        {
            SignalTermination(Libc.SignalKill);
        }
    }

    /// <summary>True while the direct child, its process group or a /proc-visible descendant still exists.</summary>
    private bool HasRemainingTree()
    {
        if (processGroup != 0)
        {
            if (Libc.KillProcessGroup(processGroup, 0) == 0)
            {
                return true;
            }

            return Marshal.GetLastPInvokeError() == Errno.NotPermitted;
        }

        if (Libc.Kill(ProcessIdentifier, 0) == 0 || Marshal.GetLastPInvokeError() == Errno.NotPermitted)
        {
            return true;
        }

        return ProcessTree.DescendantsDeepestFirst(ProcessIdentifier).Count > 0;
    }

    /// <summary>
    /// Releases the process object and the captured streams once the child has exited and every pump has reached EOF.
    /// The pipes are the descriptors that leaked on every launch (P2): releasing them here, from the exit waiter and
    /// from the pumps observer, makes the descriptor count flat across repeated runs.
    /// </summary>
    public void Dispose()
    {
        var releaseUnstarted = false;
        lock (disposalGate)
        {
            if (!pumpsStarted)
            {
                // The launch sequence failed before StartPumps (a later pipeline stage could not be started):
                // no pump will ever read these streams, and TryRelease's guard exists only for the exit-waiter
                // race. Release them here instead of leaving three descriptors to the finalizer (audit F1).
                noPumpsExpected = true;
                releaseUnstarted = true;
            }
            else if (eventsDisposed == 0 && !pumpsCompleted.IsSet)
            {
                // The call is over: a pump still blocked on a detached writer is asked to stop, so the read ends
                // are released at its next poll tick instead of waiting for that writer (P8e).
                StopReadingPumps();
            }
        }

        if (releaseUnstarted)
        {
            ReleaseStreams();
            // The pipeline/failure path has killed the child; wait for the waiter so TryRelease can mark the child
            // released and the events can be disposed. A SIGKILLed child is reaped well within the grace period.
            // The waiter may win the race and dispose the event first (audit M1): then the child is already released.
            try
            {
                exited.Wait(ProcessTimings.KillGracePeriod);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        TryRelease();
        DisposeEventsIfReleased();
    }

    /// <summary>Releases the lazy event handles once the child and its pumps are released; idempotent.</summary>
    private void DisposeEventsIfReleased()
    {
        lock (disposalGate)
        {
            if (released == 0 || eventsDisposed != 0)
            {
                return;
            }

            eventsDisposed = 1;
            foreach (var pump in pumps)
            {
                pump.Dispose();
            }

            exited.Dispose();
            pumpsCompleted.Dispose();
        }
    }

    /// <summary>
    /// Closes the captured pipes once their readers are done. Idempotent: the writer may already be closed by
    /// CloseStandardInput, disposing a StreamWriter flushes, and flushing a closed pipe throws (P2).
    /// </summary>
    public void ReleaseStreams()
    {
        lock (disposalGate)
        {
            if (streamsReleased != 0)
            {
                return;
            }

            streamsReleased = 1;
            DisposeIgnoringClosed(() => standardInput?.Dispose());
            DisposeIgnoringClosed(() => standardOutput?.Dispose());
            DisposeIgnoringClosed(() => standardError?.Dispose());
        }
    }

    private static void DisposeIgnoringClosed(Action dispose)
    {
        try
        {
            dispose();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Already closed by the caller, the child or StopReadingPumps.
        }
    }

    private void TryRelease()
    {
        lock (disposalGate)
        {
            if (released != 0 || !exited.IsSet)
            {
                return;
            }

            if (pumps.Count > 0 && !pumpsCompleted.IsSet)
            {
                return;
            }

            // A child that exits before StartPumps runs must not release the captured pipes: the pumps are
            // about to read what the child already wrote (measured on `parallel.run_all` with an immediate
            // `echo deuxieme`). Only a child with no redirected stream can be released without pumps.
            if (!pumpsStarted && !noPumpsExpected && (standardOutput is not null || standardError is not null))
            {
                return;
            }

            released = 1;
            // The pumps have reached EOF, so no thread is inside read(2) on these descriptors: closing them now is
            // safe and is what keeps a failed pipeline flat (runtime audit #1). A detached descendant that still
            // holds the writer leaves `pumpsCompleted` unset; Dispose asks the poll-based pumps to stop, so the
            // read ends are released at once instead of waiting for that writer (P8e).
            ReleaseStreams();
        }
    }

    private void WaitForExit()
    {
        var errno = PosixSpawn.TryWait(ProcessIdentifier, out var code);
        if (errno == 0)
        {
            exitCode = code;
        }
        else
        {
            waitError = errno;
        }

        table.Unregister(this);
        exited.Set();
        TryRelease();
        if (noPumpsExpected)
        {
            DisposeEventsIfReleased();
        }
    }

    /// <summary>A waitpid failure is an invariant violation (no global reaper): report it as the catalogued other failure.</summary>
    internal void ThrowIfWaitFailed()
    {
        if (waitError != 0)
        {
            throw new ProcessFailureException(
                ProcessErrorKinds.Other,
                program,
                "cannot wait for the child (errno " + waitError.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
        }
    }
}

internal static class ProcessTimings
{
    public static readonly TimeSpan KillGracePeriod = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long <c>process.run_inherit</c> lets its capture pumps finish after the direct child exited. The bytes are
    /// already in the pipe (a write of at most the pipe buffer), so this is a scheduler allowance, not a data wait: a
    /// descendant that keeps the pipe open is never waited for (DESIGN §6).
    /// </summary>
    public static readonly TimeSpan InheritDrainBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>The largest value <see cref="TimeSpan"/> can represent in milliseconds; larger deadlines mean none.</summary>
    private static readonly double MaximumTimeoutMilliseconds = TimeSpan.MaxValue.TotalMilliseconds;

    /// <summary>
    /// <see cref="WaitHandle.WaitAny(WaitHandle[], TimeSpan)"/> takes a millisecond count that must fit in an
    /// <see cref="int"/>; a longer wait is split into chunks of at most this size and the loop re-evaluates the deadline.
    /// </summary>
    public static readonly TimeSpan MaximumWaitChunk = TimeSpan.FromMilliseconds(int.MaxValue - 1);

    /// <summary>A zero/negative deadline means none; a value beyond <see cref="TimeSpan.MaxValue"/> also means none.</summary>
    public static TimeSpan? ToTimeout(long timeoutMilliseconds) =>
        timeoutMilliseconds > 0 && timeoutMilliseconds < MaximumTimeoutMilliseconds
            ? TimeSpan.FromMilliseconds(timeoutMilliseconds)
            : null;

    /// <summary>Clamps a remaining duration to a wait chunk the runtime can pass to <see cref="WaitHandle.WaitAny(WaitHandle[], TimeSpan)"/>.</summary>
    public static TimeSpan ClampWaitChunk(TimeSpan remaining) =>
        remaining <= TimeSpan.Zero
            ? TimeSpan.Zero
            : remaining > MaximumWaitChunk ? MaximumWaitChunk : remaining;
}

/// <summary>Blocking wait that returns early (by throwing) when a signal interruption is pending.</summary>
internal static class InterruptibleWait
{
    private static readonly TimeSpan LongestSlice = TimeSpan.FromHours(1);

    /// <summary>True when the signal is set; false when the timeout expired first.</summary>
    public static bool Wait(ManualResetEventSlim signal, InterruptionMonitor monitor, TimeSpan? timeout)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            monitor.ThrowIfInterrupted();
            if (signal.IsSet)
            {
                return true;
            }

            var slice = LongestSlice;
            if (timeout is { } limit)
            {
                var remaining = limit - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                slice = remaining < slice ? remaining : slice;
            }

            try
            {
                var signalHandle = signal.WaitHandle;
                WaitHandle.WaitAny([signalHandle, monitor.WaitHandle], slice);
            }
            catch (ObjectDisposedException)
            {
                // The pumps observer disposes a pump's event only after that pump has completed (runtime audit #1);
                // reaching the wait after the disposal therefore means the pump is done.
                return true;
            }
        }
    }
}
