// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Concurrency;

/// <summary>
/// One registered slot and its per-worker values: the value for a worker is
/// created the first time that worker asks for it and reused afterwards, so a
/// worker's state lives exactly as long as the context.
/// </summary>
/// <remarks>
/// <para>
/// The values array is indexed by worker index. A warm read of an entry this
/// worker already published takes no lock: only the owning worker ever writes its
/// own entry, so there is no contention. The dispatcher never touches the array.
/// </para>
/// <para>
/// The worker set grows on demand (design §5), so a worker's index can exceed the
/// steady-state count. The array starts at the steady-state size and a worker
/// that finds its slot beyond the end grows the array to fit. Growth replaces the
/// array with a larger one, so a first-use write through a stale reference would
/// land in a discarded array and lose the value — a broken identity, a leak and a
/// second create. The <em>entire</em> read–grow–create–publish sequence for an
/// entry that is still absent is therefore one critical section: the worker
/// re-reads the live array and writes through it under the lock, so a concurrent
/// grow can never discard a live write. This runs once per worker and never on a
/// warm call, so the lock stays off the hot path.
/// </para>
/// </remarks>
internal sealed class Slot
{
    private readonly Func<int, object?> _create;
    private readonly Action<object>? _destroy;
    private readonly object _grow = new();

    private object?[] _values;

    internal Slot(string name, int initialCount, Func<int, object?> create, Action<object>? destroy)
    {
        Name = name;
        _create = create;
        _destroy = destroy;
        _values = new object?[initialCount];
    }

    /// <summary>Gets the diagnostic name of the slot.</summary>
    internal string Name { get; }

    /// <summary>
    /// Gets the calling worker's value, creating it on first use.
    /// </summary>
    /// <param name="workerIndex">The calling worker's index.</param>
    /// <returns>The value for that worker.</returns>
    internal object? Get(int workerIndex)
    {
        // Warm path, no lock: the entry was already published by this worker. The
        // array never shrinks, so once this worker's index fits it always will.
        var values = Volatile.Read(ref _values);
        if (workerIndex < values.Length)
        {
            var warm = Volatile.Read(ref values[workerIndex]);
            if (warm is not null)
            {
                return warm;
            }
        }

        // Cold path, one critical section for the whole read-grow-create-publish:
        // the array may be replaced by a concurrent grow at any moment, so a write
        // through a stale reference would be discarded (losing the value, breaking
        // per-worker identity and leaking). Re-reading the live array and writing
        // through it under the lock makes the grow and the first-use write atomic
        // with respect to each other. This runs once per worker, never on a warm
        // call, so the lock is off the hot path.
        lock (_grow)
        {
            values = GrowToFitLocked(workerIndex + 1);

            // No null check here: only the owning worker ever writes its own index,
            // and a grow copies the old entries without fabricating one, so this
            // index is always empty at this point. The warm path above already
            // returned a value another call had published.
            var grown = _create(workerIndex);
            Volatile.Write(ref values[workerIndex], grown);
            return grown;
        }
    }

    /// <summary>
    /// Disposes every worker's value, once per worker.
    /// </summary>
    internal void DisposeValues()
    {
        if (_destroy is null)
        {
            return;
        }

        // Every grow happens under _grow, and Get's fast path never writes an
        // index past the live length, so no create can race this read of the
        // final array once the pool has been joined.
        var values = Volatile.Read(ref _values);

        for (var index = 0; index < values.Length; index++)
        {
            if (Volatile.Read(ref values[index]) is { } value)
            {
                _destroy(value);
            }
        }
    }

    /// <summary>
    /// Grows the value array so <paramref name="length"/> worker indices fit,
    /// returning the current array when it already does.
    /// </summary>
    /// <param name="length">The minimum array length required.</param>
    /// <returns>The array to use, at least <paramref name="length"/> long.</returns>
    internal object?[] GrowToFit(int length)
    {
        lock (_grow)
        {
            return GrowToFitLocked(length);
        }
    }

    /// <summary>
    /// Grows the value array while the caller already holds <c>_grow</c>.
    /// </summary>
    /// <param name="length">The minimum array length required.</param>
    /// <returns>The array to use, at least <paramref name="length"/> long.</returns>
    private object?[] GrowToFitLocked(int length)
    {
        var values = Volatile.Read(ref _values);

        if (length <= values.Length)
        {
            return values;
        }

        var grown = new object?[length];
        Array.Copy(values, grown, values.Length);
        Volatile.Write(ref _values, grown);
        return grown;
    }
}
