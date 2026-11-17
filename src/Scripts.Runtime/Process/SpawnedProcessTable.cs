namespace Scripts.Runtime.Processes;

internal sealed class SpawnedProcess(ChildProcess child, BufferingSink standardOutput, BufferingSink standardError, string program)
{
    public ChildProcess Child { get; } = child;

    public BufferingSink StandardOutput { get; } = standardOutput;

    public BufferingSink StandardError { get; } = standardError;

    /// <summary>Program string passed to process.spawn; reported by process.wait_for_process (DESIGN §3.4).</summary>
    public string Program { get; } = program;

    public bool Killed { get; set; }
}

/// <summary>process.spawn handles keyed by the child's process identifier.</summary>
internal sealed class SpawnedProcessTable
{
    private readonly object gate = new();
    private readonly Dictionary<int, SpawnedProcess> processes = [];

    public void Add(SpawnedProcess process)
    {
        lock (gate)
        {
            processes[process.Child.ProcessIdentifier] = process;
        }
    }

    /// <summary>Consumes the handle; null when unknown, already waited or killed.</summary>
    public SpawnedProcess? TakeForWait(long processIdentifier)
    {
        lock (gate)
        {
            if (processIdentifier is < 1 or > int.MaxValue
                || !processes.TryGetValue((int)processIdentifier, out var process)
                || process.Killed)
            {
                return null;
            }

            processes.Remove((int)processIdentifier);
            return process;
        }
    }

    /// <summary>
    /// Atomically reserves a handle for <c>process.terminate</c> under the table gate: null when unknown or already
    /// killed; an already-exited child is returned <b>without</b> marking it killed (the kill is a no-op and the
    /// handle stays waitable); a live child is marked killed before the gate is released, so a concurrent wait
    /// cannot also consume it.
    /// </summary>
    public SpawnedProcess? ReserveForKill(long processIdentifier)
    {
        lock (gate)
        {
            if (processIdentifier is < 1 or > int.MaxValue
                || !processes.TryGetValue((int)processIdentifier, out var process)
                || process.Killed)
            {
                return null;
            }

            if (!process.Child.HasExited)
            {
                process.Killed = true;
                // A killed handle is not waitable (SPEC-STDLIB §7.3): drop it now so the table and the
                // captured buffers do not survive the whole run (P2).
                processes.Remove((int)processIdentifier);
            }

            return process;
        }
    }
}
