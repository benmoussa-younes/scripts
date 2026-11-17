// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Concurrency;

/// <summary>
/// The context's worker set: a pool of dedicated worker threads that is born with
/// the resolved count and <em>grows on demand</em> when a nested call needs more
/// threads than are currently idle.
/// </summary>
/// <remarks>
/// <para>
/// This is the single most important property of the design (design
/// <c>DESIGN-CORE-CONCURRENCY.md</c> §5, spike §4/§7): a <em>fixed</em> worker
/// set deadlocks on a nested parallel call. When a body running inside a call
/// itself enters a call, the inner call would broadcast to workers the outer call
/// still occupies and then wait for itself. The pool therefore hands out the
/// workers that are free and runs the rest of the inner call's items on the
/// calling thread, so a bound of <c>N</c> is "at most N in parallel for this
/// call", never "the pool may never exceed N threads".
/// </para>
/// <para>
/// <b>The total size is capped at the context's workload-aware ceiling</b>
/// (<see cref="ConcurrencyConfiguration.MaximumPoolSize"/>, defaulting to the physical core
/// count for CPU-bound work and the logical processor count for I/O-bound work,
/// via <see cref="ThreadCount.ResolvePoolCap"/>). This is the design §4a HARD
/// RULE: no SMT oversubscription — never more than the logical processors — ever,
/// at any nesting depth. The ceiling is one global budget shared by nesting, not
/// multiplied: a CPU-bound nesting stays within the physical cores (6×6, never
/// 72) and an I/O-bound nesting within the logical processors (12×12, never 144).
/// A call that asked for more workers than the pool can supply runs the remainder
/// of its items on the calling thread — the caller is inside a worker in the
/// nested case, so its <see cref="ConcurrencyContext.WorkerIndex"/> stays valid —
/// which both honours the ceiling and keeps nesting deadlock-free. The spike
/// measured a pool without this ceiling growing to 72 threads for an 8×8 nested
/// call and costing ×180 across a cap sweep.
/// </para>
/// <para>
/// The pool never shrinks, so it settles at the largest live demand it has seen
/// (bounded by the ceiling). A steady-state call — no nesting — therefore reuses
/// the same threads for the context's whole life and creates none.
/// </para>
/// <para>
/// The idle/busy bookkeeping lives here, behind one lock, and is off the hot
/// path: a call rents its workers once, dispatches each its own wave directly,
/// and returns them once. The idle set is a stack, so rent and return are O(1)
/// pops and pushes with no shifting and no sort.
/// </para>
/// </remarks>
internal sealed class WorkerPool : IDisposable
{
    /// <summary>
    /// The longest time <see cref="Dispose()"/> spends settling in-flight drain and
    /// joining the workers (roadmap R8, bounded dispose). A body that blocks past it
    /// is abandoned: the worker thread is a background thread, so the process can
    /// still exit, and the abandoned worker is never reused (the pool is disposed).
    /// </summary>
    internal static readonly TimeSpan DisposeBudget = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly List<Worker> _all = [];
    private readonly int _stackBytes;
    private readonly bool _pinToCpu;
    private readonly int _maximumSize;

    // The idle set as a stack: workers are rented from the top and returned to the
    // top, so rent and return are O(1) pushes/pops with no shifting. Entries above
    // `_idleCount` are stale references, overwritten on the next push.
    private Worker[] _idle = new Worker[16];
    private int _idleCount;

    // Workers still draining in the background after an AbortMode.Front call
    // returned early. They are not idle, so Rent may replace them up to the cap.
    private int _draining;

    // Signalled while a Front drain is in flight, so Dispose can wait for it
    // before joining the workers: a Front call returns before its bodies finish,
    // so the drain outlives the call and must not race teardown.
    private readonly ManualResetEventSlim _drained = new(true);

    // A copy-on-grow snapshot of _all, so WorkerIndex can map the calling thread
    // to its stable index without taking the pool lock on a body's hot path.
    private Worker[] _snapshot = [];

    private bool _disposed;

