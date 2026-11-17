// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Concurrency;

/// <summary>
/// Configures a <see cref="ConcurrencyContext"/> for its whole lifetime: the
/// worker bound, the workload kind, the per-worker stack size and the optional
/// advisories (oversubscription and CPU pinning).
/// </summary>
/// <remarks>
/// <para>
/// This sizes the <em>context</em>, not a call. The per-call counterpart is
/// <see cref="ConcurrencyOptions"/>, which sizes one <c>For</c>/<c>Run</c>. A
/// context is created from this record once (<see cref="ConcurrencyContext.Create"/>)
/// and keeps the values for its whole life; a caller that wants a different bound
/// for a single call sets <see cref="ConcurrencyOptions.WorkerCount"/> instead of
/// creating a new context.
/// </para>
/// <para>
/// The record is immutable; every option is set through an <c>init</c> accessor
/// and a context created from it copies the values once at
/// <see cref="ConcurrencyContext.Create"/> time. A later change to the instance a
/// context was created from never affects that context.
/// </para>
/// <para>
/// The rules that turn these options into a concrete worker count are in
/// <see cref="ThreadCount.Resolve"/>; the one that turns them into the pool's
/// workload-aware ceiling is in <see cref="MaximumPoolSize"/>.
/// </para>
/// </remarks>
public sealed record ConcurrencyConfiguration
{
    /// <summary>
    /// Gets the explicit worker count; when set it wins over every derived rule.
    /// </summary>
    /// <remarks>
    /// The value is honoured as given, between <see cref="MinimumWorkerCount"/>
    /// and <see cref="MaximumWorkerCount"/>; a value outside that range is a
    /// programmer error and <see cref="ConcurrencyContext.Create"/> throws. The
    /// explicit bound is never silently clamped.
    /// </remarks>
    public int? WorkerCount { get; init; }

    /// <summary>
    /// Gets a value indicating whether the derived count may use the logical
    /// processors, that is whether <c>logicalCpus - 1</c> is used instead of
    /// <c>physicalCores - 1</c>.
    /// </summary>
    /// <remarks>
    /// This is the older, coarser form of <see cref="Workload"/>: it forces the
    /// logical count whatever the workload, and it is meant for I/O-bound bodies.
    /// It sizes the context and it raises the pool's ceiling to the logical
    /// processor count (see <see cref="MaximumPoolSize"/>), exactly like
    /// <see cref="WorkloadKind.IoBound"/>. It never oversubscribes: the logical
    /// count is the outer bound, and nesting shares it rather than multiplying it.
    /// </remarks>
    public bool AllowOversubscribe { get; init; }

    /// <summary>
    /// Gets what the context's bodies mostly do, which selects the CPU count the
    /// derived worker count is based on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the workload-aware sizing rule: <see cref="WorkloadKind.CpuBound"/>
    /// (and <see cref="WorkloadKind.Unknown"/>) size on <em>physical cores</em>,
    /// because a CPU-bound second thread per core only steals execution units;
    /// <see cref="WorkloadKind.IoBound"/> sizes on <em>logical processors</em>,
    /// because a second thread per core overlaps blocking and is beneficial.
    /// </para>
    /// <para>
    /// <see cref="AllowOversubscribe"/> is the older, coarser form: it forces the
    /// logical count whatever the workload. An explicit
    /// <see cref="WorkerCount"/> wins over both. Both select the ceiling as well as
    /// the count, and neither ever exceeds the logical processors (see
    /// <see cref="MaximumPoolSize"/>).
    /// </para>
    /// </remarks>
    public WorkloadKind Workload { get; init; } = WorkloadKind.Unknown;

