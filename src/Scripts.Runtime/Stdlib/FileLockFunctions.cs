using System.Runtime.InteropServices;
using Scripts.Runtime.Platform;

namespace Scripts.Runtime.Stdlib;

/// <summary>Advisory exclusive file locks (DESIGN §3.1/§7.2): acquire through filesystem, release through the handle.</summary>
internal static class FileLockFunctions
{
    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["filesystem.acquire_file_lock"] = Acquire;
        table["file_lock.release"] = Release;
    }

    private static Value Acquire(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardIo(() => SystemValues.Success(FileLocks.Acquire(
            SystemValues.PathAt(arguments, 0),
            SystemValues.IntegerAt(arguments, 1),
            context.ThrowIfInterrupted)));

    private static Value Release(RuntimeContext context, Value[] arguments)
    {
        var lockValue = (FileLockValue)arguments[0];
        var descriptor = lockValue.TakeFileDescriptor();
        if (descriptor < 0)
        {
            return SystemValues.Success();
        }

        if (Libc.Close(descriptor) != 0)
        {
            return SystemValues.IoFailure(
                IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), lockValue.LockPath));
        }

        return SystemValues.Success();
    }
}
