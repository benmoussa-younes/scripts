using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Scripts.Runtime.Platform;

/// <summary>
/// Opens a user-named file for writing without following a final symbolic link: the decisive
/// <c>open(2)</c> uses <c>O_NOFOLLOW|O_CLOEXEC</c>, so a path swapped for a link after an earlier
/// check is still refused. The write truncates the opened inode in place, which preserves its mode
/// and owner (a rename would replace the inode). A final symbolic link, including a dangling one,
/// fails with an <see cref="IOException"/> whose message is <c>the path is a symbolic link</c>.
/// Trailing slashes are trimmed before the no-follow call, so <c>link/</c> cannot turn the final link
/// into a followed directory prefix; <c>/</c> keeps its single slash.
/// </summary>
public static class NoFollowFileWriter
{
    /// <summary>The reason shared by the pre-check and the write-time refusal.</summary>
    public const string SymbolicLinkReason = "the path is a symbolic link";

    /// <summary>The default creation mode, <c>0666</c>, masked by the process umask as <c>open(2)</c> does.</summary>
    private const uint CreationMode = 0x1B6;

    /// <summary>
    /// Statuses <paramref name="path"/> without following it and returns true when the final component is a symbolic
    /// link, including a dangling one. A missing path returns false so the caller falls through to its loader; this is
    /// a best-effort early refusal before loading, not the decisive write-time check (<see cref="OpenAndTruncate"/>).
    /// </summary>
    public static bool IsFinalSymbolicLink(string path)
    {
        var nativePath = ToNativePath(PathText.TrimTrailingSlashes(path));
        return NativeFileSystem.TryGetStatus(nativePath, followLinks: false, out var status) == 0
            && status.IsSymbolicLink;
    }

    /// <summary>Opens an existing regular file and truncates it in place; a missing path is an error.</summary>
    public static FileStream OpenAndTruncate(string path) => Open(path, create: false);

    /// <summary>Creates the file when it is missing and truncates it in place otherwise.</summary>
    public static FileStream OpenCreateAndTruncate(string path) => Open(path, create: true);

    private static byte[] ToNativePath(string path)
    {
        try
        {
            return NativeFileSystem.ToNativePath(path);
        }
        catch (IoFailureException exception)
        {
            throw new IOException(exception.Reason);
        }
    }

    private static FileStream Open(string path, bool create)
    {
        var flags = Libc.OpenWriteOnly | Libc.OpenTruncate | Libc.OpenCloseOnExec | Libc.OpenNoFollow;
        if (create)
        {
            flags |= Libc.OpenCreate;
        }

        var nativePath = ToNativePath(PathText.TrimTrailingSlashes(path));
        while (true)
        {
            var descriptor = Libc.Open(nativePath, flags, CreationMode);
            if (descriptor >= 0)
            {
                var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
                try
                {
                    return new FileStream(handle, FileAccess.Write, 4096);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno == Errno.Interrupted)
            {
                continue;
            }

            throw new IOException(
                errno == Errno.TooManyLinks
                    ? SymbolicLinkReason
                    : Marshal.GetPInvokeErrorMessage(errno));
        }
    }
}
