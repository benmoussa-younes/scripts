// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Concurrency;

/// <summary>
/// Selects how a <see cref="ConcurrencyContext.For(long, long, long, Action{long}, ConcurrencyOptions)"/>
/// or <see cref="ConcurrencyContext.Run(Action)"/> call splits its items across the workers.
/// </summary>
public enum WorkPartition
{
    /// <summary>
    /// Splits the range into one contiguous slice per worker up front. Every
    /// worker walks its own slice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not the default, and avoid it for fault reporting.</b> A failure stops
    /// the worker that failed and stops the other slices only when each worker
    /// <em>observes</em> the shared flag at its next loop top — and there is no
    /// observation between a failure's publication and a fixed slice's tail. The
    /// consequence is that the <em>surfaced</em> failure is whichever slice
    /// recorded first, not necessarily the lowest index: the spike measured a
    /// three-fault static-chunk map surfacing item 40 instead of item 10 in
    /// <b>30/30</b> runs. It therefore violates the lowest-index contract and
    /// must not be the default. <see cref="ClaimChunk"/> gives the same
    /// throughput while keeping the lowest-index guarantee.
    /// </para>
    /// <para>
    /// The split stays useful where a range is uniform and no failure is expected
    /// (a partition with no shared atomic at all), and where a caller explicitly
    /// wants a fixed up-front split.
    /// </para>
    /// </remarks>
    StaticChunks = 0,

    /// <summary>
    /// Hands out one item at a time from an atomic cursor. Every claim is a
    /// single atomic increment, so a failure is observed by the other workers
    /// almost immediately and no worker claims a new item afterwards. This is
    /// the split to choose when the items vary in cost or when the promptest
    /// abort matters more than the claim cost.
    /// </summary>
    /// <remarks>
    /// One shared atomic per item, so this pays the most contention (measured at
    /// 33 ns/item versus 16.6 for <see cref="ClaimChunk"/>); it is the price of a
    /// per-item prompt abort. Pair it with <see cref="AbortMode.Front"/> for the
    /// promptest abort.
    /// </remarks>
    ClaimOne = 1,

    /// <summary>
    /// Hands out a fixed-size chunk of items per atomic claim. This is the
    /// hybrid of <see cref="StaticChunks"/> and <see cref="ClaimOne"/>: the
    /// atomic is paid once per chunk (so its contention cost is divided by the
    /// chunk size) while the work stays dynamically balanced, because a worker
    /// that finishes its chunk claims another. The chunk size is
    /// <see cref="ConcurrencyOptions.ClaimChunkSize"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the default: it is the measured best of the three (33 → 16.6
    /// ns/item of shared-atomic cost, beating <c>Parallel.For</c>'s 17.0) while
    /// keeping the lowest-index failure guarantee that <see cref="StaticChunks"/>
    /// breaks. The claim order is atomic, so a lower ordinal is always claimed
    /// before a higher one and the lowest failing index is deterministic.
    /// </para>
    /// <para>
    /// The abort guarantee is between the other two: a chunk already in flight
    /// runs to its end, so a failure is honoured at the next chunk boundary. With
    /// the default chunk size of 64 that is a bounded, small delay for a large
    /// reduction in atomic traffic; set the size to 1 for claim-one semantics or
    /// keep <see cref="ClaimOne"/> to be explicit.
    /// </para>
    /// </remarks>
    ClaimChunk = 2,
}
