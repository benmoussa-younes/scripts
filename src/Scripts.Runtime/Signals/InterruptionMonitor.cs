using System.Runtime.InteropServices;
using Scripts.Runtime.Platform;

namespace Scripts.Runtime.Signals;

/// <summary>
/// Records the first SIGINT/SIGTERM/SIGHUP, wakes blocked waits, notifies listeners (child termination) and arms a
/// watchdog that forces process exit if the interpreter does not unwind in time (DESIGN §6).
/// </summary>
public sealed class InterruptionMonitor : IDisposable
{
    public const int HangUpExitCode = 129;
    public const int InterruptExitCode = 130;
    public const int TerminateExitCode = 143;

    /// <summary>The signal name recorded for SIGINT.</summary>
    public const string InterruptSignalName = "SIGINT";

    public static readonly TimeSpan DefaultWatchdogDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Backstop while the deferred cleanup runs: the normal watchdog would truncate a legitimate cleanup, so it is
    /// replaced by this longer one instead of being cancelled outright (P2). A cleanup that hangs still terminates.
    /// </summary>
    public static readonly TimeSpan CleanupWatchdogDelay = TimeSpan.FromSeconds(30);

    private readonly object gate = new();
    private readonly Action<int> forceExit;
    private readonly TimeSpan watchdogDelay;
    private readonly ManualResetEventSlim interrupted = new(false);
    private readonly CancellationTokenSource interruptedSource = new();
    private readonly List<PosixSignalRegistration> registrations = [];
    private InterruptionTermination? pending;
    private Timer? watchdog;
    private int suspended;

    /// <param name="forceExit">Called with the exit code when the watchdog expires; production code terminates the process.</param>
    public InterruptionMonitor(Action<int> forceExit, TimeSpan watchdogDelay)
    {
        this.forceExit = forceExit;
        this.watchdogDelay = watchdogDelay;
    }

    /// <summary>Raised on the signal thread right after an interruption is recorded.</summary>
    public event Action? Interrupted;

    /// <summary>Signalled once an interruption is pending; blocking waits include it in their wait set.</summary>
    public WaitHandle WaitHandle => interrupted.WaitHandle;

    /// <summary>Cancelled once an interruption is pending; event waits pass it instead of building a WaitHandle set (R0).</summary>
    public CancellationToken CancellationToken => interruptedSource.Token;

    public bool IsInterrupted => pending is not null;

    public InterruptionTermination? Pending => pending;

    public void ThrowIfInterrupted()
    {
        var current = Volatile.Read(ref suspended) != 0 ? null : pending;
        if (current is not null)
        {
            throw new InterruptionTermination(current.SignalName, current.ExitCode);
        }
    }

    /// <summary>
    /// Suspends <see cref="ThrowIfInterrupted"/> until the returned scope is disposed (nested scopes compose; the pending
    /// interruption is kept). Used by the interpreter while it drains the deferred actions: an action that calls the stdlib
    /// must still run after a signal, since the signal is already reflected in the run's outcome. The forced-exit watchdog
    /// is cancelled for the duration so a legitimate cleanup is not truncated (the exit code is already decided).
    /// </summary>
    public IDisposable Suspend()
    {
        Interlocked.Increment(ref suspended);
        lock (gate)
        {
            watchdog?.Dispose();
            watchdog = pending is { } interruption
                ? new Timer(_ => forceExit(interruption.ExitCode), null, CleanupWatchdogDelay, Timeout.InfiniteTimeSpan)
                : null;
        }

        return new InterruptionSuspension(this);
    }

    private void EndSuspension()
    {
        if (Interlocked.Decrement(ref suspended) != 0)
        {
            return;
        }

        lock (gate)
        {
            if (pending is not { } interruption)
            {
                return;
            }

            watchdog?.Dispose();
            watchdog = new Timer(_ => forceExit(interruption.ExitCode), null, watchdogDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private sealed class InterruptionSuspension(InterruptionMonitor monitor) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            monitor.EndSuspension();
        }
    }

    /// <summary>Records the interruption (first one wins), wakes waiters, notifies listeners and arms the watchdog.</summary>
    public void Raise(string signalName, int exitCode)
    {
        lock (gate)
        {
            if (pending is not null)
            {
                return;
            }

            pending = new InterruptionTermination(signalName, exitCode);
            watchdog = new Timer(_ => forceExit(exitCode), null, watchdogDelay, Timeout.InfiniteTimeSpan);
            interrupted.Set();
        }

        // Outside the gate: cancellation runs the waiters' registrations synchronously.
        interruptedSource.Cancel();
        Interrupted?.Invoke();
    }

    /// <summary>
    /// Installs SIGINT/SIGTERM/SIGHUP handlers that cancel the default termination. A SIGINT or SIGHUP that was ignored
    /// when the process started (a background job, `nohup`) stays ignored, per POSIX convention: no handler is installed,
    /// so the inherited disposition is kept instead of being overridden.
    /// </summary>
    public void ListenForSignals()
    {
        if (!SignalDispositions.IsIgnored(Libc.SignalInterrupt))
        {
            Listen(PosixSignal.SIGINT, InterruptSignalName, InterruptExitCode);
        }

        Listen(PosixSignal.SIGTERM, "SIGTERM", TerminateExitCode);
        if (!SignalDispositions.IsIgnored(Libc.SignalHangUp))
        {
            Listen(PosixSignal.SIGHUP, "SIGHUP", HangUpExitCode);
        }
    }

    public void Dispose()
    {
        foreach (var registration in registrations)
        {
            registration.Dispose();
        }

        registrations.Clear();
        lock (gate)
        {
            watchdog?.Dispose();
        }
    }

    private void Listen(PosixSignal signal, string signalName, int exitCode) =>
        registrations.Add(PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            Raise(signalName, exitCode);
        }));
}
