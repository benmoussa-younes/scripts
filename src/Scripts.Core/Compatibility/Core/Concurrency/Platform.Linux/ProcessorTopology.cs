// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;

namespace Core.Concurrency.Platform.Linux;

/// <summary>
/// Reads the CPU topology: the processors this process may run on
/// (<c>sched_getaffinity</c>) and the physical core count (the <c>sysfs</c>
/// topology tree).
/// </summary>
/// <remarks>
/// <para>
/// There is no portable managed API for physical cores and no portable affinity
/// API: <see cref="Environment.ProcessorCount"/> reports the processors the
/// runtime can see, which is neither the socket topology nor the affinity set.
/// This layer reads the kernel's own view.
/// </para>
/// <para>
/// The physical core count has no non-privileged syscall; the kernel exposes it
/// only through <c>sysfs</c>. This layer reads it best-effort and falls back to
/// the affinity count when the tree is unavailable (a container, a stripped root,
/// a non-x86 topology). The fallback is the correct behaviour for the sizing rule:
/// an unknown topology must never make the derived worker count smaller than the
/// host can run.
/// </para>
/// <para>
/// The counting is a pure function of the <c>cpu</c> directory and the affinity
/// set, so the suite drives every branch — a full tree, a missing tree, a
/// restricted affinity set, an unreadable file — against a temporary fixture
/// instead of depending on the measuring host's topology.
/// </para>
/// </remarks>
internal static partial class ProcessorTopology
{
    /// <summary>The sysfs path that lists one entry per logical processor.</summary>
    private const string CpuRoot = "/sys/devices/system/cpu";

    /// <summary>The default cgroup mount root where the CPU quota is reported.</summary>
    private const string CgroupRoot = "/sys/fs/cgroup";

    /// <summary>The largest CPU mask this layer reads (4096 CPUs).</summary>
    internal const int MaximumCpuCount = 4096;

    private static readonly Lazy<int> PhysicalCores = new(
        () => CountPhysicalCores(CpuRoot, CountAllowedCpus(), AllowedCpuMask()),
        isThreadSafe: true);

    private static readonly Lazy<int> AllowedCpus = new(CountAllowedCpus, isThreadSafe: true);

    private static readonly Lazy<int> LogicalCpus = new(
        () => Math.Max(1, Math.Min(CountAllowedCpus(), QuotaProcessorCount() ?? int.MaxValue)),
        isThreadSafe: true);

    /// <summary>
    /// Gets the number of logical processors this process may run on, honouring a
    /// restricted affinity set and, where reported, a cgroup CPU quota.
    /// </summary>
    /// <returns>The logical processor count, at least 1.</returns>
    internal static int LogicalProcessorCount() => LogicalCpus.Value;

    /// <summary>
    /// Gets the number of physical cores, best-effort.
    /// </summary>
    /// <returns>The physical core count, at least 1.</returns>
    internal static int PhysicalCoreCount() => PhysicalCores.Value;

    /// <summary>
    /// Gets the number of processors this process may run on.
    /// </summary>
    /// <returns>The allowed processor count, at least 1.</returns>
    internal static int AllowedProcessorCount() => AllowedCpus.Value;

    /// <summary>
    /// Counts the physical cores below a <c>cpu</c> directory, treating the tree as
    /// contiguous from <c>cpu0</c>, and falls back to the allowed count when the
    /// tree is unusable.
    /// </summary>
    /// <param name="cpuRoot">The directory holding the per-CPU entries.</param>
    /// <param name="allowed">The number of processors the process may run on.</param>
    /// <returns>The physical core count, between 1 and <paramref name="allowed"/> when the topology is known, otherwise <paramref name="allowed"/>.</returns>
    /// <remarks>
    /// The suite drives this against a temporary fixture whose <c>cpu</c> entries
    /// are contiguous, so the count is over <c>cpu0..cpuN</c>. The live path uses
    /// the affinity-aware overload, which walks the allowed-processor mask instead
    /// of a contiguous range.
    /// </remarks>
    internal static int CountPhysicalCores(string cpuRoot, int allowed) =>
        CountPhysicalCores(cpuRoot, allowed, affinity: null);

