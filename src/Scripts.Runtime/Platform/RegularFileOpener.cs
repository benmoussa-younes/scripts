using System.Runtime.InteropServices;

namespace Scripts.Runtime.Platform;

/// <summary>Why a regular-file open or descriptor check failed; the errno is meaningful for <see cref="Errno"/> only.</summary>
internal enum RegularFileFault
{
    None,
    Directory,
    SymbolicLink,
    NotRegular,
    Errno,
}

/// <summary>The result of <see cref="RegularFileOpener"/>: an opened descriptor, or the classified fault.</summary>
internal readonly record struct RegularFileOpenResult(int Descriptor, RegularFileFault Fault, int Errno)
{
    public bool IsOpened => Fault == RegularFileFault.None;

    public static RegularFileOpenResult Opened(int descriptor) => new(descriptor, RegularFileFault.None, 0);

    public static RegularFileOpenResult Of(RegularFileFault fault) => new(-1, fault, 0);

    public static RegularFileOpenResult Failed(int errno) => new(-1, RegularFileFault.Errno, errno);
}

/// <summary>
/// The one opener/classifier of a regular file for reading (DESIGN §7.2/§7.3): an <c>O_PATH</c> preview classifies
/// the target before any readable open (a directory/FIFO/device/socket is rejected without a readable side effect),
/// then the readable open uses <c>O_NONBLOCK|O_NOCTTY|O_CLOEXEC</c> and re-validates the type on the opened descriptor
/// with <c>statx(fd, "", AT_EMPTY_PATH, …)</c>; this catches a type change between the two opens, but a
/// regular-to-regular swap is not detected. A failed open is classified by a no-open path <c>statx</c>. The caller owns
/// the opened descriptor and maps <see cref="RegularFileFault"/> to its own error family (I/O or process).
/// </summary>
internal static class RegularFileOpener
{
    /// <summary>Opens a regular file for reading, following a final symbolic link; the caller closes the descriptor.</summary>
    public static RegularFileOpenResult OpenReadable(ReadOnlySpan<byte> nativePath, Action checkInterrupted) =>
        OpenReadable(nativePath, nativePath, followLinks: true, checkInterrupted);

    /// <summary>
    /// Opens a regular file for reading while refusing a final symbolic link. <paramref name="nativePath"/> is the
    /// path handed to <c>open(2)</c> and <paramref name="previewPath"/> the path whose trailing slashes were trimmed
    /// for the no-follow preview: on Linux a trailing slash can only resolve to a directory (otherwise <c>ENOTDIR</c>),
    /// so it prevents no read, but without the trim a final link with a slash would be reported <c>not_a_directory</c>
    /// instead of <c>invalid_path</c>. The readable open keeps the original path, so a regular file with a trailing
    /// slash still fails with the kernel's <c>ENOTDIR</c>. The caller closes the descriptor.
    /// </summary>
    public static RegularFileOpenResult OpenReadableNoFollow(
        ReadOnlySpan<byte> nativePath,
        ReadOnlySpan<byte> previewPath,
        Action checkInterrupted) =>
        OpenReadable(nativePath, previewPath, followLinks: false, checkInterrupted);

    private static RegularFileOpenResult OpenReadable(
        ReadOnlySpan<byte> nativePath,
        ReadOnlySpan<byte> previewPath,
        bool followLinks,
        Action checkInterrupted)
    {
        if (Preview(previewPath, followLinks, checkInterrupted) is { } previewFault)
        {
            return previewFault;
        }

        var openFlags = Libc.OpenReadOnly | Libc.OpenNonBlock | Libc.OpenNoCtty | Libc.OpenCloseOnExec;
        if (!followLinks)
        {
            openFlags |= Libc.OpenNoFollow;
        }

        while (true)
        {
            checkInterrupted();
            var fileDescriptor = Libc.Open(nativePath, openFlags, 0);
            if (fileDescriptor >= 0)
            {
                // checkInterrupted can throw (SIGINT) and statx can fail: close the freshly opened descriptor on
                // every path, including the throwing one, so an interruption cannot leak it.
                RegularFileOpenResult verified;
                try
                {
                    verified = VerifyDescriptor(fileDescriptor, checkInterrupted);
                }
                catch
                {
                    _ = Libc.Close(fileDescriptor);
                    throw;
                }

                if (!verified.IsOpened)
                {
                    _ = Libc.Close(fileDescriptor);
                }

                return verified;
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno == Errno.Interrupted)
            {
                continue;
            }

            return ClassifyOpenFailure(nativePath, errno, followLinks, checkInterrupted);
        }
    }

