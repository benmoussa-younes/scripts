// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Concurrency;

/// <summary>
/// Resolves the concrete worker count and the pool's workload-aware ceiling for
/// a <see cref="ConcurrencyConfiguration"/>, applying the ported core-count rule.
/// </summary>
/// <remarks>
/// <para>The sizing rule, in priority order:</para>
/// <list type="number">
/// <item>an explicit <see cref="ConcurrencyConfiguration.WorkerCount"/> is honoured as given;</item>
/// <item>otherwise <see cref="ConcurrencyConfiguration.Workload"/> (or the older
/// <see cref="ConcurrencyConfiguration.AllowOversubscribe"/>) selects the core count the
/// derived value is based on: logical processors for I/O-bound work, physical
/// cores otherwise;</item>
/// <item>otherwise the default is <c>physicalCores - 1</c>.</item>
/// </list>
/// <para>
/// The subtraction leaves the calling thread a core, so a caller that waits on
/// the barrier never competes with every worker for the same CPU. The result is
/// never below <see cref="ConcurrencyConfiguration.MinimumWorkerCount"/>: on a
/// single-core host the derived value would be <c>0</c> and one worker is used
/// instead, because a context with no workers makes every call a silent no-op.
/// </para>
/// <para>
/// The pool's <em>ceiling</em> is a separate, non-negotiable rule (design §4a):
/// it is the ceiling of the workload's core count — <em>physical cores</em> for
/// CPU-bound work, <em>logical processors</em> for I/O-bound work — and it is a
/// global budget shared by nesting, so the total worker count never exceeds it at
/// any depth. "Oversubscription", the forbidden state, means <em>more than the
/// logical processors</em>; using the logical count in I/O mode is legitimate.
/// See <see cref="ResolvePoolCap"/>.
/// </para>
/// </remarks>
public static class ThreadCount
{
    /// <summary>
    /// Resolves the worker count for <paramref name="config"/>.
    /// </summary>
    /// <param name="config">The configuration to resolve; an explicit bound wins.</param>
    /// <returns>The worker count, between <see cref="ConcurrencyConfiguration.MinimumWorkerCount"/> and <see cref="ConcurrencyConfiguration.MaximumWorkerCount"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The explicit <see cref="ConcurrencyConfiguration.WorkerCount"/> is outside <see cref="ConcurrencyConfiguration.MinimumWorkerCount"/>..<see cref="ConcurrencyConfiguration.MaximumWorkerCount"/>.</exception>
    public static int Resolve(ConcurrencyConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.WorkerCount is { } explicitCount)
        {
            if (explicitCount is < ConcurrencyConfiguration.MinimumWorkerCount or > ConcurrencyConfiguration.MaximumWorkerCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(config),
                    explicitCount,
                    $"The explicit worker count must be between {ConcurrencyConfiguration.MinimumWorkerCount} and {ConcurrencyConfiguration.MaximumWorkerCount}.");
            }

            return explicitCount;
        }

        return Math.Max(ConcurrencyConfiguration.MinimumWorkerCount, SizedCoreCount(config) - 1);
    }

    /// <summary>
    /// Resolves the pool's ceiling: the largest number of worker threads the
    /// context may ever hold, at any nesting depth.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The ceiling, between the resolved worker count and <see cref="ConcurrencyConfiguration.MaximumWorkerCount"/>.</returns>
    /// <remarks>
    /// <para>
    /// <b>The ceiling is the workload's core count</b> (cgroup-aware), the
    /// design's §4a rule: the whole process uses at most that many worker threads,
    /// so a nested call <em>shares</em> the budget instead of multiplying it.
    /// <see cref="WorkloadKind.CpuBound"/> (and <see cref="WorkloadKind.Unknown"/>)
    /// use the <b>physical</b> core count — on 6 physical / 12 logical that is 6,
    /// so a CPU-bound nesting is at most 6×6, never 8×8, never 72. SMT does not
    /// help a CPU-bound body, so a second thread per core only steals execution
    /// units; using the logical count there would be oversubscription.
    /// </para>
    /// <para>
    /// <see cref="WorkloadKind.IoBound"/> (and the older
    /// <see cref="ConcurrencyConfiguration.AllowOversubscribe"/>) raise the ceiling to the
    /// <b>logical</b> processor count, because an I/O-bound body blocks and a
    /// second thread per core overlaps that blocking — it is beneficial, not
    /// oversubscription. Even then the ceiling is a single global budget: it
    /// applies to nesting, so the pool never grows past it and a nested I/O call
    /// shares the chosen budget rather than multiplying it. "Oversubscription" —
    /// the forbidden state — is <em>more than the logical processors</em>, and it
    /// is never reached, at any depth. An explicit
    /// <see cref="ConcurrencyConfiguration.MaximumPoolSize"/> overrides both, still bounded
    /// by <see cref="ConcurrencyConfiguration.MaximumWorkerCount"/>; the ceiling is never
    /// below the resolved worker count.
    /// </para>
    /// </remarks>
    internal static int ResolvePoolCap(ConcurrencyConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var workerCount = Resolve(config);
        var cpus = SizedCoreCount(config);
        var cap = config.MaximumPoolSize ?? cpus;
        cap = Math.Clamp(cap, workerCount, ConcurrencyConfiguration.MaximumWorkerCount);
        return Math.Max(1, cap);
    }

    /// <summary>
    /// Reads the number of logical processors this process may run on
    /// (cgroup-quota aware where the platform reports it).
    /// </summary>
    /// <returns>The logical processor count, at least 1.</returns>
    internal static int LogicalProcessorCount() => Platform.Linux.ProcessorTopology.LogicalProcessorCount();

    /// <summary>
    /// Reads the number of physical cores, falling back to the logical processor
    /// count when the platform does not report a physical topology.
    /// </summary>
    /// <returns>The physical core count, at least 1.</returns>
    internal static int PhysicalCoreCount() => Platform.Linux.ProcessorTopology.PhysicalCoreCount();

    /// <summary>
    /// Selects the core count the sizing and the ceiling are based on: the logical
    /// processors for I/O-bound work, the physical cores otherwise.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The core count, at least 1.</returns>
    private static int SizedCoreCount(ConcurrencyConfiguration config) =>
        config.AllowOversubscribe || config.Workload == WorkloadKind.IoBound
            ? LogicalProcessorCount()
            : PhysicalCoreCount();
}
