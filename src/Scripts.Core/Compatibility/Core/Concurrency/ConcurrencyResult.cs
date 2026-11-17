// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Concurrency;

/// <summary>
/// The outcome of a <see cref="ConcurrencyContext.For(long, long, long, Action{long})"/>
/// or <see cref="ConcurrencyContext.Run(Action)"/> call: how it ended, the first failure and
/// every failure when the call collected them.
/// </summary>
/// <remarks>
/// <para>
/// The failures are the <see cref="Exception"/>s the caller's body threw, kept
/// by reference. The library never wraps them and never throws them itself: a
/// failure is data on this result, so a caller decides whether to rethrow, log
/// or ignore. This is why the API returns a result rather than throwing the
/// first failure.
/// </para>
/// <para>
/// A failure list is empty for a <see cref="ConcurrencyStatus.Completed"/> call and
/// holds at least one entry otherwise.
/// </para>
/// </remarks>
public sealed class ConcurrencyResult
{
    private static readonly IReadOnlyList<Exception> NoFailures = [];

    private readonly IReadOnlyList<Exception> _failures;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrencyResult"/> class.
    /// </summary>
    /// <param name="status">How the call ended.</param>
    /// <param name="failures">Every failure the call recorded, in item-index order; empty when none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="failures"/> is <see langword="null"/>.</exception>
    public ConcurrencyResult(ConcurrencyStatus status, IReadOnlyList<Exception>? failures = null)
    {
        Status = status;
        _failures = failures ?? NoFailures;
    }

    /// <summary>Gets how the call ended.</summary>
    public ConcurrencyStatus Status { get; }

    /// <summary>Gets a value indicating whether every item ran to completion.</summary>
    public bool IsCompleted => Status == ConcurrencyStatus.Completed;

    /// <summary>
    /// Gets every failure the call recorded, in item-index order for a collected
    /// run and the single lowest-index failure otherwise.
    /// </summary>
    /// <remarks>
    /// The order is the caller's item order (the <c>For</c> index or the staged
    /// task's position), never the workers' completion order.
    /// </remarks>
    public IReadOnlyList<Exception> Failures => _failures;

    /// <summary>
    /// Gets the failure of the lowest-index item, or <see langword="null"/> when
    /// none failed.
    /// </summary>
    /// <remarks>
    /// A <c>For</c> call records the failure of the lowest index, a staged-task
    /// wave the failure of the earliest staged task: the order is the caller's,
    /// never the workers' completion order, so the surfaced error is deterministic
    /// across runs.
    /// </remarks>
    public Exception? FirstFailure => _failures.Count == 0 ? null : _failures[0];

    /// <summary>
    /// Creates a completed result with no failure.
    /// </summary>
    /// <returns>A completed result.</returns>
    public static ConcurrencyResult Completed() => new(ConcurrencyStatus.Completed, NoFailures);

    /// <summary>
    /// Creates an aborted result that recorded a single failure.
    /// </summary>
    /// <param name="failure">The first failure.</param>
    /// <returns>An aborted result carrying <paramref name="failure"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="failure"/> is <see langword="null"/>.</exception>
    public static ConcurrencyResult Aborted(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new ConcurrencyResult(ConcurrencyStatus.Aborted, [failure]);
    }

    /// <summary>
    /// Creates a result that collected every failure.
    /// </summary>
    /// <param name="failures">Every failure, in item-index order.</param>
    /// <returns>A faulted result carrying <paramref name="failures"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="failures"/> is <see langword="null"/>.</exception>
    public static ConcurrencyResult Collected(IReadOnlyList<Exception> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        return new ConcurrencyResult(ConcurrencyStatus.Faulted, failures);
    }

    /// <summary>
    /// Rethrows the first failure with its original stack trace preserved.
    /// </summary>
    /// <remarks>
    /// A caller that treats a body's failure as a programmer error uses this to
    /// surface it after the barrier, when no worker is running. It is a no-op on
    /// a completed result.
    /// </remarks>
    /// <exception cref="Exception">The first recorded failure, rethrown.</exception>
    public void ThrowIfFailed()
    {
        if (FirstFailure is { } failure)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
