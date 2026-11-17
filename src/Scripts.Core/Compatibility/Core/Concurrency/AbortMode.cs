// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Concurrency;

/// <summary>
/// Selects what a <see cref="ConcurrencyContext.For(long, long, long, Action{long}, ConcurrencyOptions)"/>
/// or <see cref="ConcurrencyContext.Run(Action, ConcurrencyOptions)"/> call does with the
/// work already in flight when a body fails.
/// </summary>
public enum AbortMode
{
    /// <summary>
    /// Stop claiming new items, let every worker finish the item it is running,
    /// and return once the whole wave has drained. The call returns only when no
    /// worker of this call is running a body.
    /// </summary>
    /// <remarks>
    /// This is the strong contract: after the call returns, nothing it started is
    /// still running, so a caller can safely dispose the context or reuse the
    /// resources the bodies touched. Its cost is one item's duration per worker
    /// that was mid-item when the failure happened.
    /// </remarks>
    Drain = 0,

    /// <summary>
    /// Stop claiming new items and return as soon as the first failure is
    /// recorded, without waiting for the items already in flight to finish.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This matches the "stop dispatching" behaviour of <c>Parallel.For</c>: a
    /// failure at the front of the range surfaces in microseconds instead of one
    /// item's duration. It is the mode to choose when the body is blocked on an
    /// external resource and the caller is going to release that resource
    /// (through <see cref="ConcurrencyOptions.AbortAction"/>) and then abandon the
    /// call anyway.
    /// </para>
    /// <para>
    /// <b>The contract is weaker.</b> The call returns while workers may still be
    /// running bodies; those bodies run to completion on the pool in the
    /// background, and their failures are discarded. A caller that disposes the
    /// context or shares mutable state with the bodies must use
    /// <see cref="Drain"/>. The lowest-index failure is still the one surfaced: the
    /// claim-one cursor hands out ordinals in strictly increasing order, so when the
    /// first failure is not the front-most ordinal the call waits for the items
    /// already in flight before publishing the result — a lower-indexed item claimed
    /// earlier may still fail after the first signal and must be the one reported. A
    /// failure <em>at</em> the front-most ordinal returns at once, because no lower
    /// ordinal exists to still fail.
    /// </para>
    /// <para>
    /// <b>Pair it with <see cref="WorkPartition.ClaimOne"/>.</b> A chunked
    /// partition runs a whole chunk before it observes the stop flag, so a late
    /// fault behind a large chunk is only reached at the next chunk boundary —
    /// measured at 318 ms versus 35 ms for claim-one in the spike. The library
    /// therefore runs <see cref="Front"/> on the claim-one arm even when a chunked
    /// partition was selected, so the mode's promptness does not depend on the
    /// chunk size.
    /// </para>
    /// </remarks>
    Front = 1,
}