    /// <summary>
    /// Gets the size, in bytes, of each worker's stack.
    /// </summary>
    /// <remarks>
    /// A worker runs with this stack size for its whole life. The value must be
    /// positive: there is no implicit "OS default", because the default stack is
    /// too small for the deep recursion this library exists to run, and a stack
    /// that overflows terminates the process. <see cref="ConcurrencyContext.Create"/>
    /// rejects zero and negative. The default
    /// (<see cref="DefaultWorkerStackBytes"/>, 32 MiB) is the design's measured
    /// choice: the real scripts interpreter spends ≈ 2.3 KiB of native stack per call,
    /// so depth 10000 needs a minimum of 24 MiB and 32 MiB leaves a 1.3× margin
    /// (spike §3). scripts may pass 64 MiB for a 2.7× margin. A stack that is too
    /// small for a caller's recursion overflows the worker and terminates the
    /// process: the library documents the requirement, it cannot size a stack for
    /// a recursion it cannot see.
    /// </remarks>
    public int WorkerStackBytes { get; init; } = DefaultWorkerStackBytes;

    /// <summary>
    /// Gets a value indicating whether workers are pinned, best-effort, to CPUs.
    /// </summary>
    /// <remarks>
    /// Pinning is advisory: <see cref="System.Threading.Thread"/> has no portable
    /// affinity API, so on Linux each worker calls <c>sched_setaffinity</c> when its
    /// thread starts, targeting the <c>index</c>-th processor this process is
    /// allowed to run on. A kernel that refuses it — a restricted cpuset, a
    /// container without the capability — is not an error: the worker simply runs
    /// unpinned, and the returned request is never checked. This is not a
    /// performance guarantee; it is a hint.
    /// </remarks>
    public bool PinToCpu { get; init; }

    /// <summary>
    /// The default worker stack size: 32 MiB, the design's measured choice
    /// (≈ 2.3 KiB per interpreter call; depth 10000 needs 24 MiB minimum).
    /// </summary>
    public const int DefaultWorkerStackBytes = 32 * 1024 * 1024;

    /// <summary>The smallest worker count a context may be created with.</summary>
    public const int MinimumWorkerCount = 1;

    /// <summary>The largest worker count a context may be created with (the design's hard bound).</summary>
    public const int MaximumWorkerCount = 256;

    /// <summary>
    /// Gets the largest number of worker threads the pool may hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the <b>workload-aware ceiling</b>, not a tuning detail. It is a
    /// single global budget: a nested parallel call never multiplies the worker
    /// count past this value — the pool hands out the workers it has and a call
    /// that cannot rent the rest runs the deficit <em>on its own calling thread</em>
    /// (still a worker, so <see cref="ConcurrencyContext.WorkerIndex"/> stays valid
    /// there). So a CPU-bound nesting is at most 6×6 and an I/O-bound nesting at
    /// most 12×12 on a 6-physical / 12-logical host: the budget is <em>shared</em>,
    /// never multiplied, at any depth.
    /// </para>
    /// <para>
    /// The default is the workload's core count (cgroup-aware).
    /// <see cref="WorkloadKind.CpuBound"/> and <see cref="WorkloadKind.Unknown"/>
    /// use the <b>physical</b> core count, so on a 6-physical / 12-logical host the
    /// whole process holds at most <b>6 worker threads</b> — never the logical
    /// count, which would oversubscribe a CPU-bound body. The spike measured
    /// nesting without this cap growing a pool to 72 threads and costing ×180
    /// across a cap sweep; capping at the physical count removed the
    /// oversubscription entirely. The rule is the design's §4a.
    /// </para>
    /// <para>
    /// <see cref="WorkloadKind.IoBound"/> (and <see cref="AllowOversubscribe"/>)
    /// raise the ceiling to the <b>logical</b> processor count, because I/O-bound
    /// bodies block and the extra threads overlap that blocking — that is
    /// legitimate and expected, not oversubscription. "Oversubscription", the
    /// forbidden state, is <em>more than the logical processors</em>; the ceiling
    /// still applies to nesting, so it is never reached at any depth. An explicit
    /// value here overrides both and wins outright, still bounded by
    /// <see cref="MaximumWorkerCount"/>; the pool is at least
    /// <see cref="WorkerCount"/> (or 1).
    /// </para>
    /// </remarks>
    public int? MaximumPoolSize { get; init; }

    /// <summary>
    /// Gets the default configuration: no explicit bound, no oversubscription,
    /// a 32 MiB stack and no pinning.
    /// </summary>
    public static ConcurrencyConfiguration Default { get; } = new();
}
