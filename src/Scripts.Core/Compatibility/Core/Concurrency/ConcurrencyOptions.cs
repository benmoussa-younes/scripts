// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Concurrency;

/// <summary>
/// The per-call options of a <see cref="ConcurrencyContext.For(long, long, long, Action{long}, ConcurrencyOptions)"/>
/// or <see cref="ConcurrencyContext.Run(Action, ConcurrencyOptions)"/> call: the
/// partition, the failure policy and an optional per-call worker bound.
/// </summary>
/// <remarks>
/// This sizes <em>one call</em>, not the context. The context's lifetime
/// counterpart is <see cref="ConcurrencyConfiguration"/>, which sizes the worker
/// pool, the stack and the ceiling. The per-call bound here exists because a
/// caller sometimes knows better than the context's default: a script's
/// <c>workers</c> argument wins for that call, and the context's configured count
/// applies only when the caller says nothing. A per-call bound never lifts the
/// context's workload-aware ceiling
/// (<see cref="ConcurrencyConfiguration.MaximumPoolSize"/>).
/// </remarks>
public sealed record ConcurrencyOptions
{
    /// <summary>
    /// Gets how the call splits its items across the workers.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="WorkPartition.ClaimChunk"/>: it is the measured
    /// throughput winner (16.6 ns/item of shared-atomic cost versus <c>Parallel.For</c>'s
    /// 17.0) and, unlike <see cref="WorkPartition.StaticChunks"/>, it keeps the
    /// lowest-index failure guarantee deterministic.
    /// </remarks>
    public WorkPartition Partition { get; init; } = WorkPartition.ClaimChunk;

    /// <summary>
    /// Gets the number of consecutive ordinals one atomic claim hands out for
    /// <see cref="WorkPartition.ClaimChunk"/>.
    /// </summary>
    /// <remarks>
    /// Larger chunks amortise the shared atomic further but widen the abort
    /// granularity (a chunk in flight runs to its end). 1 makes
    /// <see cref="WorkPartition.ClaimChunk"/> identical to
    /// <see cref="WorkPartition.ClaimOne"/>.
    /// </remarks>
    public int ClaimChunkSize { get; init; } = DefaultClaimChunkSize;

    /// <summary>The default claim-chunk size: 64 items per atomic.</summary>
    public const int DefaultClaimChunkSize = 64;

    /// <summary>
    /// Gets how the call reacts when a body fails and
    /// <see cref="CollectAllFailures"/> is <see langword="false"/>.
    /// </summary>
    public AbortMode AbortMode { get; init; } = AbortMode.Drain;

    /// <summary>
    /// Gets a value indicating whether every failure is collected instead of
    /// stopping at the first one.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/> the call runs to completion and
    /// <see cref="ConcurrencyResult.Failures"/> holds every failure, in item-index
    /// order. When <see langword="false"/> (the default) the first failure stops
    /// new items from being claimed and only
    /// <see cref="ConcurrencyResult.FirstFailure"/> is meaningful.
    /// </remarks>
    public bool CollectAllFailures { get; init; }

    /// <summary>
    /// Gets an action run inside a worker after its body failed, before the worker
    /// reports the failure.
    /// </summary>
    /// <remarks>
    /// The action runs in the failing worker and again, once, on the caller's
    /// thread after the barrier. Its purpose is to unblock the other workers: a
    /// body that launched a child process, or that waits on an external resource,
    /// is released here so the abort does not wait for the wave to drain. The
    /// action receives the exception the body threw and must not throw: a throwing
    /// abort action is ignored, because the abort is already running.
    /// </remarks>
    public Action<Exception>? AbortAction { get; init; }

    /// <summary>
    /// Gets a per-call worker bound; when set it wins over the context's count.
    /// </summary>
    /// <remarks>
    /// The bound caps the number of workers that take part in this one call. It is
    /// the ported <c>workers</c> argument: <see cref="ConcurrencyConfiguration.MinimumWorkerCount"/>
    /// (1) to <see cref="ConcurrencyConfiguration.MaximumWorkerCount"/> (256), honoured as
    /// given and never silently clamped; a value outside that range is a programmer
    /// error and <see cref="ConcurrencyContext.For(long, long, long, Action{long}, ConcurrencyOptions)"/>
    /// or <see cref="ConcurrencyContext.Run(Action, ConcurrencyOptions)"/> throws. The
    /// call reuses idle pool workers and, when the pool has fewer free than the
    /// bound (a nested call), it runs the deficit <em>on the calling thread</em>:
    /// the pool is capped at the context's workload-aware ceiling
    /// (<see cref="ConcurrencyConfiguration.MaximumPoolSize"/>, physical for CPU-bound work
    /// and logical for I/O-bound work), so a nested call shares the budget instead
    /// of multiplying it (design §4a).
    /// </remarks>
    public int? WorkerCount { get; init; }

    /// <summary>
    /// Gets the default per-call options: chunked claim, stop on the first
    /// failure, no abort action and no per-call bound.
    /// </summary>
    public static ConcurrencyOptions Default { get; } = new();
}
