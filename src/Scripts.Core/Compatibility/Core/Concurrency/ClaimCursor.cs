// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Concurrency;

/// <summary>
/// The atomic cursor that hands out one item at a time for a
/// <see cref="WorkPartition.ClaimOne"/> call.
/// </summary>
/// <remarks>
/// One <see cref="Interlocked.Increment(ref long)"/> per claim, so a worker
/// observes the range end without a lock and no two workers ever receive the
/// same index. The cursor is created per call and dropped when the call returns.
/// </remarks>
internal sealed class ClaimCursor
{
    private readonly long _start;
    private readonly long _count;
    private readonly long _step;

    private long _claimed;

    internal ClaimCursor(long start, long count, long step)
    {
        _start = start;
        _count = count;
        _step = step;
    }

    /// <summary>
    /// Claims the next index.
    /// </summary>
    /// <param name="index">The claimed index when one was left.</param>
    /// <returns><see langword="true"/> when an index was claimed; otherwise <see langword="false"/>.</returns>
    internal bool TryClaim(out long index)
    {
        var claimed = Interlocked.Increment(ref _claimed) - 1;

        if (claimed >= _count)
        {
            index = 0;
            return false;
        }

        // `claimed * step` can exceed long.MaxValue for a range near the type's
        // edge (a negative start with a huge step), so the offset is computed in
        // Int128 and cast only when it fits the index it produces.
        index = (long)((Int128)_start + ((Int128)claimed * _step));
        return true;
    }
}
