// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Concurrency;

/// <summary>
/// The state shared by the workers of one wave: whether every failure is
/// collected, whether a failure has stopped the run, and the failures themselves.
/// </summary>
/// <remarks>
/// <para>
/// The dispatcher creates one record per call and hands the same reference to
/// every worker. The record is the only shared mutable state on the
/// <c>For</c>/<c>Run</c> path.
/// </para>
/// <para>
/// The stop flag is one volatile field written once by the failing worker and
/// read by every worker before each claim (or at the top of each slice
/// iteration). The read is volatile so the flag is eventually seen; the
/// correctness contract is per-observation, not per-clock-instant: a worker that
/// has already observed the flag claims nothing further, while a worker that has
/// not yet looked — a static slice faster than the publication — may finish the
/// work it is already inside.
/// </para>
/// <para>
/// <see cref="FirstFailure"/> is the failure with the <em>lowest item index</em>,
/// not the one that reached the record first: workers fail in a
/// scheduling-dependent order, so completion order would make the surfaced error
/// nondeterministic. The ordinal is the <c>For</c> index or, for a staged-task
/// wave, the task's staging position; both order the call's items the way the
/// caller listed them.
/// </para>
/// </remarks>
internal sealed class FailureRecord : IDisposable
{
    private readonly List<Failure> _failures = [];
    private readonly object _sync = new();
    private readonly ManualResetEventSlim _failed = new(false);

    private volatile int _stopped;

    internal FailureRecord(bool collectAll)
    {
        CollectAll = collectAll;
    }

    /// <summary>Gets a value indicating whether every failure is collected.</summary>
    internal bool CollectAll { get; }

    /// <summary>
    /// Gets a signal that is set as soon as the first failure is recorded, and its
    /// <see cref="WaitHandle"/> form for a multi-condition wait.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="AbortMode.Front"/> to let the caller return without
    /// waiting for the items already in flight. The signal stays set once raised,
    /// so a caller that races the record sees it immediately.
    /// </remarks>
    internal ManualResetEventSlim Failed => _failed;

    /// <summary>Gets the first-failure signal as a waitable handle.</summary>
    internal WaitHandle FailedHandle => _failed.WaitHandle;

    /// <summary>
    /// Releases the first-failure signal. Called once per call, after the last
    /// worker that may record into this record has stopped.
    /// </summary>
    public void Dispose() => _failed.Dispose();

    /// <summary>Gets a value indicating whether a failure has stopped the run.</summary>
    internal bool ShouldKeepGoing => _stopped == 0;

    /// <summary>Gets every failure recorded so far, in item-index order.</summary>
    internal IReadOnlyList<Exception> Failures
    {
        get
        {
            lock (_sync)
            {
                return [.. _failures.OrderBy(failure => failure.Index).Select(failure => failure.Exception)];
            }
        }
    }

    /// <summary>
    /// Gets the failure with the lowest item index, or <see langword="null"/> when
    /// none failed.
    /// </summary>
    /// <remarks>
    /// The lowest index is the caller's item order (the <c>For</c> index or the
    /// staged-task position), never the workers' completion order, so the surfaced
    /// error is deterministic across runs.
    /// </remarks>
    internal Exception? FirstFailure
    {
        get
        {
            lock (_sync)
            {
                Exception? lowest = null;
                var lowestIndex = long.MaxValue;

                foreach (var failure in _failures)
                {
                    if (failure.Index < lowestIndex)
                    {
                        lowestIndex = failure.Index;
                        lowest = failure.Exception;
                    }
                }

                return lowest;
            }
        }
    }

    /// <summary>
    /// Gets the item index of the lowest-index recorded failure, or
    /// <see langword="null"/> when none has been recorded.
    /// </summary>
    /// <remarks>
    /// Used by the <see cref="AbortMode.Front"/> path to decide whether the
    /// first-recorded failure is the lowest possible one: only when it is (the
    /// front-most ordinal of a claim-one call) can Front return without waiting for
    /// the in-flight workers, because no lower ordinal exists to still fail.
    /// </remarks>
    internal long? FirstFailureIndex
    {
        get
        {
            lock (_sync)
            {
                long? lowest = null;

                foreach (var failure in _failures)
                {
                    if (lowest is null || failure.Index < lowest)
                    {
                        lowest = failure.Index;
                    }
                }

                return lowest;
            }
        }
    }

    /// <summary>
    /// Records a failure and, unless every failure is collected, stops the run.
    /// </summary>
    /// <param name="index">The failing item's index (the range index or the staged-task position).</param>
    /// <param name="failure">The failure the body threw.</param>
    internal void Record(long index, Exception failure)
    {
        lock (_sync)
        {
            _failures.Add(new Failure(index, failure));
        }

        if (!CollectAll)
        {
            Interlocked.Exchange(ref _stopped, 1);

            // Raise the front-abort signal: set after the failure is published, so
            // a caller that races the record sees a consistent FirstFailure.
            _failed.Set();
        }
    }

    /// <summary>
    /// Builds the result this record describes.
    /// </summary>
    /// <returns>The call's result: completed with no failure, aborted on the
    /// lowest-index failure, or faulted with every failure when they are collected.</returns>
    internal ConcurrencyResult ToResult()
    {
        if (Failures.Count == 0)
        {
            return ConcurrencyResult.Completed();
        }

        return CollectAll
            ? ConcurrencyResult.Collected(Failures)
            : ConcurrencyResult.Aborted(FirstFailure!);
    }

    /// <summary>One recorded failure and the index of the item that threw it.</summary>
    private readonly record struct Failure(long Index, Exception Exception);
}
