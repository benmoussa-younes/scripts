// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Concurrency;

/// <summary>
/// Runs parallel work on a pool of dedicated worker threads with explicit control
/// over the worker count, the per-worker stack, per-worker state and abort.
/// </summary>
/// <remarks>
/// <para>
/// The context owns its threads: one per worker, created at <see cref="Create"/>
/// and reused for the context's whole life. Creating a context is the expensive
/// step (each thread is a stack); a call is cheap because it only rents the
/// workers it needs, writes each its own gate and waits for the barrier.
/// </para>
/// <para>
/// <b>The pool grows on demand but never past the workload-aware ceiling.</b> A
/// call rents workers from the pool and, when a body running inside the call
/// itself enters a call (a recursive <c>map</c>/<c>walk</c>), the inner call finds
/// the workers busy and rents what is left, running the rest of its items
/// <em>on the calling thread</em>. A bound of <c>N</c> therefore means "at most N
/// in parallel for this call", never "the pool may never exceed N threads". A
/// fixed worker set would deadlock here (design §5, spike §4/§7); this pool
/// cannot. Growth is capped at the context's ceiling
/// (<see cref="ConcurrencyConfiguration.MaximumPoolSize"/>) — the <em>physical</em> core
/// count for CPU-bound work, the <em>logical</em> processor count for I/O-bound
/// work. The ceiling is a single global budget shared by nesting, never
/// multiplied: on 6 physical / 12 logical a CPU-bound nesting is at most 6×6
/// (never 8×8, never a pool of 72) and an I/O-bound nesting at most 12×12 (never
/// more, never 144). "Oversubscription" — more than the logical processors — is
/// never reached (design §4a). A call that asked for more workers than the pool
/// can supply runs the remainder of its items on the calling thread; that
/// participation is also what keeps nesting deadlock-free at the ceiling, because
/// a call never waits on a worker it could not rent.
/// </para>
/// <para>
/// The dispatch model is direct: the caller's thread splits the work into one
/// contiguous slice per rented worker, writes that slice into the worker's own
/// gate and pulses it, then waits for every worker's gate to report done. There
/// is no shared queue on this path, so no worker contends with another; the one
/// shared structure is the claim cursor of the claim-based partitions. A
/// <see cref="BoundedQueue{T}"/> is the one deliberately shared structure for
/// producer/consumer work.
/// </para>
/// <para>
/// A call may run on the caller's thread while one or more nested calls run on
/// worker threads; the <c>For</c>/<c>Run</c> entry points are safe to re-enter,
/// from the caller or from a worker. Staging (<see cref="Submit"/>,
/// <see cref="SubmitBatch"/>, <see cref="DispatchAndWait"/>) shares one stage, so
/// a caller must not stage from two threads at once.
/// </para>
/// </remarks>
public sealed class ConcurrencyContext : IDisposable
{
    /// <summary>
    /// The number of spin iterations <see cref="AbortMode.Front"/> uses before it
    /// builds wait handles and blocks. A front failure or a trivial call is
    /// observed within it, so the warm path stays allocation-free.
    /// </summary>
    private const int FrontSpinCount = 2_000;

    private readonly WorkerPool _pool;
    private readonly object _stageGate = new();
    private readonly List<Action> _staged = [];
    private readonly List<Slot> _slots = [];

    private bool _disposed;

    private ConcurrencyContext(ConcurrencyConfiguration config, int workerCount)
    {
        Config = config;
        _pool = new WorkerPool(
            workerCount,
            config.WorkerStackBytes,
            config.PinToCpu,
            ThreadCount.ResolvePoolCap(config));
    }

    /// <summary>
    /// Gets the configuration this context was created from.
    /// </summary>
    public ConcurrencyConfiguration Config { get; }

    /// <summary>
    /// Gets the number of workers the context holds right now.
    /// </summary>
    /// <remarks>
    /// It is the resolved steady-state count until a nested call makes the pool
    /// grow, and the grown count afterwards. It never exceeds the context's
    /// workload-aware ceiling (<see cref="ConcurrencyConfiguration.MaximumPoolSize"/>) and is
    /// never smaller than the count <see cref="Create"/> resolved, because the pool
    /// never shrinks.
    /// </remarks>
    public int WorkerCount => _pool.Count;

