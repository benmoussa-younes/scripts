// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Concurrency;

/// <summary>
/// A per-worker buffer pool: one reusable array per worker, created once and
/// handed back automatically, so a body never allocates a scratch buffer.
/// </summary>
/// <remarks>
/// <para>
/// This is the library's first-class pooling facility. A caller registers a
/// buffer pool on its context and reads its worker's buffer with
/// <see cref="ConcurrencyContext.WorkerBuffer{T}"/>; because the buffer belongs to
/// the worker, a body can hold it for the whole call without any rent/return
/// bookkeeping and without a lock. Two bodies on two workers therefore never
/// share a buffer, which is exactly the isolation a scratch buffer needs.
/// </para>
/// <para>
/// It replaces the hand-rolled use of <c>ArrayPool&lt;T&gt;.Shared</c> inside a
/// body: that pattern rents and returns from the shared pool per item (a lock on
/// the shared pool's hot path) and, if the return is missed, leaks. Here the array
/// is owned by the worker for its whole life, so the hot path is a field read. The
/// spike measured the pooled body at 20.5 ns/item against 41.9 for
/// <c>ArrayPool.Shared</c> — about 2× — with 0 B allocated per item.
/// </para>
/// <para>
/// The buffer is grown on demand and kept at its largest size: a caller asks for
/// <see cref="Buffer{T}.Ensure"/> of at least N elements and gets the worker's
/// array, enlarged if it is smaller. There is exactly one buffer per worker per
/// pool, so a body that needs two independent scratch regions registers two pools.
/// A worker created later (a grown pool) gets its buffer on its own first use.
/// </para>
/// </remarks>
/// <typeparam name="T">The element type.</typeparam>
public sealed class WorkerBufferPool<T> : IDisposable
{
    private readonly SlotRegistration _slot;

    internal WorkerBufferPool(SlotRegistration slot, int initialCapacity, int workerCount)
    {
        _slot = slot;
        InitialCapacity = initialCapacity;
        WorkerCount = workerCount;
    }

    /// <summary>Gets the smallest capacity, in elements, every worker's buffer starts with.</summary>
    public int InitialCapacity { get; }

    /// <summary>
    /// Gets the number of buffers the pool sizes to at creation: one per
    /// steady-state worker.
    /// </summary>
    public int WorkerCount { get; }

    /// <summary>Gets the registration the context reads the per-worker buffers through.</summary>
    internal SlotRegistration Registration => _slot;

    /// <summary>
    /// Disposes the pool. The buffers are managed arrays, so there is nothing to
    /// release; the values are dropped by the context's slot disposal.
    /// </summary>
    public void Dispose()
    {
        // Managed arrays need no explicit release; the method exists so a caller
        // can dispose a pool in a using block symmetrically with the context.
    }
}
