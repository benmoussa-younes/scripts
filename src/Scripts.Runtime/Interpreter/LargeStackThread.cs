using System.Runtime.ExceptionServices;

namespace Scripts.Runtime.Interpreter;

/// <summary>Runs work on a dedicated thread with a large (virtual) stack so the call depth limit of 10000 is reachable (DESIGN 11).</summary>
public static class LargeStackThread
{
    /// <summary>Virtual stack reservation of the interpreter thread (the pages are committed lazily).</summary>
    public const int StackSizeInBytes = 512 * 1024 * 1024;

    /// <summary>
    /// Virtual stack reservation of one <c>parallel</c> worker thread. Smaller than the interpreter thread
    /// because a pool creates up to 256 of them at once (256 × 512 MiB would reserve 128 GiB of address space,
    /// MAJOR-2). The measured floor for the documented call depth (DESIGN §5.8, ≤10000 calls) is about 6.1 MiB:
    /// a depth-9999 recursion inside a worker overflows natively below it and, at and above it, returns the clean
    /// <c>error(transform_failed)</c> whose message is <c>SCRIPTS-RUNTIME-0011</c> (depth 9998 succeeds, depth 9999
    /// already reports the guard — commit 8dc5245). 64 MiB keeps ~10× headroom. Committed lazily, so the
    /// resident cost is only what a worker actually uses.
    /// </summary>
    public const int WorkerStackSizeInBytes = 64 * 1024 * 1024;

    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        var workExecuted = false;
        ExceptionDispatchInfo? failure = null;
        Thread thread;
        try
        {
            thread = new Thread(
                () =>
                {
                    try
                    {
                        result = work();
                        workExecuted = true;
                    }
                    catch (Exception exception)
                    {
                        failure = ExceptionDispatchInfo.Capture(exception);
                    }
                },
                StackSizeInBytes);
            thread.Start();
        }
        catch (Exception exception) when (exception is OutOfMemoryException or SystemException)
        {
            // Starting the large-stack thread failed (e.g. a low virtual-memory limit): this is an environment
            // limit, surfaced as a runtime failure (DESIGN 11), not an internal scripts bug.
            throw new RuntimeFailure("SCRIPTS-RUNTIME-0015");
        }

        thread.Join();
        if (failure is not null)
        {
            // A failure before the interpreter ran (the large-stack thread could not commit its stack) is an
            // environment limit; an in-execution failure is rethrown as-is (the interpreter already maps it).
            if (!workExecuted && failure.SourceException is OutOfMemoryException)
            {
                throw new RuntimeFailure("SCRIPTS-RUNTIME-0015");
            }

            failure.Throw();
        }

        return result;
    }
}