    /// <summary>
    /// Counts the physical cores below a <c>cpu</c> directory for the processors
    /// whose bit is set in <paramref name="affinity"/>.
    /// </summary>
    /// <param name="cpuRoot">The directory holding the per-CPU entries.</param>
    /// <param name="allowed">The number of processors the process may run on (the fallback).</param>
    /// <param name="affinity">The allowed-processor mask; a <see langword="null"/> or all-zero mask means "every processor below <paramref name="cpuRoot"/>, contiguous".</param>
    /// <returns>The physical core count, between 1 and <paramref name="allowed"/> when the topology is known, otherwise <paramref name="allowed"/>.</returns>
    /// <remarks>
    /// A sparse affinity set is why the mask matters: a process restricted to
    /// <c>0-3,8-11</c> runs on processors whose indices are far above the number of
    /// allowed processors, and iterating <c>cpu0..cpuN</c> would read the wrong
    /// entries or stop at the hole. Walking the mask reads exactly the allowed
    /// processors.
    /// </remarks>
    internal static int CountPhysicalCores(string cpuRoot, int allowed, byte[]? affinity)
    {
        if (!File.Exists($"{cpuRoot}/cpu0/topology/core_id"))
        {
            return allowed;
        }

        var distinct = new HashSet<string>(StringComparer.Ordinal);

        // A null or all-zero mask means "no affinity information": treat the tree
        // as contiguous over cpu0..cpu(allowed-1).
        var mask = affinity is not null && CountBits(affinity) > 0 ? affinity : null;

        for (var cpu = 0; cpu < MaximumCpuCount; cpu++)
        {
            if (mask is null)
            {
                if (cpu >= allowed)
                {
                    break;
                }
            }
            else if ((mask[cpu >> 3] & (1 << (cpu & 7))) == 0)
            {
                continue;
            }

            var path = $"{cpuRoot}/cpu{cpu}";

            if (!Directory.Exists(path))
            {
                // A hole: with a mask the next allowed cpu is still read; without
                // one the contiguous tree has ended.
                if (mask is null)
                {
                    break;
                }

                continue;
            }

            distinct.Add(
                ReadTopology(path, "physical_package_id") + ":" + ReadTopology(path, "core_id"));
        }

        // The pair (package, core) is unique per physical core. An empty set means
        // the tree was readable but reported nothing usable, so the fallback is
        // the affinity count rather than a claim of zero cores.
        return distinct.Count > 0 ? distinct.Count : allowed;
    }

    /// <summary>
    /// Reads the allowed-processor mask of this process.
    /// </summary>
    /// <returns>The mask, or <see langword="null"/> when the call fails.</returns>
    private static byte[]? AllowedCpuMask()
    {
        var mask = new byte[MaximumCpuCount / 8];

        return sched_getaffinity(0, (nuint)mask.Length, mask) == 0 ? mask : null;
    }

    /// <summary>
    /// Reads the affinity mask of the calling thread, for the suite to assert a
    /// best-effort pin.
    /// </summary>
    /// <returns>The thread's mask, or <see langword="null"/> when the call fails.</returns>
    internal static byte[]? CurrentThreadMask()
    {
        var mask = new byte[MaximumCpuCount / 8];

        return sched_getaffinity(0, (nuint)mask.Length, mask) == 0 ? mask : null;
    }

    private static string ReadTopology(string cpuPath, string file)
    {
        try
        {
            return File.ReadAllText($"{cpuPath}/topology/{file}").Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "?";
        }
    }

    private static int CountAllowedCpus()
    {
        var mask = new byte[MaximumCpuCount / 8];

        var result = sched_getaffinity(0, (nuint)mask.Length, mask);

        return result != 0
            ? FallbackProcessorCount()
            : CountBits(mask);
    }

    /// <summary>
    /// Counts the set bits of an affinity mask, at least 1.
    /// </summary>
    /// <param name="mask">The affinity mask.</param>
    /// <returns>The number of allowed processors, at least 1.</returns>
    internal static int CountBits(byte[] mask)
    {
        var total = 0;
        foreach (var value in mask)
        {
            total += BitOperations.PopCount(value);
        }

        return Math.Max(total, 1);
    }

    /// <summary>
    /// The processor count used when the affinity call itself fails.
    /// </summary>
    /// <returns>The runtime's processor count, at least 1.</returns>
    internal static int FallbackProcessorCount() => Math.Max(Environment.ProcessorCount, 1);

    /// <summary>
    /// Reads the cgroup CPU quota as a whole number of processors, or
    /// <see langword="null"/> when there is no quota.
    /// </summary>
    /// <returns>The quota in processors (rounded up), or <see langword="null"/>.</returns>
    /// <remarks>
    /// <para>
    /// A container's <c>cpu.max</c> (v2) or <c>cpu.cfs_quota_us</c> /
    /// <c>cpu.cfs_period_us</c> (v1) limits how much CPU the cgroup may use, which
    /// can be far below the affinity count. Sizing on the affinity count alone
    /// would oversubscribe the cgroup's slice and make every worker slower, so the
    /// quota is honoured when the kernel reports one.
    /// </para>
    /// <para>
    /// The reader is best-effort: a missing or unreadable file (a bare host, a
    /// stripped root) means "no quota" and the affinity count stands.
    /// </para>
    /// </remarks>
    internal static int? QuotaProcessorCount() => QuotaProcessorCount(CgroupRoot);