    /// <summary>
    /// Creates a pool with <paramref name="initialCount"/> started idle workers.
    /// </summary>
    /// <param name="initialCount">The resolved steady-state worker count.</param>
    /// <param name="stackBytes">The stack size every worker is created with.</param>
    /// <param name="pinToCpu">Whether every worker asks, best-effort, to be pinned to a processor.</param>
    /// <param name="maximumSize">The pool's hard ceiling: the largest total worker count it may ever hold.</param>
    internal WorkerPool(int initialCount, int stackBytes, bool pinToCpu = false, int maximumSize = ConcurrencyConfiguration.MaximumWorkerCount)
    {
        _stackBytes = stackBytes;
        _pinToCpu = pinToCpu;
        _maximumSize = Math.Max(maximumSize, initialCount);
        SteadyStateCount = initialCount;

        for (var index = 0; index < initialCount; index++)
        {
            PushIdle(CreateWorker());
        }
    }

    /// <summary>
    /// Gets the pool's initial (steady-state) worker count: the resolved count
    /// the context was created with.
    /// </summary>
    internal int SteadyStateCount { get; }

    /// <summary>
    /// Gets the pool's hard ceiling: the largest total worker count it may hold.
    /// </summary>
    internal int MaximumSize => _maximumSize;

    /// <summary>
    /// Gets the number of workers the pool holds right now: the steady-state
    /// count plus any the pool had to create for a nested call.
    /// </summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _all.Count;
            }
        }
    }

    /// <summary>
    /// Gets the worker threads, for the suite to assert the deterministic join.
    /// </summary>
    internal IReadOnlyList<Thread> WorkerThreads
    {
        get
        {
            lock (_gate)
            {
                return [.. _all.Select(worker => worker.Thread)];
            }
        }
    }

    /// <summary>
    /// Gets the index, stable for the context's life, of the worker the given
    /// thread is, or <c>-1</c> when the thread is not one of this pool's workers.
    /// </summary>
    /// <param name="thread">The thread to identify.</param>
    /// <returns>The worker index, or <c>-1</c>.</returns>
    internal long IndexOf(Thread thread)
    {
        // Only a worker running a body calls this, so the snapshot it reads always
        // contains that worker; a grown snapshot replaces the reference atomically.
        var snapshot = Volatile.Read(ref _snapshot);

        for (var index = 0; index < snapshot.Length; index++)
        {
            if (ReferenceEquals(snapshot[index].Thread, thread))
            {
                return snapshot[index].Index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Rents up to <paramref name="wanted"/> workers, creating fresh ones when
    /// fewer than that are idle and the pool is below its ceiling.
    /// </summary>
    /// <param name="wanted">The number of workers the call wants; must be positive.</param>
    /// <returns>
    /// The rented workers, at most the pool's ceiling in total and possibly fewer
    /// than <paramref name="wanted"/> when the pool is at its ceiling; the caller
    /// runs the deficit on its own thread.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The pool is disposed.</exception>
    internal Worker[] Rent(int wanted)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Grow towards `wanted`, but never let the pool's TOTAL size pass the
            // ceiling (design §4a): a nested call asks for workers the outer call
            // holds, and growing past the ceiling oversubscribes. The ceiling is
            // physical for CPU-bound work and logical for I/O-bound work, but it is
            // one budget either way.
            //
            // A worker still draining after a Front abort is NOT replaced by a fresh
            // thread: it is still an active worker, so creating a replacement would
            // breach the ceiling. Instead the next call rents whatever is idle and
            // runs the deficit on its calling thread, exactly as any capped call
            // does; the drain accounting is what keeps that deficit from being the
            // whole wave's worth of workers for the whole drain.
            var canCreate = Math.Max(0, _maximumSize - _all.Count);
            var target = Math.Min(wanted, _idleCount + canCreate);
            while (_idleCount < target)
            {
                PushIdle(CreateWorker());
            }

            var count = Math.Min(wanted, _idleCount);
            var rented = new Worker[count];

            // Pop from the end; the dispatcher hands out slices in array order and
            // the wave carries the slice, so the worker order does not need to be
            // sorted by index. Dropping the sort removes a comparison callback per
            // rented worker from the hot path.
            for (var offset = 0; offset < count; offset++)
            {
                rented[offset] = _idle[--_idleCount];
            }

            return rented;
        }
    }

    /// <summary>
    /// Gets the number of workers currently draining in the background after an
    /// <see cref="AbortMode.Front"/> call returned early, for the suite.
    /// </summary>
    internal int DrainingCount
    {
        get
        {
            lock (_gate)
            {
                return _draining;
            }
        }
    }

    /// <summary>
    /// Returns rented workers to the pool, where a later call reuses them.
    /// </summary>
    /// <param name="workers">The workers to return.</param>
    internal void Return(Worker[] workers)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                // A call outliving Dispose: its workers were already joined by
                // Dispose, so there is nothing to return them to.
                return;
            }

            foreach (var worker in workers)
            {
                PushIdle(worker);
            }
        }
    }

    /// <summary>
    /// Records that <paramref name="count"/> workers are draining in the
    /// background after an <see cref="AbortMode.Front"/> call returned early.
    /// </summary>
    /// <param name="count">The number of workers being drained.</param>
    /// <remarks>
    /// A draining worker is still busy, so it is not idle; the count lets
    /// <see cref="Rent"/> allow the pool to replace it up to the ceiling instead of
    /// blocking the next call on a worker that only frees up later. Without it a
    /// front-aborted call starves the following call for the whole drain
    /// (measured: 318 ms of starvation across repeated front-aborted calls).
    /// </remarks>
    internal void BeginDrain(int count)
    {
        lock (_gate)
        {
            if (_draining == 0)
            {
                _drained.Reset();
            }

            _draining += count;
        }
    }

    /// <summary>
    /// Records that <paramref name="count"/> draining workers have finished.
    /// </summary>
    /// <param name="count">The number of workers that finished draining.</param>
    internal void EndDrain(int count)
    {
        lock (_gate)
        {
            _draining -= count;

            if (_draining == 0)
            {
                _drained.Set();
            }
        }
    }

    /// <summary>
    /// Disposes the pool: stops and joins every worker it ever created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent. A call must not be in flight when the pool is disposed; a
    /// worker returned after the disposal is dropped rather than resurrected, so
    /// the join stays deterministic.
    /// </para>
    /// <para>
    /// An <see cref="AbortMode.Front"/> call <em>returns</em> while its bodies are
    /// still running, so its drain is legitimately in flight when the caller
    /// disposes the context. Dispose therefore waits for any pending drain before
    /// joining: the alternative would join a worker mid-body and dispose its
    /// events under the drain continuation.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        var deadline = Environment.TickCount64 + (long)DisposeBudget.TotalMilliseconds;

        // Settle every in-flight Front drain first: its workers are still running
        // bodies, and joining them now would race the drain continuation (which
        // also disposes the failure record and returns the workers). The loop
        // re-checks under the gate so a drain that begins between the wait and the
        // join is still observed — but never waits past the dispose budget: a body
        // that blocks forever must not make Dispose block forever (it is a
        // background thread, so it cannot keep the process alive either).
        while (true)
        {
            lock (_gate)
            {
                if (_draining == 0)
                {
                    break;
                }
            }

            if (!_drained.Wait(Remaining(deadline)))
            {
                break;
            }
        }

        Worker[] all;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            all = [.. _all];
            _idleCount = 0;
        }

        // Every join draws on the same budget: N stuck workers cost the budget once,
        // not N times. A worker that did not end keeps its events (Worker returns
        // false and leaves them alone).
        foreach (var worker in all)
        {
            worker.Dispose(Remaining(deadline));
        }

        _drained.Dispose();
    }

    /// <summary>The time left before <paramref name="deadline"/> (never negative).</summary>
    private static TimeSpan Remaining(long deadline) =>
        TimeSpan.FromMilliseconds(Math.Max(0, deadline - Environment.TickCount64));

    /// <summary>
    /// Pushes a worker onto the idle stack, growing the backing array if needed.
    /// The caller holds <c>_gate</c>.
    /// </summary>
    /// <param name="worker">The worker to push.</param>
    private void PushIdle(Worker worker)
    {
        if (_idleCount == _idle.Length)
        {
            Array.Resize(ref _idle, _idle.Length * 2);
        }

        _idle[_idleCount++] = worker;
    }

    private Worker CreateWorker()
    {
        var worker = new Worker(_all.Count, _stackBytes, _pinToCpu);
        _all.Add(worker);

        if (_all.Count != _snapshot.Length)
        {
            Volatile.Write(ref _snapshot, [.. _all]);
        }

        worker.Start();
        return worker;
    }
}
