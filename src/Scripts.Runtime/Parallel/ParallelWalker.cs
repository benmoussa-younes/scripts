using System.Collections.Concurrent;
using Core.Concurrency;
using Scripts.Runtime.Platform;

namespace Scripts.Runtime.Parallel;

/// <summary>
/// Bounded-parallel directory traversal for <c>parallel.walk_directory</c> (DESIGN §7.10.1). Directory expansion runs
/// on workers of the shared <see cref="Core.Concurrency.ConcurrencyContext"/>, but the output order —
/// lexicographic pre-order, root excluded, exactly <c>walk_directory</c> — is produced by a single sequential
/// fold, so it cannot depend on scheduling. Failures are observed when the fold reaches the failing directory,
/// so the reported error is the same first failure a sequential walk would report.
/// </summary>
/// <remarks>
/// <para>
/// The fold and the speculative workers are the bodies of one
/// <see cref="ConcurrencyContext.For(long, long, long, Action{long}, ConcurrencyOptions)"/> call with a static
/// split: body 0 is the fold (it fills the result list and closes the queue), bodies 1..N-1 each drain the queue
/// and expand the directories they find. A static split is required here — the fold must be the FIRST body to
/// run — and it also keeps the expansion parallel when the pool is capped (the caller takes the trailing
/// bodies). The fold and the worker loops all touch the queue, so the walk still returns a result or a clean
/// <c>error</c>, never an aborted process.
/// </para>
/// <para>
/// Each directory is expanded exactly once: the fold claims a directory with <see cref="ExpandState.TryClaim"/>
/// and expands it itself when nobody holds it yet; a speculative worker that already claimed it does the read,
/// and the fold waits for that single read. Speculation is bounded by the queue capacity, so a huge tree cannot
/// grow the working set without limit, and a directory that does not fit the queue is simply expanded by the
/// fold.
/// </para>
/// </remarks>
internal sealed class ParallelWalker(int workerCount, Action throwIfInterrupted)
{
    private readonly ConcurrentDictionary<string, ExpandState> expansions = new(StringComparer.Ordinal);

    // Bounded: speculative expansion cannot grow the working set without limit on a huge tree.
    private readonly BoundedQueue<string> queue = new(workerCount * 256);
    private readonly int capacity = workerCount * 256;

    // Directories pushed into the queue but not yet popped. A producer only pushes when a reservation is
    // available, so BoundedQueue.Push — which BLOCKS while the queue is full — can never block: nobody can be
    // left waiting for a slot while every consumer is itself waiting to push (the deadlock a naive blocking
    // enqueue would allow with a single worker loop). The counter is the enqueue gate's protected state.
    private int reserved;

    private bool started;
    private string subject = string.Empty;

    // Guards every enqueue, the reservation counter and the one `Close`: a worker either enqueues its children
    // entirely before the fold closes the queue, or sees the closed state and drops its enqueues. So a push can
    // never observe an already-closed queue (the race that used to abort the process, BLOCKER-1).
    private readonly object enqueueGate = new();
    private bool queueCompleted;

    // Diagnostic for the MAJOR-3 regression test: how many directory expansions currently hold their child
    // list. Bounded by the fold's live stack plus the workers' in-flight expansions, not by the tree size.
    private static int retainedExpansions;

    /// <summary>Highest number of expansions that held their child list at once during the last walk.</summary>
    internal static int PeakRetainedExpansions;

    /// <summary>Resets the MAJOR-3 retention diagnostic (test hook).</summary>
    internal static void ResetRetentionDiagnostics()
    {
        retainedExpansions = 0;
        PeakRetainedExpansions = 0;
    }