    /// <summary>
    /// Reads the cgroup CPU quota below <paramref name="cgroupRoot"/>, or
    /// <see langword="null"/> when there is no quota.
    /// </summary>
    /// <param name="cgroupRoot">The cgroup mount root (normally <c>/sys/fs/cgroup</c>).</param>
    /// <returns>The quota in processors (rounded up), or <see langword="null"/>.</returns>
    /// <remarks>
    /// The root is a parameter so the suite drives every branch — a v2 quota, a v2
    /// <c>"max"</c> line, a malformed v2 line, a v1 quota, a v1 zero quota, a
    /// missing tree and an unreadable file — against a temporary fixture, exactly
    /// as the physical-core count is driven against a fixture.
    /// </remarks>
    internal static int? QuotaProcessorCount(string cgroupRoot)
    {
        // cgroup v2: a single "quota period" line, "max" meaning no limit.
        var v2 = ReadText(Path.Combine(cgroupRoot, "cpu.max"));
        if (v2 is not null)
        {
            var parts = v2.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2
                && !string.Equals(parts[0], "max", StringComparison.Ordinal)
                && long.TryParse(parts[0], out var v2Quota)
                && long.TryParse(parts[1], out var v2Period)
                && v2Quota > 0
                && v2Period > 0)
            {
                return CeilDivToInt(v2Quota, v2Period);
            }

            return null;
        }

        // cgroup v1: separate quota and period files.
        var quotaText = ReadText(Path.Combine(cgroupRoot, "cpu", "cpu.cfs_quota_us"));
        var periodText = ReadText(Path.Combine(cgroupRoot, "cpu", "cpu.cfs_period_us"));
        if (quotaText is not null
            && periodText is not null
            && long.TryParse(quotaText, out var quota)
            && long.TryParse(periodText, out var period)
            && quota > 0
            && period > 0)
        {
            return CeilDivToInt(quota, period);
        }

        return null;
    }

    /// <summary>
    /// Rounds <paramref name="numerator"/> / <paramref name="denominator"/> up to
    /// a whole processor count, clamped to <see cref="int"/>.
    /// </summary>
    /// <param name="numerator">The quota.</param>
    /// <param name="denominator">The period.</param>
    /// <returns>The processor count, at least 1.</returns>
    private static int CeilDivToInt(long numerator, long denominator)
    {
        var value = (numerator + denominator - 1) / denominator;
        return value > int.MaxValue ? int.MaxValue : (int)Math.Max(value, 1);
    }

    /// <summary>
    /// Reads a small text file, returning <see langword="null"/> when it is absent
    /// or unreadable.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The trimmed contents, or <see langword="null"/>.</returns>
    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pins the calling thread, best-effort, to the <paramref name="ordinal"/>-th
    /// processor this process is allowed to run on.
    /// </summary>
    /// <param name="ordinal">The worker ordinal; the allowed processor is chosen by <c>ordinal % allowed</c>.</param>
    /// <returns><see langword="true"/> when the affinity was set; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// Best-effort by design: the request is advisory, so a kernel that refuses it
    /// (a restricted cpuset, a container without the capability) is not an error
    /// and the worker simply runs unpinned. The ordinal is reduced modulo the
    /// allowed processor count so more workers than processors does not ask for a
    /// processor that is not in the set.
    /// </remarks>
    internal static bool PinCurrentThread(int ordinal)
    {
        // A failed affinity read yields a null mask, which the pure overload treats
        // as all-zero: nothing to pin to, so it reports false.
        var mask = CurrentThreadMask() ?? new byte[MaximumCpuCount / 8];
        return PinCurrentThread(ordinal, mask, Math.Max(CountBits(mask), 1));
    }

    /// <summary>
    /// Pins the calling thread to the <paramref name="ordinal"/>-th processor set
    /// in <paramref name="mask"/>.
    /// </summary>
    /// <param name="ordinal">The worker ordinal.</param>
    /// <param name="mask">The allowed-processor mask.</param>
    /// <param name="allowed">The count of set bits in <paramref name="mask"/>.</param>
    /// <returns><see langword="true"/> when the affinity was set; otherwise <see langword="false"/>.</returns>
    internal static bool PinCurrentThread(int ordinal, byte[] mask, int allowed)
    {
        var target = ((ordinal % allowed) + allowed) % allowed;
        var pinned = new byte[mask.Length];
        var seen = 0;

        for (var cpu = 0; cpu < MaximumCpuCount; cpu++)
        {
            if ((mask[cpu >> 3] & (1 << (cpu & 7))) == 0)
            {
                continue;
            }

            if (seen++ == target)
            {
                pinned[cpu >> 3] = (byte)(1 << (cpu & 7));

                // The kernel may still refuse (a cpuset race); that is not an error.
                return sched_setaffinity(0, (nuint)pinned.Length, pinned) == 0;
            }
        }

        // Defensive: `mask` is the kernel's own allowed set and `target` is below
        // its popcount, so the loop above always finds a bit. An all-zero mask (a
        // kernel quirk) leaves nothing to pin.
        return false;
    }

    [LibraryImport("libc", EntryPoint = "sched_getaffinity", SetLastError = true)]
    private static partial int sched_getaffinity(int pid, nuint cpuSetSize, byte[] mask);

    [LibraryImport("libc", EntryPoint = "sched_setaffinity", SetLastError = true)]
    private static partial int sched_setaffinity(int pid, nuint cpuSetSize, byte[] mask);
}
