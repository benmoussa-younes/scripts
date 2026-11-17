using Core.Concurrency;
using Scripts.Runtime.Interpreter;

namespace Scripts.Runtime.Parallel;

/// <summary>
/// The one multithreading entry point of <c>parallel</c> (DESIGN §7.10.1): a thin, in-process adapter over the
/// shared <see cref="Core.Concurrency.ConcurrencyContext"/>. Every bounded call — <c>map</c>,
/// <c>exec_all</c>, <c>walk</c> — is one <see cref="ConcurrencyContext.For(long, long, long, Action{long}, ConcurrencyOptions)"/>
/// on the same context, so scripts exposes exactly one worker pool.
/// </summary>
/// <remarks>
/// <para>
/// The context is created lazily on the first call that actually needs workers (a <c>workers == 1</c> call stays
/// sequential on the calling thread and pays nothing) and is dropped by <see cref="Quiesce"/>. It is
/// <b>refcounted</b>: a long-lived host can run several scripts CONCURRENTLY in one process, so the shared
/// context may only be torn down when the LAST of them finishes — never while another run still has a job in
/// flight (that would hang it). <see cref="ProgramInterpreter"/> brackets each run with
/// <see cref="BeginRun"/>/<see cref="Quiesce"/>; the semantics are unchanged from the pool this replaced.
/// </para>
/// <para>
/// The configuration is fixed once for the process: the worker stack is
/// <see cref="LargeStackThread.WorkerStackSizeInBytes"/> (64 MiB), so a <c>transform</c> or <c>visit</c> that
/// recurses to the interpreter's documented call-depth limit of 10000 behaves on a worker exactly like on the
/// main interpreter thread (DESIGN §5.8), and the workload is <see cref="WorkloadKind.IoBound"/> because a
/// parallel body blocks on an external resource (a child process, a directory read): the pool ceiling is
/// the logical processor count (ARCHITECTURE.md, "Concurrency"). An explicit script <c>workers</c> argument
/// (1..256) wins for its own call but never lifts that cap: a call that cannot rent every wanted worker simply
/// runs the deficit on its own thread, which is also what keeps a nested call deadlock-free.
/// </para>
/// </remarks>
internal static class ParallelPool
{
    /// <summary>Bounds the call's partition to the exact ported <c>workers</c> range (1..256).</summary>
    private const int MinimumWorkers = 1;

    private const int MaximumWorkers = 256;

    // The one process-wide context, created on first use (which keeps startup free of any worker thread) and
    // dropped by Quiesce. `volatile`: published under `creationGate` and read lock-free on every call.
    private static readonly object creationGate = new();
    private static volatile ConcurrencyContext? shared;

    // Number of runs currently using the shared context. See the class remarks.
    private static int activeRuns;

    /// <summary>
    /// Depth of the parallel bodies running on the CURRENT thread. A thread at depth 0 is at the top of a
    /// call (an idle pooled worker, or the interpreter's own thread); a thread above 0 is already running a
    /// body — a nested call on that same thread must NOT reset the per-thread interpreter state, or it would
    /// clobber the outer evaluation in progress.
    /// </summary>
    [ThreadStatic]
    private static int bodyDepth;

    /// <summary>
    /// Whether the CURRENT thread's per-thread interpreter state may have been left unbalanced by a previous
    /// body that threw. The interpreter increments its call depth (and pushes the environment/return slot)
    /// without a matching decrement on the throwing path, so only a body that threw can leave the state dirty.
    /// The flag is set on the throwing path and cleared when the state is reset, so a successful body pays
    /// neither the reset nor the <see cref="ConcurrencyContext.WorkerIndex"/> lookup.
    /// </summary>
    [ThreadStatic]
    private static bool stateDirty;

    /// <summary>
    /// Marks the start of a run. A run that uses <c>parallel</c> must be bracketed by
    /// <see cref="BeginRun"/>/<see cref="Quiesce"/> so the shared context is only torn down when the last
    /// concurrent run finishes (a long-lived host can run several scripts at once).
    /// </summary>
    public static void BeginRun() => Interlocked.Increment(ref activeRuns);

    /// <summary>
    /// Disposes the shared context, but ONLY when the last concurrent run finishes. Called at the end of a run:
    /// the workers are background threads, so this only makes the shutdown deterministic (no worker of a
    /// finished run is still parked when the caller returns) and the next run starts from a clean, empty pool.
    /// Idempotent-safe: an unmatched call does nothing.
    /// </summary>
    public static void Quiesce()
    {
        if (Interlocked.Decrement(ref activeRuns) > 0)
        {
            // Other run(s) still active: the shared context is left untouched.
            return;
        }

        Interlocked.Exchange(ref activeRuns, 0);

        ConcurrencyContext? instance;
        lock (creationGate)
        {
            instance = shared;
            shared = null;
        }

        instance?.Dispose();
    }

