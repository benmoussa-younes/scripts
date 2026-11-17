// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Numerics;

namespace Core.Concurrency;

/// <summary>
/// One worker's buffer handle, obtained from
/// <see cref="ConcurrencyContext.WorkerBuffer{T}"/>.
/// </summary>
/// <remarks>
/// The handle is owned by one worker for the context's life, so a body may read
/// <see cref="Span"/>, <see cref="Memory"/> and <see cref="Ensure"/> with no lock
/// and no return step. It is never shared between workers.
/// </remarks>
/// <typeparam name="T">The element type.</typeparam>
public sealed class Buffer<T>
{
    private T[] _array;

    internal Buffer(int capacity)
    {
        _array = new T[capacity];
    }

    /// <summary>Gets the number of elements the buffer currently holds.</summary>
    public int Capacity => _array.Length;

    /// <summary>
    /// Gets the whole buffer as a span.
    /// </summary>
    /// <value>A span over the buffer's storage.</value>
    public Span<T> Span => _array.AsSpan();

    /// <summary>Gets the whole buffer as a memory region.</summary>
    /// <value>A memory region over the buffer's storage.</value>
    public Memory<T> Memory => _array;

    /// <summary>
    /// Gets a span of at least <paramref name="length"/> elements, growing the
    /// buffer when it is smaller.
    /// </summary>
    /// <param name="length">The minimum number of elements required.</param>
    /// <returns>A span of at least <paramref name="length"/> elements.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    public Span<T> Ensure(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (_array.Length < length)
        {
            // Grow to the next power of two so repeated growth is amortised and
            // the buffer stops being reallocated once it is large enough. The
            // rounding is done in `long`: `RoundUpToPowerOf2((uint)length)` returns
            // 2^31 for a request above 2^30, which wrapped to a negative `int` and
            // made `new T[negative]` throw an `OverflowException` instead of the
            // natural allocation failure. A request that large cannot be rounded
            // within `int`, so it keeps its exact length and lets the allocation
            // report the truth.
            var rounded = (long)BitOperations.RoundUpToPowerOf2((uint)length);
            var size = rounded > int.MaxValue ? length : (int)rounded;
            _array = new T[Math.Max(size, 1)];
        }

        return _array.AsSpan();
    }
}
