// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Concurrency;

/// <summary>
/// Describes what the bodies of a context mostly do, so the worker count can be
/// sized for it.
/// </summary>
/// <remarks>
/// The distinction is the SMT trade-off: a CPU-bound body keeps a core busy, so a
/// second thread on the same core steals execution units from the first and adds
/// little; an I/O-bound body spends most of its time blocked, so a second thread
/// on the same core overlaps that blocking and is beneficial. It selects both the
/// <em>core count</em> the derived worker count is based on and the pool's
/// <em>ceiling</em>: physical cores for CPU-bound work, logical processors for
/// I/O-bound work — the logical count is legitimate for I/O and is not
/// oversubscription, which would be going <em>beyond</em> the logical processors
/// (design §4a).
/// </remarks>
public enum WorkloadKind
{
    /// <summary>
    /// The default: the context does not know, and is sized for CPU-bound work
    /// (one worker per physical core, minus the caller's core). The ceiling is the
    /// physical core count.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The bodies compute: the worker count is the physical core count (minus the
    /// caller's core) and the pool ceiling is the physical core count. SMT threads
    /// are not added, because they would not add throughput.
    /// </summary>
    CpuBound = 1,

    /// <summary>
    /// The bodies block on I/O: the worker count is the logical processor count
    /// (minus the caller's) and the pool ceiling is the logical processor count,
    /// because a second thread per physical core overlaps the blocking and is
    /// beneficial. The logical count is legitimate here — it is not
    /// oversubscription, which would be going beyond it.
    /// </summary>
    IoBound = 2,
}