    /// <summary>
    /// Runs <paramref name="body"/> once per index in <c>[0, itemCount)</c> on at most <paramref name="workers"/>
    /// workers of the shared context, and returns the call's result.
    /// </summary>
    /// <param name="itemCount">The number of items; the body is called with <c>0..itemCount-1</c>.</param>
    /// <param name="workers">The script's worker bound, already validated to 1..256 by the caller.</param>
    /// <param name="body">The body to run for each index.</param>
    /// <param name="partition">How the call splits its items; claim-one for map/exec_all, static chunks for walk.</param>
    /// <param name="abortAction">
    /// Called from inside the failing worker right after the failure is recorded (kills live children so a
    /// blocked worker unblocks promptly), then again once every worker is idle (belt and braces). Idempotent.
    /// </param>
    public static ConcurrencyResult Run(
        int itemCount,
        int workers,
        Action<long> body,
        WorkPartition partition,
        Action<Exception>? abortAction = null)
    {
        // Validated here as well as by the caller: the shared context is the last line of defence, and a bound
        // outside 1..256 is a programmer error, never silently clamped.
        if (workers is < MinimumWorkers or > MaximumWorkers)
        {
            throw new ArgumentOutOfRangeException(nameof(workers), workers, "the worker count must be between 1 and 256");
        }

        var context = Shared;
        var options = new ConcurrencyOptions
        {
            WorkerCount = workers,
            Partition = partition,
            AbortAction = abortAction,
        };

        return context.For(0, itemCount, 1, index => RunBody(context, body, index), options);
    }

    /// <summary>
    /// Runs one item, resetting the per-thread interpreter state first when this thread is a pooled worker at
    /// the top of a call and a previous body left the state dirty.
    /// </summary>
    /// <remarks>
    /// A <see cref="ConcurrencyContext"/> worker is reused across calls, and the interpreter's call depth /
    /// current environment / return slot are thread-affine and are NOT restored when a body throws (the
    /// increment in <c>InvokeFunction</c> has no matching decrement on the throwing path). Without a reset a
    /// second call landing on the same worker inherits the previous call's depth and is wrongly reported as a
    /// <c>SCRIPTS-RUNTIME-0011</c> — the MAJOR reuse hazard.
    /// <para>
    /// The reset is only needed after a body <em>threw</em>: a body that returned normally left the state
    /// balanced. So the reset is gated on <see cref="stateDirty"/> (set on the throwing path) instead of being
    /// run for every item — a successful body pays neither the reset nor the <c>WorkerIndex</c> lookup. In the
    /// default (non collect-all) failure policy a failing worker also stops claiming, so no later item of the
    /// same call runs on the dirty worker; the flag therefore only has to survive until the next call. (A
    /// collect-all body that throws and continues would need the reset between items — which this flag provides,
    /// since the next item re-checks it.) The reset is still skipped when the thread is already inside a
    /// parallel body (a nested call running on the calling worker) and on a thread that is not a worker at
    /// all (the interpreter's main thread, which must keep its own state across the call).
    /// </para>
    /// </remarks>
    private static void RunBody(ConcurrencyContext context, Action<long> body, long index)
    {
        if (bodyDepth == 0 && stateDirty && context.WorkerIndex >= 0)
        {
            ProgramExecution.ResetEvaluationState();
            stateDirty = false;
        }

        bodyDepth++;
        try
        {
            body(index);
        }
        catch
        {
            // Any throw may leave the interpreter's thread-affine state unbalanced; flag it for the next reset.
            stateDirty = true;
            throw;
        }
        finally
        {
            bodyDepth--;
        }
    }

    private static ConcurrencyContext Shared
    {
        get
        {
            var instance = shared;
            if (instance is not null)
            {
                return instance;
            }

            lock (creationGate)
            {
                return shared ??= ConcurrencyContext.Create(
                    new ConcurrencyConfiguration
                    {
                        // The parallel bodies block on external work (a child process, a directory read),
                        // so the context is I/O-bound: the pool ceiling is the LOGICAL processor count, not the
                        // physical core count. That is the library's documented sizing for a blocking body
                        // (ARCHITECTURE.md, "Concurrency"), and it is what lets exec_all overlap several blocked
                        // children per core. The ceiling is still a single shared budget: a script's `workers`
                        // (1..256) never lifts it, and nesting shares it rather than multiplying it.
                        Workload = WorkloadKind.IoBound,
                        WorkerStackBytes = LargeStackThread.WorkerStackSizeInBytes,
                    });
            }
        }
    }
}