    /// <summary>Runs the traversal from <paramref name="rootPath"/> and returns every path in pre-order.</summary>
    public List<string> Run(string rootPath)
    {
        // A walker is single-use: its queue is closed and disposed by the first Run.
        if (started)
        {
            throw new InvalidOperationException("a parallel.walk_directory traversal runs only once");
        }

        started = true;
        subject = rootPath;

        var results = new List<string>();

        try
        {
            // Body 0 is the fold; bodies 1.. workerCount-1 drain the queue. A static split guarantees the fold
            // runs first (index 0 is always the first index of the first slice) while the other bodies expand in
            // parallel with it.
            var outcome = ParallelPool.Run(
                workerCount,
                workerCount,
                index =>
                {
                    if (index == 0)
                    {
                        Fold(rootPath, results);
                    }
                    else
                    {
                        WorkerLoop();
                    }
                },
                WorkPartition.StaticChunks);

            // The fold's failure is the lowest index (0), so it is the one surfaced. A worker loop is not
            // expected to fail (every expansion captures its own exception), but a collected one is reported
            // rather than silently dropped.
            if (outcome.FirstFailure is { } failure)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }

            return results;
        }
        finally
        {
            // The queue is closed then disposed once ALL bodies have finished: a blocked consumer wakes
            // up and exits, and no `Push` races against the disposal.
            queue.Close();
            queue.Dispose();
        }
    }

    /// <summary>
    /// Adds a path for speculative expansion (best effort: a full queue simply drops it and the demand-driven
    /// fold expands that directory later). Never called after the queue is closed: the gate and the flag close
    /// the race that used to throw <c>InvalidOperationException</c> on a worker thread (BLOCKER-1).
    /// </summary>
    private void Enqueue(string directoryPath)
    {
        lock (enqueueGate)
        {
            if (queueCompleted || reserved >= capacity)
            {
                return;
            }

            // The reservation guarantees the queue has room, so this push never blocks (see `reserved`).
            reserved++;
            queue.Push(directoryPath);
        }
    }

    /// <summary>Marks the queue complete; no enqueue happens after this returns.</summary>
    private void CloseQueue()
    {
        lock (enqueueGate)
        {
            if (queueCompleted)
            {
                return;
            }

            queueCompleted = true;
            queue.Close();
        }
    }

    /// <summary>The sequential fold: pre-order with an explicit stack; it queries each directory it reaches.</summary>
    private void Fold(string rootPath, List<string> results)
    {
        var stack = new Stack<Frame>();
        try
        {
            var root = ExpansionOf(rootPath);
            EnqueueChildren(root);
            stack.Push(new Frame(root));
            while (stack.Count > 0)
            {
                // Parity with the sequential WalkDirectory: a pending interrupt aborts the fold promptly (its
                // InterruptionTermination propagates out of the body and is rethrown by Run, never swallowed).
                throwIfInterrupted();

                var frame = stack.Peek();
                if (!frame.Children.MoveNext())
                {
                    // The directory is fully enumerated: nothing else reads its expansion, so it is freed now
                    // instead of being kept until the whole walk ends (MAJOR-3).
                    stack.Pop();
                    frame.Dispose();
                    continue;
                }

                var child = frame.Children.Current;
                results.Add(child.Path);
                if (results.Count > SystemLimits.MaximumUnits)
                {
                    throw SystemLimits.ListTooLong(results.Count);
                }

                if (child.IsDirectory)
                {
                    var expansion = ExpansionOf(child.Path);
                    EnqueueChildren(expansion);
                    stack.Push(new Frame(expansion));
                }
            }
        }
        finally
        {
            // The queue is closed even when the fold throws (a bad directory, a list that is too long): the
            // worker loops blocked on an empty queue wake up and the walk returns its error instead of hanging.
            CloseQueue();
            foreach (var frame in stack)
            {
                frame.Dispose();
            }
        }
    }

    /// <summary>Queues a freshly expanded directory's subdirectories for speculative expansion.</summary>
    private void EnqueueChildren(Expansion expansion)
    {
        foreach (var child in expansion.Children)
        {
            if (child.IsDirectory)
            {
                Enqueue(child.Path);
            }
        }
    }

    /// <summary>
    /// The expansion of one directory. The first caller to claim it expands it — on the fold's thread or on a
    /// worker — and every other caller waits for that one read, so no directory is ever read twice for nothing.
    /// The expansion is reference-counted: the fold is always a reader, and the claimer is a second one. Once
    /// both have read it, its child list is dropped (MAJOR-3), so a huge tree no longer keeps every expansion
    /// alive for the whole walk.
    /// </summary>
    private Expansion ExpansionOf(string directoryPath)
    {
        var state = expansions.GetOrAdd(directoryPath, static _ => new ExpandState());
        if (state.TryClaim(1))
        {
            // The fold is the only reader: it expands, reads and releases once.
            var expansion = Expand(directoryPath, state);
            state.Complete(expansion);
            return ReThrow(expansion);
        }

        return ReThrow(state.Wait());
    }

    private Expansion Expand(string directoryPath, ExpandState state)
    {
        try
        {
            var expansion = new Expansion(FileSystemQueries.ExpandDirectory(directoryPath, subject), null, state);
            CountRetained();
            return expansion;
        }
        catch (Exception exception)
        {
            var expansion = new Expansion([], exception, state);
            CountRetained();
            return expansion;
        }
    }

    private static void CountRetained()
    {
        var retained = Interlocked.Increment(ref retainedExpansions);
        PeakRetainedExpansions = Math.Max(PeakRetainedExpansions, retained);
    }

    private static Expansion ReThrow(Expansion expansion)
    {
        if (expansion.Failure is { } failure)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return expansion;
    }

    private void WorkerLoop()
    {
        while (queue.Pop(out var directoryPath))
        {
            ReleaseReservation();

            var state = expansions.GetOrAdd(directoryPath, static _ => new ExpandState());
            if (!state.TryClaim(2))
            {
                continue; // Already claimed (the fold or another worker); its read will be shared.
            }

            var expansion = Expand(directoryPath, state);
            state.Complete(expansion);
            EnqueueChildren(expansion);
            // This worker is done reading; the fold still holds the other reference.
            expansion.Release();
        }
    }

    /// <summary>Drops the reservation of an item that has just been popped, freeing a speculative slot.</summary>
    private void ReleaseReservation()
    {
        lock (enqueueGate)
        {
            reserved--;
        }
    }

    /// <summary>
    /// One directory's children. Reference-counted (<see cref="ExpandState"/>): the fold and, for a
    /// worker-claimed directory, the worker each release once; the child list is dropped when both are done.
    /// </summary>
    private sealed class Expansion(List<DirectoryChild> children, Exception? failure, ExpandState state)
    {
        private List<DirectoryChild>? children = children;

        public Exception? Failure { get; } = failure;

        /// <summary>The children; empty once released (a released directory is never read again).</summary>
        public List<DirectoryChild> Children => Volatile.Read(ref children) ?? EmptyChildren;

        public void Release()
        {
            if (state.Release() && Interlocked.Exchange(ref children, null) is not null)
            {
                // This directory's child list is freed here, not at the end of the walk (MAJOR-3). The peak
                // retention is observable for the regression test.
                var retained = Interlocked.Decrement(ref retainedExpansions);
                PeakRetainedExpansions = Math.Max(PeakRetainedExpansions, retained);
            }
        }
    }

    /// <summary>An enumerator over one directory's children plus the expansion reference it holds.</summary>
    private readonly struct Frame : IDisposable
    {
        private readonly Expansion expansion;

        public Frame(Expansion expansion)
        {
            this.expansion = expansion;
            Children = expansion.Children.GetEnumerator();
        }

        public IEnumerator<DirectoryChild> Children { get; }

        public void Dispose()
        {
            Children.Dispose();
            expansion.Release();
        }
    }

    private static readonly List<DirectoryChild> EmptyChildren = [];

    /// <summary>One directory's expansion: claimed once, read by the fold (and its claimer), then released.</summary>
    private sealed class ExpandState
    {
        private readonly ManualResetEventSlim done = new(false);
        private int claimed;
        private int readers;
        private Expansion? expansion;

        /// <summary>
        /// Claims this expansion for the current thread and sets how many readers it will have: the fold is
        /// always one; a worker-claimed directory is read by the worker (for its enqueues) and by the fold.
        /// </summary>
        public bool TryClaim(int readerCount)
        {
            if (Interlocked.CompareExchange(ref claimed, 1, 0) != 0)
            {
                return false;
            }

            Volatile.Write(ref readers, readerCount);
            return true;
        }

        public void Complete(Expansion result)
        {
            Volatile.Write(ref expansion, result);
            done.Set();
        }

        public Expansion Wait()
        {
            done.Wait();
            return Volatile.Read(ref expansion)!;
        }

        /// <summary>Drops one reader; true when no reader is left (the caller may then free the list).</summary>
        public bool Release() => Interlocked.Decrement(ref readers) == 0;
    }
}