    /// <summary>
    /// Gets the worker threads, for the suite to assert the deterministic join.
    /// Internal: the thread objects are an implementation detail, never API.
    /// </summary>
    internal IReadOnlyList<Thread> WorkerThreads => _pool.WorkerThreads;

    /// <summary>
    /// Gets the index, 0..<see cref="WorkerCount"/>-1, of the worker the calling
    /// thread is, or <c>-1</c> when the caller is not one of this context's workers.
    /// </summary>
    /// <remarks>
    /// The value is stable for each worker for the context's life, so a worker can
    /// key its own state on it.
    /// </remarks>
    public long WorkerIndex => _pool.IndexOf(Thread.CurrentThread);

    /// <summary>
    /// Creates a context with <paramref name="config"/>'s worker count and stack size.
    /// </summary>
    /// <param name="config">The configuration; <see langword="null"/> uses <see cref="ConcurrencyConfiguration.Default"/>.</param>
    /// <returns>A started context.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The resolved worker count is outside the allowed range, or <see cref="ConcurrencyConfiguration.WorkerStackBytes"/> is not positive.</exception>
    public static ConcurrencyContext Create(ConcurrencyConfiguration? config = null)
    {
        var effective = config ?? ConcurrencyConfiguration.Default;
        var workerCount = ThreadCount.Resolve(effective);

        // A stack of 0 would mean "the OS default" silently, which for the deep
        // recursion this library exists to run is a stack that overflows and
        // aborts the process. Require an explicit, positive size.
        if (effective.WorkerStackBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config),
                effective.WorkerStackBytes,
                "WorkerStackBytes must be positive; there is no implicit OS-default stack.");
        }

        return new ConcurrencyContext(effective, workerCount);
    }

    /// <summary>
    /// Registers a per-worker slot: one value per worker, created once and reused.
    /// </summary>
    /// <typeparam name="T">The value type the slot holds.</typeparam>
    /// <param name="create">Creates the value for a worker index; called once per worker.</param>
    /// <param name="destroy">Disposes a value when the context is disposed; may be <see langword="null"/>.</param>
    /// <returns>The registration to read with <see cref="WorkerSlot{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="create"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
    public SlotRegistration RegisterSlot<T>(Func<int, T> create, Action<T>? destroy = null)
    {
        ArgumentNullException.ThrowIfNull(create);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var slot = new Slot(
            typeof(T).Name,
            _pool.SteadyStateCount,
            index => create(index),
            destroy is null ? null : value => destroy((T)value!));

        _slots.Add(slot);
        return new SlotRegistration(slot);
    }

    /// <summary>
    /// Gets the calling worker's value for <paramref name="slot"/>, creating it on
    /// first use.
    /// </summary>
    /// <typeparam name="T">The slot's value type.</typeparam>
    /// <param name="slot">The registration returned by <see cref="RegisterSlot{T}"/>.</param>
    /// <returns>The value for the calling worker.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="slot"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The caller is not one of this context's workers.</exception>
    public T WorkerSlot<T>(SlotRegistration slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        var index = WorkerIndex;
        if (index < 0)
        {
            throw new InvalidOperationException(
                "WorkerSlot is only available on one of this context's worker threads.");
        }

        return (T)slot.Slot.Get((int)index)!;
    }

    /// <summary>
    /// Registers a per-worker buffer pool: one growable array per worker, created
    /// once and reused for the context's life.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="initialCapacity">The capacity, in elements, each worker's buffer starts with.</param>
    /// <returns>The pool to read with <see cref="WorkerBuffer{T}"/>.</returns>
    /// <remarks>
    /// This is the first-class pooling facility: a body reads its own worker's
    /// buffer with no rent/return and no lock, so a scratch buffer costs one field
    /// read instead of an <c>ArrayPool</c> round trip per item. The pool is sized
    /// to the steady-state worker count and grows with the pool, so a worker the
    /// pool created for a nested call gets its own buffer on first use. The spike
    /// measured this ~2× cheaper than <c>ArrayPool.Shared</c> (20.5 versus
    /// 41.9 ns/item) with no per-item allocation.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
    public WorkerBufferPool<T> RegisterBufferPool<T>(int initialCapacity = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);

        var workerCount = _pool.SteadyStateCount;
        var registration = RegisterSlot<Buffer<T>>(_ => new Buffer<T>(initialCapacity));
        return new WorkerBufferPool<T>(registration, initialCapacity, workerCount);
    }

    /// <summary>
    /// Gets the calling worker's buffer from <paramref name="pool"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="pool">The pool returned by <see cref="RegisterBufferPool{T}"/>.</param>
    /// <returns>The calling worker's buffer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The caller is not one of this context's workers.</exception>
    public Buffer<T> WorkerBuffer<T>(WorkerBufferPool<T> pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        return WorkerSlot<Buffer<T>>(pool.Registration);
    }

    /// <summary>
    /// Runs <paramref name="body"/> once for every index in the range, with the
    /// default options (chunked claim, stop on the first failure).
    /// </summary>
    /// <param name="start">The first index, inclusive.</param>
    /// <param name="end">The index one past the last, exclusive.</param>
    /// <param name="step">The index stride; must be positive.</param>
    /// <param name="body">The body to run for each index.</param>
    /// <returns>The call's result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is not positive.</exception>
    /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
    public ConcurrencyResult For(long start, long end, long step, Action<long> body) =>
        For(start, end, step, body, ConcurrencyOptions.Default);

    /// <summary>
    /// Runs <paramref name="body"/> once for every index in the range.
    /// </summary>
    /// <param name="start">The first index, inclusive.</param>
    /// <param name="end">The index one past the last, exclusive.</param>
    /// <param name="step">The index stride; must be positive.</param>
    /// <param name="body">The body to run for each index.</param>
    /// <param name="options">The per-call partition, failure policy and worker bound.</param>
    /// <returns>The call's result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is not positive or the per-call bound is outside <see cref="ConcurrencyConfiguration.MinimumWorkerCount"/>..<see cref="ConcurrencyConfiguration.MaximumWorkerCount"/>.</exception>
    /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
    public ConcurrencyResult For(long start, long end, long step, Action<long> body, ConcurrencyOptions options)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);

        var count = CountIndices(start, end, step);
        if (count == 0)
        {
            return ConcurrencyResult.Completed();
        }

        // Wake only as many workers as there are items: a 4-item call on an
        // 8-worker context wakes 4, and no worker is woken just to report idle.
        var wanted = (int)Math.Min(WantedWorkers(options), count);
        var workers = _pool.Rent(wanted);

        // The pool is capped, so a deeply nested call gets fewer workers than it
        // asked for; the caller takes the rest itself. This is what bounds the
        // thread count while keeping nesting deadlock-free: a call never waits on
        // a worker it could not rent.
        var participates = workers.Length < wanted;
        var front = options.AbortMode == AbortMode.Front && !options.CollectAllFailures;

        // Front pairs with the claim-one cursor: a front failure must be observed
        // without waiting for a whole chunk of ordinals, so the mode runs the
        // single-item arm even when a chunked partition was requested. ClaimChunk
        // + Front is the footgun the spike measured at 318 ms; running ClaimOne
        // here makes Front's promptness independent of ClaimChunkSize.
        var partition = options.CollectAllFailures
            ? options.Partition
            : front
                ? WorkPartition.ClaimOne
                : options.Partition;

        // The failure record — and, for Front, its abort signal — lives for the
        // whole call and, on the Front path, until the background drain finishes;
        // it is disposed there. Every other mode disposes nothing (no unmanaged
        // handle is created) and the record dies with the call.
        var failures = new FailureRecord(options.CollectAllFailures);
        var draining = false;

        try
        {
            if (partition == WorkPartition.ClaimOne)
            {
                var cursor = new ClaimCursor(start, count, step);

                foreach (var worker in workers)
                {
                    worker.Dispatch(new Wave.Claim(cursor, body, failures, options.AbortAction));
                }

                if (participates)
                {
                    new Wave.Claim(cursor, body, failures, options.AbortAction).Run();
                }
            }
            else if (partition == WorkPartition.ClaimChunk)
            {
                var chunkSize = Math.Max(1, options.ClaimChunkSize);
                var cursor = new ChunkCursor(start, count, step);

                foreach (var worker in workers)
                {
                    worker.Dispatch(new Wave.ClaimChunk(cursor, chunkSize, body, failures, options.AbortAction));
                }

                if (participates)
                {
                    new Wave.ClaimChunk(cursor, chunkSize, body, failures, options.AbortAction).Run();
                }
            }
            else
            {
                RunSlices(workers, participates, start, end, count, step, body, failures, options.AbortAction);
            }

            if (front)
            {
                // Front: return as soon as the first failure is recorded, or when
                // every worker has finished, whichever is first. The workers still
                // running are drained in the background; the pool keeps them out of
                // its idle set until then, so no other call dispatches to them.
                //
                // The drain is registered with the pool BEFORE the call can return,
                // so a concurrent Dispose always sees it and waits, instead of
                // joining a worker mid-body and disposing its events under the
                // drain continuation.
                _pool.BeginDrain(workers.Length);
                draining = true;

                WaitFront(workers, failures);

                // The lowest-index contract still holds on the Front path, but only
                // because of the claim-one cursor: it hands out ordinals in strictly
                // increasing order, so `start` is the smallest ordinal of the call.
                // If the first failure is NOT at `start`, every lower ordinal was
                // already claimed and a worker running one may still be mid-body
                // (it blocks, sleeps, or is simply slower) and can record a *lower*
                // failure after Front's first signal. Snapshotting the result now
                // would surface that first-recorded, higher-index failure instead of
                // the lowest-index one the record promises (measured: index 0 fails
                // slowly behind index 1 failing fast, 5/5 surfaced index 1).
                //
                // So: when the first failure is not the front-most ordinal, wait for
                // the in-flight workers before building the result. The wait is
                // bounded by the items already running and is zero when the failure
                // *is* the front-most ordinal — which is the case Front exists for
                // (a front fault still returns without waiting for the wave). The
                // drain accounting above still runs, and `draining` stays set, so the
                // pool keeps the workers reserved until the continuation returns them.
                if (AllDone(workers) || failures.FirstFailureIndex is { } index && index != start)
                {
                    WaitAll(workers);
                }

                RunPostAbort(options, failures);
                return failures.ToResult();
            }

            WaitAll(workers);

            if (!failures.ShouldKeepGoing)
            {
                RunPostAbort(options, failures);
            }

            return failures.ToResult();
        }
        finally
        {
            if (draining)
            {
                // The background continuation drains the workers, disposes the
                // failure record after the last worker stops writing to it, and
                // returns the workers to the pool.
                ThreadPool.UnsafeQueueUserWorkItem(static state =>
                {
                    var continuation = (DrainContinuation)state!;
                    WaitAll(continuation.Workers);
                    continuation.Record.Dispose();
                    continuation.Pool.EndDrain(continuation.Workers.Length);
                    continuation.Pool.Return(continuation.Workers);
                }, new DrainContinuation(_pool, workers, failures));
            }
            else
            {
                _pool.Return(workers);
            }
        }
    }

    /// <summary>
    /// Waits for <see cref="AbortMode.Front"/>: returns as soon as the first
    /// failure is recorded, or when every worker has finished, whichever is first.
    /// </summary>
    /// <param name="workers">The rented workers.</param>
    /// <param name="failures">The call's failure record, whose signal is raised on the first failure.</param>
    private static void WaitFront(Worker[] workers, FailureRecord failures)
    {
        // Spin first: a trivial call finishes every worker within it, and a
        // front-failure is observed within it too. No handle is built on this
        // path, so a warm call stays allocation-free.
        for (var spin = 0; spin < FrontSpinCount; spin++)
        {
            if (!failures.ShouldKeepGoing || AllDone(workers))
            {
                return;
            }

            Thread.SpinWait(1);
        }

        // Slow path: block on the first of (worker done, ..., first failure).
        // WaitAny wakes as soon as one condition holds; the loop re-checks because
        // a worker finishing is not the end if another still runs.
        var handles = new WaitHandle[workers.Length + 1];
        for (var index = 0; index < workers.Length; index++)
        {
            handles[index] = workers[index].DoneHandle;
        }

        handles[workers.Length] = failures.FailedHandle;

        while (true)
        {
            if (!failures.ShouldKeepGoing || AllDone(workers))
            {
                return;
            }

            _ = WaitHandle.WaitAny(handles);
        }
    }

    private static bool AllDone(Worker[] workers)
    {
        foreach (var worker in workers)
        {
            if (!worker.IsDone)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Runs <paramref name="body"/> once for every index, passing a caller value
    /// and the default options.
    /// </summary>
    /// <typeparam name="TState">The caller value type.</typeparam>
    /// <param name="start">The first index, inclusive.</param>
    /// <param name="end">The index one past the last, exclusive.</param>
    /// <param name="step">The index stride; must be positive.</param>
    /// <param name="state">The value passed to every body invocation.</param>
    /// <param name="body">The body to run for each index.</param>
    /// <returns>The call's result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is not positive.</exception>
    /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
    public ConcurrencyResult For<TState>(long start, long end, long step, TState state, Action<long, TState> body) =>
        For(start, end, step, state, body, ConcurrencyOptions.Default);

    /// <summary>
    /// Runs <paramref name="body"/> once for every index, passing a caller value
    /// and per-call options.
    /// </summary>
    /// <typeparam name="TState">The caller value type.</typeparam>
    /// <param name="start">The first index, inclusive.</param>
    /// <param name="end">The index one past the last, exclusive.</param>
    /// <param name="step">The index stride; must be positive.</param>
    /// <param name="state">The value passed to every body invocation.</param>
    /// <param name="body">The body to run for each index.</param>
    /// <param name="options">The per-call partition, failure policy and worker bound.</param>
    /// <returns>The call's result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is not positive or the per-call bound is outside <see cref="ConcurrencyConfiguration.MinimumWorkerCount"/>..<see cref="ConcurrencyConfiguration.MaximumWorkerCount"/>.</exception>
    /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
    public ConcurrencyResult For<TState>(
        long start,
        long end,
        long step,
        TState state,
        Action<long, TState> body,
        ConcurrencyOptions options)
    {
        ArgumentNullException.ThrowIfNull(body);

        return For(start, end, step, index => body(index, state), options);
    }

    /// <summary>
    /// Stages one task for the next <see cref="DispatchAndWait"/>.
    /// </summary>
    /// <param name="task">The task to run on a worker.</param>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
    public void Submit(Action task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stageGate)
        {
            _staged.Add(task);
        }
    }

    /// <summary>
    /// Stages a batch of tasks as one wave.
    /// </summary>
    /// <param name="tasks">The tasks to run on the workers.</param>
    /// <exception cref="ArgumentNullException">A task in <paramref name="tasks"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
    public void SubmitBatch(ReadOnlySpan<Action> tasks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stageGate)
        {
            foreach (var task in tasks)
            {
                ArgumentNullException.ThrowIfNull(task);
                _staged.Add(task);
            }
        }
    }

    /// <summary>
    /// Runs every staged task on the workers and waits for all of them.
    /// </summary>
    /// <returns>The wave's result.</returns>
    /// <remarks>
    /// The stage is emptied by the call, whether it succeeded or not. A wave with
    /// more tasks than workers runs several tasks per worker, in staging order,
    /// one after another. Every failure is collected, so a barrier never hides one.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
    public ConcurrencyResult DispatchAndWait()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Action[] tasks;

        lock (_stageGate)
        {
            if (_staged.Count == 0)
            {
                return ConcurrencyResult.Completed();
            }

            tasks = [.. _staged];
            _staged.Clear();
        }

        // A wave rents the workers it will actually use: at most one task per
        // worker, and never more workers than there are tasks.
        var wanted = Math.Min(WantedWorkers(ConcurrencyOptions.Default), tasks.Length);
        var workers = _pool.Rent(wanted);
        var callerParticipates = workers.Length < wanted;

        try
        {
            var failures = new FailureRecord(collectAll: true);
            DispatchTasks(tasks, failures, workers, callerParticipates);
            WaitAll(workers);

            return failures.ToResult();
        }
        finally
        {
            _pool.Return(workers);
        }
    }

    /// <summary>
    /// Runs one task on a worker and waits for it: submit, dispatch, barrier.
    /// </summary>
    /// <param name="task">The task to run.</param>
    /// <returns>The call's result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
    public ConcurrencyResult Run(Action task) => Run(task, ConcurrencyOptions.Default);

    /// <summary>
    /// Runs one task on the workers, with per-call options, and waits for it.
    /// </summary>
    /// <param name="task">The task to run.</param>
    /// <param name="options">The per-call failure policy.</param>
    /// <returns>The call's result.</returns>
    /// <remarks>
    /// A task that is not an index loop runs on one worker, so the partition does
    /// not apply. The call rents a single worker, so a task that enters a call of
    /// its own never waits for the worker it is already on: the pool grows. A
    /// failure follows the call's failure policy and the abort action runs once,
    /// after the barrier. <see cref="ConcurrencyOptions.AbortMode"/> does not change a
    /// single task's behaviour: <see cref="AbortMode.Front"/> and
    /// <see cref="AbortMode.Drain"/> are identical here, because there is no wave
    /// of items to abandon — the one task either completes or throws, and the call
    /// always waits for it.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The per-call bound is outside <see cref="ConcurrencyConfiguration.MinimumWorkerCount"/>..<see cref="ConcurrencyConfiguration.MaximumWorkerCount"/>.</exception>
    /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
    public ConcurrencyResult Run(Action task, ConcurrencyOptions options)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A task runs on one worker whatever the per-call bound says, but an
        // out-of-range bound is still a programmer error: validate it so Run and
        // For reject the same bad `workers` argument instead of ignoring it.
        _ = WantedWorkers(options);

        var rented = _pool.Rent(1);

        // At the pool's cap the caller is already inside a worker (a nested call)
        // or the pool is fully rented: run the task on this thread rather than wait
        // for a worker that cannot be created.
        if (rented.Length == 0)
        {
            return RunOnCaller(task, options);
        }

        var worker = rented[0];

        try
        {
            var failures = new FailureRecord(options.CollectAllFailures);
            worker.Dispatch(new Wave.Tasks([task], 0, 1, failures));
            worker.WaitDone();

            if (!failures.ShouldKeepGoing)
            {
                RunPostAbort(options, failures);
            }

            return failures.ToResult();
        }
        finally
        {
            _pool.Return([worker]);
        }
    }

    /// <summary>
    /// Runs one task on the calling thread, used when the pool is at its cap.
    /// </summary>
    /// <param name="task">The task to run.</param>
    /// <param name="options">The per-call failure policy.</param>
    /// <returns>The call's result.</returns>
    private static ConcurrencyResult RunOnCaller(Action task, ConcurrencyOptions options)
    {
        var failures = new FailureRecord(options.CollectAllFailures);
        new Wave.Tasks([task], 0, 1, failures).Run();

        if (!failures.ShouldKeepGoing)
        {
            RunPostAbort(options, failures);
        }

        return failures.ToResult();
    }

    /// <summary>
    /// Disposes the context: wakes every worker, joins it and disposes every
    /// registered slot.
    /// </summary>
    /// <remarks>
    /// The join is deterministic: when this returns, no worker thread of this
    /// context is running. A call must not be in flight on another thread when
    /// the context is disposed.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _pool.Dispose();

        foreach (var slot in _slots)
        {
            slot.DisposeValues();
        }

        _slots.Clear();

        lock (_stageGate)
        {
            _staged.Clear();
        }
    }

    /// <summary>
    /// Counts the indices of <c>[start, start + count*step)</c> that are below
    /// <paramref name="end"/>, without overflowing <see cref="long"/>.
    /// </summary>
    /// <param name="start">The first index, inclusive.</param>
    /// <param name="end">The index one past the last, exclusive.</param>
    /// <param name="step">The positive stride.</param>
    /// <returns>The number of indices, or <see cref="long.MaxValue"/> when the range holds more.</returns>
    /// <remarks>
    /// The span <c>end - start</c> does not fit a <see cref="long"/> for a range
    /// as wide as the type (for example <c>[long.MinValue, long.MaxValue)</c>), so
    /// the arithmetic is done in <see cref="Int128"/>. The count is
    /// <c>ceil(span / step)</c>, computed without the <c>span + step - 1</c> that
    /// overflows a <see cref="ulong"/>. A range wider than <see cref="long.MaxValue"/>
    /// indices is saturated; the caller cannot run that many and treats the
    /// saturated value as "not expressible".
    /// </remarks>
    private static long CountIndices(long start, long end, long step)
    {
        if (end <= start)
        {
            return 0;
        }

        var span = (Int128)end - start;
        var count = (span + step - 1) / step;
        return count > long.MaxValue ? long.MaxValue : (long)count;
    }

    /// <summary>
    /// Computes <c>start + ordinal * step</c> in <see cref="Int128"/> and clamps it
    /// to <paramref name="end"/>, so a slice boundary that would overflow a
    /// <see cref="long"/> becomes the exclusive end instead of wrapping negative.
    /// </summary>
    /// <param name="start">The first index, inclusive.</param>
    /// <param name="ordinal">The index ordinal within the range.</param>
    /// <param name="step">The positive stride.</param>
    /// <param name="end">The exclusive upper bound to clamp to.</param>
    /// <returns>The slice boundary, never beyond <paramref name="end"/>.</returns>
    private static long SliceBoundary(long start, long ordinal, long step, long end)
    {
        var boundary = (Int128)start + ((Int128)ordinal * step);
        return boundary >= end ? end : (long)boundary;
    }

    /// <summary>
    /// Splits the range into one contiguous slice per participant and runs them:
    /// one slice per rented worker, plus the caller's own slice when the pool
    /// could not supply every wanted worker.
    /// </summary>
    /// <param name="workers">The rented workers; may be empty.</param>
    /// <param name="callerParticipates">Whether the caller runs the trailing slice itself.</param>
    /// <param name="start">The first index, inclusive.</param>
    /// <param name="end">The index one past the last, exclusive.</param>
    /// <param name="count">The number of indices.</param>
    /// <param name="step">The positive stride.</param>
    /// <param name="body">The body to run for each index.</param>
    /// <param name="failures">The call's shared failure record.</param>
    /// <param name="abortAction">The abort action, or <see langword="null"/>.</param>
    private static void RunSlices(
        Worker[] workers,
        bool callerParticipates,
        long start,
        long end,
        long count,
        long step,
        Action<long> body,
        FailureRecord failures,
        Action<Exception>? abortAction)
    {
        // One contiguous slice per participant: the first (count % participants)
        // get one extra index, so the slices differ by at most one. The caller
        // rents at most `count` workers, so every participant gets at least one
        // index.
        var participants = workers.Length + (callerParticipates ? 1 : 0);
        var baseSize = count / participants;
        var remainder = count % participants;

        var cursor = 0L;
        for (var index = 0; index < workers.Length; index++)
        {
            var size = baseSize + (index < remainder ? 1 : 0);

            // A boundary is clamped to `end`, so `start + count * step` — which can
            // exceed long.MaxValue for a range near the type's edge — never wraps.
            var sliceStart = SliceBoundary(start, cursor, step, end);
            var sliceEnd = SliceBoundary(start, cursor + size, step, end);
            workers[index].Dispatch(new Wave.Slice(sliceStart, sliceEnd, step, body, failures, abortAction));
            cursor += size;
        }

        if (callerParticipates)
        {
            var callerSize = baseSize + (workers.Length < remainder ? 1 : 0);
            var sliceStart = SliceBoundary(start, cursor, step, end);
            var sliceEnd = SliceBoundary(start, cursor + callerSize, step, end);
            new Wave.Slice(sliceStart, sliceEnd, step, body, failures, abortAction).Run();
        }
    }

    /// <summary>
    /// Splits the staged tasks into one contiguous run per participant and runs
    /// them: one run per rented worker, plus the caller's own run when the pool
    /// could not supply every wanted worker.
    /// </summary>
    /// <param name="tasks">The staged tasks.</param>
    /// <param name="failures">The wave's shared failure record.</param>
    /// <param name="workers">The rented workers; may be empty.</param>
    /// <param name="callerParticipates">Whether the caller runs the trailing tasks itself.</param>
    private static void DispatchTasks(Action[] tasks, FailureRecord failures, Worker[] workers, bool callerParticipates)
    {
        // A static split over the staged tasks. The caller rents at most
        // `tasks.Length` participants, so every participant gets at least one task.
        var participants = workers.Length + (callerParticipates ? 1 : 0);
        var baseSize = tasks.Length / participants;
        var remainder = tasks.Length % participants;

        var cursor = 0;
        for (var index = 0; index < workers.Length; index++)
        {
            var size = baseSize + (index < remainder ? 1 : 0);

            workers[index].Dispatch(new Wave.Tasks(tasks, cursor, cursor + size, failures));
            cursor += size;
        }

        if (callerParticipates)
        {
            var callerSize = baseSize + (workers.Length < remainder ? 1 : 0);
            new Wave.Tasks(tasks, cursor, cursor + callerSize, failures).Run();
        }
    }

    private static void WaitAll(Worker[] workers)
    {
        foreach (var worker in workers)
        {
            worker.WaitDone();
        }
    }

    private int WantedWorkers(ConcurrencyOptions options)
    {
        if (options.WorkerCount is { } requested)
        {
            // The per-call bound is the ported `workers` argument: 1..256, and an
            // out-of-range value is a programmer error, never silently clamped.
            // The asymmetry with the unbounded pool would otherwise let a script
            // pass int.MaxValue and rent a million threads.
            if (requested is < ConcurrencyConfiguration.MinimumWorkerCount or > ConcurrencyConfiguration.MaximumWorkerCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    requested,
                    $"The per-call worker count must be between {ConcurrencyConfiguration.MinimumWorkerCount} and {ConcurrencyConfiguration.MaximumWorkerCount}.");
            }

            return requested;
        }

        // A steady-state call asks for exactly the configured count, so it reuses
        // the pool instead of growing it.
        return _pool.SteadyStateCount;
    }

    private static void RunPostAbort(ConcurrencyOptions options, FailureRecord failures)
    {
        // The worker-side action already ran inside the failing worker. This second
        // run, after the barrier (or, on the Front path, once the front failure has
        // surfaced) is belt and braces: it covers an abort whose workers had all
        // finished, so a caller-supplied unblock action always runs at least once.
        // A Front call returns while other workers may still be running, so there
        // the action runs once more; it is idempotent by contract.
        if (options.AbortAction is { } abort && failures.FirstFailure is { } failure)
        {
            Body.InvokeAbort(abort, failure);
        }
    }

    /// <summary>
    /// The state one <see cref="AbortMode.Front"/> call hands to a thread-pool
    /// work item: the pool, the workers still draining and the failure record to
    /// dispose once they stop.
    /// </summary>
    /// <param name="Pool">The pool that owns the workers.</param>
    /// <param name="Workers">The workers still running bodies when the call returned.</param>
    /// <param name="Record">The failure record the workers may still write to.</param>
    private sealed record DrainContinuation(WorkerPool Pool, Worker[] Workers, FailureRecord Record);
}