    /// <summary>Classifies an already-open descriptor (the caller owns it and is responsible for closing it).</summary>
    public static RegularFileOpenResult VerifyDescriptor(int fileDescriptor, Action checkInterrupted)
    {
        while (true)
        {
            checkInterrupted();
            var errno = NativeFileSystem.TryGetStatusFromDescriptor(fileDescriptor, out var status);
            if (errno == Errno.Interrupted)
            {
                continue;
            }

            if (errno != 0)
            {
                return RegularFileOpenResult.Failed(errno);
            }

            if (status.IsDirectory)
            {
                return RegularFileOpenResult.Of(RegularFileFault.Directory);
            }

            // O_PATH|O_NOFOLLOW opens the link itself: the descriptor is a link, never its target.
            if (status.IsSymbolicLink)
            {
                return RegularFileOpenResult.Of(RegularFileFault.SymbolicLink);
            }

            if (!status.IsRegularFile)
            {
                return RegularFileOpenResult.Of(RegularFileFault.NotRegular);
            }

            return RegularFileOpenResult.Opened(fileDescriptor);
        }
    }

    /// <summary>
    /// <c>O_PATH</c> preview: opens any target without reading it and rejects a directory or non-regular file from
    /// <c>statx(fd)</c>. When <paramref name="followLinks"/> is false the preview opens the final link itself, so a
    /// symbolic link is classified as such instead of being resolved. A failed preview is classified by the path, so
    /// an unreadable directory stays a directory. Returns null on success (the preview descriptor is closed here and
    /// never handed out) or the classified fault.
    /// </summary>
    private static RegularFileOpenResult? Preview(
        ReadOnlySpan<byte> nativePath,
        bool followLinks,
        Action checkInterrupted)
    {
        var previewFlags = Libc.OpenPath | Libc.OpenCloseOnExec | (followLinks ? 0 : Libc.OpenNoFollow);
        while (true)
        {
            checkInterrupted();
            var pathDescriptor = Libc.Open(nativePath, previewFlags, 0);
            if (pathDescriptor < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == Errno.Interrupted)
                {
                    continue;
                }

                return ClassifyOpenFailure(nativePath, errno, followLinks, checkInterrupted);
            }

            try
            {
                var status = VerifyDescriptor(pathDescriptor, checkInterrupted);
                return status.IsOpened ? null : status;
            }
            finally
            {
                _ = Libc.Close(pathDescriptor);
            }
        }
    }

    /// <summary>
    /// A failed open (often <c>EACCES</c> on a directory/FIFO): classify the target with a no-open path <c>statx</c>
    /// so an unreadable directory is a directory and an unreadable FIFO is not regular. A regular file keeps the open
    /// errno (typically <c>EACCES</c>). <c>ELOOP</c> keeps its errno path (<c>invalid_path</c>) instead of being
    /// classified as a regular file, and a no-follow classification reports the link itself. A no-follow call
    /// classifies with <c>AT_SYMLINK_NOFOLLOW</c>.
    /// </summary>
    private static RegularFileOpenResult ClassifyOpenFailure(
        ReadOnlySpan<byte> nativePath,
        int openErrno,
        bool followLinks,
        Action checkInterrupted)
    {
        if (openErrno == Errno.TooManyLinks)
        {
            return RegularFileOpenResult.Failed(openErrno);
        }

        while (true)
        {
            checkInterrupted();
            var statusErrno = NativeFileSystem.TryGetStatus(nativePath, followLinks, out var status);
            if (statusErrno == Errno.Interrupted)
            {
                continue;
            }

            if (statusErrno != 0)
            {
                return RegularFileOpenResult.Failed(statusErrno);
            }

            if (status.IsDirectory)
            {
                return RegularFileOpenResult.Of(RegularFileFault.Directory);
            }

            if (status.IsSymbolicLink)
            {
                return RegularFileOpenResult.Of(RegularFileFault.SymbolicLink);
            }

            if (!status.IsRegularFile)
            {
                return RegularFileOpenResult.Of(RegularFileFault.NotRegular);
            }

            return RegularFileOpenResult.Failed(openErrno);
        }
    }
}
