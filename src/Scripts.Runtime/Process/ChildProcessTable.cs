using Scripts.Runtime.Platform;

namespace Scripts.Runtime.Processes;

/// <summary>Running children (process.run, process.run_inherit, process.run_shell, process.spawn) so signals and shutdown can terminate them.</summary>
internal sealed class ChildProcessTable
{
    private readonly object gate = new();
    private readonly List<Entry> children = [];
    private readonly List<Timer> escalationTimers = [];
    private bool terminationRequested;

    public int Count
    {
        get
        {
            lock (gate)
            {
                return children.Count;
            }
        }
    }

    public void Register(ChildProcess child)
    {
        bool terminateImmediately;
        lock (gate)
        {
            children.Add(new Entry(child));
            terminateImmediately = terminationRequested;
        }

        if (terminateImmediately)
        {
            TerminateWithEscalation([child]);
        }
    }

    public void Unregister(ChildProcess child)
    {
        lock (gate)
        {
            children.RemoveAll(entry => ReferenceEquals(entry.Child, child));
        }
    }

    /// <summary>
    /// Allows children created during the deferred-action drain to run normally: after a signal, the "terminate every
    /// child on sight" flag would otherwise kill the very subprocesses a cleanup action needs. Called once per drain.
    /// </summary>
    public void AllowChildrenForDeferredActions()
    {
        lock (gate)
        {
            terminationRequested = false;
        }
    }

    /// <summary>Signal path: SIGTERM now, SIGKILL after the grace period for survivors; children registered later get the same treatment.</summary>
    public void TerminateAllAfterInterruption()
    {
        ChildProcess[] snapshot;
        lock (gate)
        {
            terminationRequested = true;
            snapshot = ChildrenIn(children);
        }

        TerminateWithEscalation(snapshot);
    }

    /// <summary>Exit path: SIGTERM, wait up to the grace period, SIGKILL survivors.</summary>
    public void TerminateAllAndWait()
    {
        ChildProcess[] snapshot;
        lock (gate)
        {
            snapshot = ChildrenIn(children);
        }

        foreach (var child in snapshot)
        {
            child.RequestTermination();
        }

        var deadline = DateTime.UtcNow + ProcessTimings.KillGracePeriod;
        foreach (var child in snapshot)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || !child.ExitSignal.Wait(remaining))
            {
                child.KillRemainingTree();
                child.ExitSignal.Wait(ProcessTimings.KillGracePeriod);
            }

            // Even if the direct child exited during the grace period, a descendant may still be in its tree.
            child.KillRemainingTree();
        }
    }

    private static ChildProcess[] ChildrenIn(List<Entry> entries)
    {
        var selected = new List<ChildProcess>(entries.Count);
        foreach (var entry in entries)
        {
            selected.Add(entry.Child);
        }

        return [.. selected];
    }

    private void TerminateWithEscalation(ChildProcess[] targets)
    {
        if (targets.Length == 0)
        {
            return;
        }

        foreach (var child in targets)
        {
            child.SignalTermination(Libc.SignalTerminate);
        }

        var escalation = new Timer(
            _ =>
            {
                foreach (var child in targets)
                {
                    // The direct child may have exited while a detached descendant from its group or /proc subtree
                    // is still alive; the escalation must reach that descendant too (audit F4).
                    child.KillRemainingTree();
                }
            },
            null,
            ProcessTimings.KillGracePeriod,
            Timeout.InfiniteTimeSpan);
        lock (gate)
        {
            escalationTimers.Add(escalation);
        }
    }

    private readonly record struct Entry(ChildProcess Child);
}
