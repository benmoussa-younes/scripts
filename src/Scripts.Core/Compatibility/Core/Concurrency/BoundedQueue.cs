// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later


namespace Core.Concurrency;

/// <summary>
/// A bounded, close-based producer/consumer queue: producers <see cref="Push"/>
/// and block when the queue is full, consumers <see cref="Pop"/> and block when
/// it is empty, and <see cref="Close"/> lets the consumers drain what is left and
/// then see <see langword="false"/>.
/// </summary>
/// <remarks>
/// <para>
/// The bound is the point: a producer cannot run ahead of its consumers without
/// limit, so a parallel walk that discovers work cannot exhaust memory, and the
/// discovery pushes apply back-pressure to the walk itself.
/// </para>
/// <para>
/// The queue is the one structure the library deliberately shares between
/// workers; it is the mechanism behind a parallel walk and any dynamic work
/// generation, where the work is discovered while it runs and cannot be split up
/// front. It is safe for any number of concurrent producers and consumers.
/// </para>
/// <para>
/// A blocked <see cref="Push"/> returns <see langword="false"/> as soon as the
/// queue is closed; a blocked <see cref="Pop"/> returns <see langword="false"/>
/// only once the queue is closed <em>and</em> empty, so a close never loses an
/// item a producer already pushed.
/// </para>
/// </remarks>
/// <typeparam name="T">The element type.</typeparam>
public sealed class BoundedQueue<T> : IDisposable
{
    private readonly Queue<T> _items;
    private readonly object _gate = new();
    private readonly int _capacity;

    private bool _closed;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="BoundedQueue{T}"/> class.
    /// </summary>
    /// <param name="capacity">The largest number of items the queue may hold; 1 to <see cref="MaximumCapacity"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not between 1 and <see cref="MaximumCapacity"/>.</exception>
    public BoundedQueue(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        // A capacity the backing array cannot hold would surface later as an
        // OutOfMemoryException from the grow; reject it here as the programmer
        // error it is, with the bound the queue actually supports.
        if (capacity > MaximumCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                capacity,
                $"The capacity must be between 1 and {MaximumCapacity}.");
        }

        _capacity = capacity;
        _items = new Queue<T>(capacity);
    }

    /// <summary>
    /// The largest capacity a queue may be created with: the largest array a
    /// <see cref="Queue{T}"/> can grow to, so a larger request is a programmer
    /// error rather than a delayed <see cref="OutOfMemoryException"/>.
    /// </summary>
    public const int MaximumCapacity = 0X7FEFFFFF;

    /// <summary>
    /// Gets the largest number of items the queue may hold.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Gets the number of items currently held.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the queue is closed: no further
    /// <see cref="Push"/> can succeed.
    /// </summary>
    public bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    /// <summary>
    /// Adds an item, blocking while the queue is full.
    /// </summary>
    /// <param name="item">The item to add.</param>
    /// <returns><see langword="true"/> when the item was added; <see langword="false"/> when the queue was closed first.</returns>
    /// <exception cref="ObjectDisposedException">The queue is disposed.</exception>
    public bool Push(T item)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            while (_items.Count == _capacity && !_closed)
            {
                Monitor.Wait(_gate);
            }

            if (_closed)
            {
                return false;
            }

            _items.Enqueue(item);
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    /// <summary>
    /// Removes the oldest item, blocking while the queue is empty and not closed.
    /// </summary>
    /// <param name="item">The removed item when one was available.</param>
    /// <returns>
    /// <see langword="true"/> when an item was removed; <see langword="false"/> when
    /// the queue is closed and empty.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The queue is disposed.</exception>
    public bool Pop(out T item)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            while (_items.Count == 0 && !_closed)
            {
                Monitor.Wait(_gate);
            }

            if (_items.Count == 0)
            {
                item = default!;
                return false;
            }

            item = _items.Dequeue();
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    /// <summary>
    /// Closes the queue: no further <see cref="Push"/> succeeds and a
    /// <see cref="Pop"/> drains the items that are already queued before it
    /// returns <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Closing is idempotent. Every blocked producer and consumer is woken, so a
    /// close never leaves a thread waiting.
    /// </remarks>
    public void Close()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Disposes the queue: it is closed, its items are dropped and every blocked
    /// producer and consumer is woken. A later <see cref="Push"/> or
    /// <see cref="Pop"/> throws.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _closed = true;
            _items.Clear();
            Monitor.PulseAll(_gate);
        }
    }
}
