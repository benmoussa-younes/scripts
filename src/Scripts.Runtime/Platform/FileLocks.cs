using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Scripts.Runtime.Platform;

/// <summary>
/// Advisory exclusive locks (DESIGN §7.2): <c>open(2)</c> the lock file with `O_NOFOLLOW` and
/// <c>flock(2)</c> it. The lock is held by the open file description, so it disappears when the
/// descriptor is closed or the process exits; the file itself is left in place.
/// </summary>
internal static class FileLocks
{
    /// <summary>`0600`: the lock file is readable and writable only by its owner.</summary>
    private const uint LockFileMode = 0x180;

    private const int RetryMilliseconds = 10;

    /// <summary>
    /// Acquires the exclusive lock on <paramref name="lockPath"/>, creating the file when it does
    /// not exist. A negative timeout is <c>invalid_argument</c> before any system call; a positive
    /// timeout retries every 10 ms and gives <c>timed_out</c> when it expires; zero waits forever
    /// (the retry loop is interruptible through <paramref name="throwIfInterrupted"/>).
    /// </summary>
    public static FileLockValue Acquire(string lockPath, long timeoutMilliseconds, Action throwIfInterrupted)
    {
        if (timeoutMilliseconds < 0)
        {
            throw new IoFailureException(IoErrorKinds.InvalidArgument, lockPath, "the timeout must not be negative");
        }

        var nativePath = NativeFileSystem.ToNativePath(lockPath);
        var descriptor = Libc.Open(
            nativePath,
            Libc.OpenReadWrite | Libc.OpenCreate | Libc.OpenCloseOnExec | Libc.OpenNoFollow,
            LockFileMode);
        if (descriptor < 0)
        {
            throw IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), lockPath);
        }

        // The creation mode is masked by the process umask; fchmod(2) guarantees the private 0600 mode.
        if (Libc.ChangeFileMode(descriptor, LockFileMode) != 0)
        {
            var modeError = Marshal.GetLastPInvokeError();
            Libc.Close(descriptor);
            throw IoFailureException.FromErrno(modeError, lockPath);
        }

        try
        {
            AcquireExclusive(descriptor, lockPath, timeoutMilliseconds, throwIfInterrupted);
        }
        catch
        {
            Libc.Close(descriptor);
            throw;
        }

        return new FileLockValue(descriptor, lockPath);
    }

    private static void AcquireExclusive(
        int descriptor,
        string lockPath,
        long timeoutMilliseconds,
        Action throwIfInterrupted)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            throwIfInterrupted();
            if (Libc.Flock(descriptor, Libc.FlockExclusive | Libc.FlockNonBlocking) == 0)
            {
                return;
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno != Errno.TryAgain)
            {
                throw IoFailureException.FromErrno(errno, lockPath);
            }

            if (timeoutMilliseconds > 0
                && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= timeoutMilliseconds)
            {
                throw new IoFailureException(
                    "timed_out",
                    lockPath,
                    "could not acquire the lock before the timeout");
            }

            Thread.Sleep(RetryMilliseconds);
        }
    }
}
