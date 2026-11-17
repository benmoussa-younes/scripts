// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Concurrency;

/// <summary>
/// The one place a body invocation is wrapped: it runs the body, records a
/// failure and runs the worker-side abort action.
/// </summary>
/// <remarks>
/// The three index-running waves (slice, claim and the task range's body-free
/// arm) share this so the failure and abort semantics are identical wherever a
/// body runs; a divergence here would be a silent behaviour difference between
/// partitions.
/// </remarks>
internal static class Body
{
    /// <summary>
    /// Runs one body invocation and records a failure.
    /// </summary>
    /// <param name="body">The caller's body.</param>
    /// <param name="index">The index handed to the body.</param>
    /// <param name="failures">The call's shared failure record.</param>
    /// <param name="abortAction">The abort action, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> to keep running this worker; <see langword="false"/> to stop it.</returns>
    internal static bool RunOne(
        Action<long> body,
        long index,
        FailureRecord failures,
        Action<Exception>? abortAction)
    {
        try
        {
            body(index);
            return true;
        }
        catch (Exception exception)
        {
            failures.Record(index, exception);

            if (abortAction is { } abort)
            {
                InvokeAbort(abort, exception);
            }

            return failures.CollectAll;
        }
    }

    /// <summary>
    /// Runs the worker-side abort action, swallowing any exception it throws.
    /// </summary>
    /// <remarks>
    /// The abort action exists to unblock the other workers when the body is
    /// blocked on an external resource. A throw from it must not replace or mask
    /// the body's own failure, so it is deliberately swallowed: the abort already
    /// has its reason.
    /// </remarks>
    internal static void InvokeAbort(Action<Exception> abort, Exception exception)
    {
        try
        {
            abort(exception);
        }
        catch (Exception)
        {
            // Deliberately ignored: the body's failure is the reason for the abort.
        }
    }
}
