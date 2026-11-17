// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Concurrency;

/// <summary>
/// One dedicated worker thread with its own gate: the handshake the dispatcher
/// uses to give it a wave and the worker uses to report the wave done.
/// </summary>
/// <remarks>
/// <para>
/// This is the direct-dispatch model the design ports from C. There is no shared
/// work queue on the <c>For</c>/<c>Submit</c> path: the dispatcher publishes the
/// wave into this worker's own field, signals this worker's own event and then
/// waits on this worker's own done flag. A worker therefore contends with no
/// other worker, which is what makes a tiny call cheap.
/// </para>
/// <para>
/// Both directions <em>spin before they block</em> (the spirit of the C library's
/// <c>BC_SPIN_COUNT</c>): a warm worker finishes a trivial wave during the
/// dispatcher's spin, so the dispatcher never pays a futex transition; a worker
/// sees the next wave during its own spin, so the dispatcher never pays a signal
/// to wake it either. This is the measured difference between ≈ 0.9 µs and
/// ≈ 60 µs per trivial call (spike §4/§7). Only a wave slower than the spin
/// blocks either side, and then the event carries the wait.
/// </para>
/// <para>
/// The wave is published through a volatile reference and the done flag is a
/// volatile bool, so the fast path takes no lock; the two
/// <see cref="ManualResetEventSlim"/>s are the slow path and carry the
/// release/acquire through the kernel when a wait really blocks.
/// </para>
/// </remarks>
internal sealed class Worker : IDisposable
{
    /// <summary>
    /// The number of spin iterations before a wait blocks. Ported from the C
    /// library's <c>BC_SPIN_COUNT</c> default; the spike measured a trivial call
    /// completing within it.
    /// </summary>
    private const int SpinCount = 2_000;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _done = new(false);

    // The wave handshake. `_wave` is published before `_ready` is set; the worker
    // loads `_wave` after it observes `_ready`.
    private volatile Wave? _wave;
    private volatile bool _stopping;

    private bool _disposed;

    // True once the thread has ended and its events were released.
    private bool _joined;

    internal Worker(int index, int stackBytes, bool pinToCpu = false)
    {
        Index = index;
        PinToCpu = pinToCpu;
        _thread = new Thread(Run, stackBytes)
        {
            IsBackground = true,
            Name = $"Core.Concurrency.Worker[{index}]",
        };
    }

    /// <summary>Gets this worker's stable index, 0..count-1.</summary>
    internal int Index { get; }

    /// <summary>
    /// Gets a value indicating whether this worker asks, best-effort, to be pinned
    /// to a processor when its thread starts.
    /// </summary>
    internal bool PinToCpu { get; }

    /// <summary>Gets the managed thread this worker runs on.</summary>
    internal Thread Thread => _thread;

    /// <summary>
    /// Starts the worker thread.
    /// </summary>
    internal void Start() => _thread.Start();

    /// <summary>
    /// Hands this worker its part of a wave and wakes it.
    /// </summary>
    /// <param name="wave">The part this worker runs.</param>
    internal void Dispatch(Wave wave)
    {
        _done.Reset();
        _wave = wave;
        _ready.Set();
    }

    /// <summary>
    /// Gets a value indicating whether this worker has finished the wave it was
    /// last handed (a worker never handed one counts as done).
    /// </summary>
    internal bool IsDone => _done.IsSet;

    /// <summary>
    /// Gets a handle set when this worker finishes its wave, for a caller that
    /// waits on several conditions at once (the <see cref="AbortMode.Front"/>
    /// path).
    /// </summary>
    internal WaitHandle DoneHandle => _done.WaitHandle;

    /// <summary>
    /// Waits until this worker has finished the wave it was handed.
    /// </summary>
    /// <remarks>
    /// Spins first — a warm worker finishes a trivial wave during the spin — and
    /// only then blocks. The spin reads the event's flag, so it takes no lock and
    /// causes no transition on the fast path.
    /// </remarks>
    internal void WaitDone()
    {
        for (var spin = 0; spin < SpinCount; spin++)
        {
            if (_done.IsSet)
            {
                return;
            }

            Thread.SpinWait(1);
        }

        _done.Wait();
    }

    /// <summary>
    /// Asks the worker to leave its loop, joins it and releases its events.
    /// Idempotent. The join is unbounded; <see cref="Dispose(TimeSpan)"/> is the
    /// bounded form used by <see cref="WorkerPool.Dispose"/>.
    /// </summary>
    public void Dispose() => Dispose(Timeout.InfiniteTimeSpan);

    /// <summary>
    /// Asks the worker to leave its loop and joins it for at most
    /// <paramref name="timeout"/>. Idempotent.
    /// </summary>
    /// <param name="timeout">The longest time to wait for the thread to end.</param>
    /// <returns>
    /// <see langword="true"/> when the thread has ended, so its events may be
    /// released; <see langword="false"/> when the thread is still running a body
    /// (a blocked or misbehaving body), in which case its events are left alone:
    /// the thread is a background thread, so it cannot keep the process alive, and
    /// disposing the events a running thread still waits on would make it throw.
    /// </returns>
    public bool Dispose(TimeSpan timeout)
    {
        if (_disposed)
        {
            return _joined;
        }

        _disposed = true;
        _stopping = true;
        _ready.Set();
        _joined = _thread.Join(timeout);
        if (!_joined)
        {
            return false;
        }

        _ready.Dispose();
        _done.Dispose();
        return true;
    }

    private void Run()
    {
        if (PinToCpu)
        {
            // Best-effort and advisory: a refusal leaves the worker unpinned.
            _ = Platform.Linux.ProcessorTopology.PinCurrentThread(Index);
        }

        while (true)
        {
            // Spin for a wave before blocking: a back-to-back call reaches a
            // worker still warm from the previous wave without a wake.
            var seen = false;
            for (var spin = 0; spin < SpinCount; spin++)
            {
                if (_ready.IsSet)
                {
                    seen = true;
                    break;
                }

                Thread.SpinWait(1);
            }

            if (!seen)
            {
                _ready.Wait();
            }

            if (_stopping)
            {
                return;
            }

            var wave = _wave!;
            _wave = null;
            _ready.Reset();

            wave.Run();

            // Publish done; the waiter's spin observes it, and the Set wakes a
            // waiter that already blocked.
            _done.Set();
        }
    }
}
