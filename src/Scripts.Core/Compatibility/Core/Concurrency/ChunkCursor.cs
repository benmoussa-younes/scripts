// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Concurrency;

/// <summary>
/// The atomic cursor that hands out items for a claim-based
/// <see cref="WorkPartition"/> call. It hands out a <em>chunk</em> of
/// <c>chunkSize</c> consecutive ordinals per atomic, so the shared atomic is paid
/// once per chunk instead of once per item.
/// </summary>
/// <remarks>
/// <para>
/// A chunk size of 1 is the design's original claim-one cursor (one atomic per
/// item, prompt abort, maximum contention). A larger chunk amortises the atomic
/// and the <c>Int128</c> index arithmetic over <c>chunkSize</c> items, which is
/// what closes the contention gap with a chunked partitioner while keeping the
/// dynamic (work-conserving) property claim-one has and static slices lack. The
/// spike measured the shared atomic fall from 33 ns to 16.6 ns per item at the
/// default chunk of 64.
/// </para>
/// <para>
/// The chunk is a contiguous run of ordinals; the indices it maps to are spaced
/// by <c>step</c> so a strided range stays strided.
/// </para>
/// </remarks>
internal sealed class ChunkCursor
{
    private readonly long _start;
    private readonly long _count;
    private readonly long _step;

    private long _claimed;

    internal ChunkCursor(long start, long count, long step)
    {
        _start = start;
        _count = count;
        _step = step;
    }

    /// <summary>Gets the stride between consecutive indices.</summary>
    internal long Step => _step;

    /// <summary>
    /// Claims the next chunk of ordinals.
    /// </summary>
    /// <param name="chunkSize">The number of ordinals to claim; must be positive.</param>
    /// <param name="firstOrdinal">The first claimed ordinal.</param>
    /// <param name="length">The number of ordinals actually claimed (1..<paramref name="chunkSize"/>).</param>
    /// <returns><see langword="true"/> when at least one ordinal was claimed.</returns>
    internal bool TryClaim(int chunkSize, out long firstOrdinal, out int length)
    {
        var claimed = Interlocked.Add(ref _claimed, chunkSize) - chunkSize;

        if (claimed >= _count)
        {
            firstOrdinal = 0;
            length = 0;
            return false;
        }

        var remaining = _count - claimed;
        length = remaining < chunkSize ? (int)remaining : chunkSize;
        firstOrdinal = claimed;
        return true;
    }

    /// <summary>
    /// Maps an ordinal to its index, computing <c>start + ordinal * step</c> in
    /// <see cref="Int128"/> so a range near the type's edge never wraps.
    /// </summary>
    /// <param name="ordinal">The ordinal within the range.</param>
    /// <returns>The index for that ordinal.</returns>
    internal long IndexAt(long ordinal) => (long)((Int128)_start + ((Int128)ordinal * _step));
}
