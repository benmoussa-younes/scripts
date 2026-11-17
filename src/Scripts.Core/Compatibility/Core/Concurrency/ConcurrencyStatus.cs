// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Concurrency;

/// <summary>
/// Describes how a <see cref="ConcurrencyContext.For(long, long, long, Action{long})"/>
/// or <see cref="ConcurrencyContext.Run(Action)"/> call ended.
/// </summary>
public enum ConcurrencyStatus
{
    /// <summary>Every item ran to completion.</summary>
    Completed = 0,

    /// <summary>
    /// At least one worker's body failed. The first failure is recorded on the
    /// <see cref="ConcurrencyResult"/>; in a call that stops at the first failure
    /// the remaining items were not claimed.
    /// </summary>
    Aborted = 1,

    /// <summary>
    /// A worker's body failed and the call was configured to collect every
    /// failure instead of stopping at the first one.
    /// </summary>
    Faulted = 2,
}
