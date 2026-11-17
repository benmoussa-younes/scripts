using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Scripts.Runtime.Platform;

/// <summary>Whole-file read/write/copy operations built on open/read/write (SPEC-STDLIB §7.2).</summary>
internal static class FileContentOperations
{
    private const long MaximumReadableFileBytes = SystemLimits.MaximumUnits;
    private const int CopyChunkBytes = 64 * 1024;
    private const uint DefaultCreationMode = 0x1B6;
    private const string AtomicTemporaryPrefix = ".scs-tmp-";
    private const int AtomicTemporaryNameAttempts = 128;

    // S15: only the 0o777 permission bits are propagated (like `cp` without `-p`); setuid/setgid/sticky are not.
    private const uint PermissionBitsMask = 0x1FF;

    /// <summary>Lowercase hex SHA-256 of the file bytes, streamed, with no size cap and no UTF-8 decoding (DESIGN §7.2).</summary>
    public static string Sha256File(string filePath)
    {
        var nativePath = NativeFileSystem.ToNativePath(filePath);
        var statusError = NativeFileSystem.TryGetStatus(nativePath, true, out var status);
        if (statusError != 0)
        {
            throw IoFailureException.FromErrno(statusError, filePath);
        }

        if (status.IsDirectory)
        {
            throw IoFailureException.FromErrno(Errno.IsADirectory, filePath);
        }

        if (!status.IsRegularFile)
        {
            throw IoFailureException.NotARegularFile(filePath);
        }

        var fileDescriptor = Libc.Open(nativePath, Libc.OpenReadOnly | Libc.OpenCloseOnExec, 0);
        if (fileDescriptor < 0)
        {
            throw IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), filePath);
        }

        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[CopyChunkBytes];
            while (true)
            {
                var bytesRead = Libc.Read(fileDescriptor, buffer, (nuint)buffer.Length);
                if (bytesRead < 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno == Errno.Interrupted)
                    {
                        continue;
                    }

                    throw IoFailureException.FromErrno(errno, filePath);
                }

                if (bytesRead == 0)
                {
                    break;
                }

                digest.AppendData(buffer, 0, (int)bytesRead);
            }

            return Convert.ToHexStringLower(digest.GetHashAndReset());
        }
        finally
        {
            Libc.Close(fileDescriptor);
        }
    }

    public static string ReadFile(string filePath)
    {
        var nativePath = NativeFileSystem.ToNativePath(filePath);
        var statusError = NativeFileSystem.TryGetStatus(nativePath, true, out var status);
        if (statusError != 0)
        {
            throw IoFailureException.FromErrno(statusError, filePath);
        }

        if (status.IsDirectory)
        {
            throw IoFailureException.FromErrno(Errno.IsADirectory, filePath);
        }

        if (!status.IsRegularFile)
        {
            throw IoFailureException.NotARegularFile(filePath);
        }

        if (status.SizeBytes > MaximumReadableFileBytes)
        {
            throw TooLarge(filePath);
        }

        var fileDescriptor = Libc.Open(nativePath, Libc.OpenReadOnly | Libc.OpenCloseOnExec, 0);
        if (fileDescriptor < 0)
        {
            throw IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), filePath);
        }

        try
        {
            return ReadDescriptorContent(fileDescriptor, filePath, status.SizeBytes);
        }
        finally
        {
            Libc.Close(fileDescriptor);
        }
    }

    /// <summary>
    /// Reads a whole regular file while refusing a final symbolic link (SPEC-STDLIB §7.2). Only the final component
    /// is refused: intermediate links are followed by the kernel (scripts has no <c>openat</c>/dirfd API). The path is
    /// opened first and the type/size is read from the descriptor, so a path/descriptor swap cannot change what is
    /// read; the error kinds match <see cref="ReadFile"/>, and a final link (including a dangling one) is
    /// <c>invalid_path</c>.
    /// </summary>
    public static string ReadFileNoFollow(string filePath, Action checkInterrupted)
    {
        // On Linux a trailing slash makes the final link a prefix component, which defeats O_NOFOLLOW; the preview
        // uses the trimmed path so a final link is still refused, while the readable open keeps the original path so
        // a regular file with a trailing slash fails with the kernel's ENOTDIR.
        var nativePath = NativeFileSystem.ToNativePath(filePath);
        var previewPath = NativeText.ToNullTerminated(PathText.TrimTrailingSlashes(filePath));
        var opened = RegularFileOpener.OpenReadableNoFollow(nativePath, previewPath, checkInterrupted);
        if (!opened.IsOpened)
        {
            throw ToReadFailure(opened, filePath);
        }

        try
        {
            var statusError = NativeFileSystem.TryGetStatusFromDescriptor(opened.Descriptor, out var status);
            if (statusError != 0)
            {
                throw IoFailureException.FromErrno(statusError, filePath);
            }

            if (status.SizeBytes > MaximumReadableFileBytes)
            {
                throw TooLarge(filePath);
            }

            return ReadDescriptorContent(opened.Descriptor, filePath, status.SizeBytes);
        }
        finally
        {
            Libc.Close(opened.Descriptor);
        }
    }

    /// <summary>Maps the shared opener's fault to the read error family; the only no-follow-specific fault is a link.</summary>
    private static IoFailureException ToReadFailure(RegularFileOpenResult opened, string filePath) =>
        opened.Fault switch
        {
            RegularFileFault.Directory => IoFailureException.FromErrno(Errno.IsADirectory, filePath),
            RegularFileFault.SymbolicLink => IoFailureException.InvalidPath(filePath, "the final component is a symbolic link"),
            RegularFileFault.NotRegular => IoFailureException.NotARegularFile(filePath),
            _ => IoFailureException.FromErrno(opened.Errno, filePath),
        };

    /// <summary>Reads an already-validated descriptor to EOF with the 2^28-byte cap and strict UTF-8 decode.</summary>
    private static string ReadDescriptorContent(int fileDescriptor, string filePath, long sizeBytes)
    {
        var content = new byte[Math.Max(sizeBytes + 1, 4096)];
        var contentLength = 0;
        while (true)
        {
            if (contentLength == content.Length)
            {
                if (contentLength > MaximumReadableFileBytes)
                {
                    throw TooLarge(filePath);
                }

                Array.Resize(ref content, (int)Math.Min((long)content.Length * 2, int.MaxValue - 64));
            }

            var bytesRead = Libc.Read(fileDescriptor, content.AsSpan(contentLength), (nuint)(content.Length - contentLength));
            if (bytesRead < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == Errno.Interrupted)
                {
                    continue;
                }

                throw IoFailureException.FromErrno(errno, filePath);
            }

            if (bytesRead == 0)
            {
                break;
            }

            contentLength += (int)bytesRead;
        }

        if (contentLength > MaximumReadableFileBytes)
        {
            throw TooLarge(filePath);
        }

        return NativeText.TryDecodeStrict(content.AsSpan(0, contentLength), out var text)
            ? text
            : throw new IoFailureException(IoErrorKinds.InvalidEncoding, filePath, "the content is not valid UTF-8 text");
    }

    /// <summary>
    /// Reads at most <paramref name="maximumBytes"/> bytes of the file and decodes them as strict UTF-8
    /// (SPEC-STDLIB §7.10a). A file holding more than the bound returns <c>too_large</c>: the method never reads
    /// beyond <c>maximumBytes + 1</c> bytes, and that extra byte only decides the overflow (it is never decoded). A
    /// negative bound is <c>invalid_argument</c>. The other failures match <see cref="ReadFile"/>, and the read is
    /// additionally capped at the same 256 MiB maximum so that a huge bound can never exceed the string-length
    /// limit (DESIGN §5.8).
    /// </summary>
    public static string ReadFileBounded(string filePath, long maximumBytes)
    {
        if (maximumBytes < 0)
        {
            throw new IoFailureException(IoErrorKinds.InvalidArgument, filePath, "maximum_bytes must not be negative");
        }

        var nativePath = NativeFileSystem.ToNativePath(filePath);
        var statusError = NativeFileSystem.TryGetStatus(nativePath, true, out var status);
        if (statusError != 0)
        {
            throw IoFailureException.FromErrno(statusError, filePath);
        }

        if (status.IsDirectory)
        {
            throw IoFailureException.FromErrno(Errno.IsADirectory, filePath);
        }

        if (!status.IsRegularFile)
        {
            throw IoFailureException.NotARegularFile(filePath);
        }

        var effectiveMaximum = Math.Min(maximumBytes, MaximumReadableFileBytes);
        var capacity = effectiveMaximum + 1; // the single overflow-deciding byte

        // The size from statx already answers for a regular file; the read loop below catches a file that grows
        // after this check. Refusing here avoids allocating or reading anything for a known-large file.
        if (status.SizeBytes > effectiveMaximum)
        {
            throw BoundedTooLarge(filePath, effectiveMaximum);
        }

        var fileDescriptor = Libc.Open(nativePath, Libc.OpenReadOnly | Libc.OpenCloseOnExec, 0);
        if (fileDescriptor < 0)
        {
            throw IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), filePath);
        }

        try
        {
            var content = new byte[(int)Math.Min(Math.Max(status.SizeBytes + 1, 4096), capacity)];
            var contentLength = 0;
            while (true)
            {
                if (contentLength == content.Length)
                {
                    if (content.Length >= capacity)
                    {
                        throw BoundedTooLarge(filePath, effectiveMaximum);
                    }

                    Array.Resize(ref content, (int)Math.Min((long)content.Length * 2, capacity));
                }

                var bytesRead = Libc.Read(fileDescriptor, content.AsSpan(contentLength), (nuint)(content.Length - contentLength));
                if (bytesRead < 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno == Errno.Interrupted)
                    {
                        continue;
                    }

                    throw IoFailureException.FromErrno(errno, filePath);
                }

                if (bytesRead == 0)
                {
                    break;
                }

                contentLength += (int)bytesRead;
                if (contentLength > effectiveMaximum)
                {
                    throw BoundedTooLarge(filePath, effectiveMaximum);
                }
            }

            return NativeText.TryDecodeStrict(content.AsSpan(0, contentLength), out var text)
                ? text
                : throw new IoFailureException(IoErrorKinds.InvalidEncoding, filePath, "the content is not valid UTF-8 text");
        }
        finally
        {
            Libc.Close(fileDescriptor);
        }
    }

    public static void WriteFile(string content, string destinationPath, bool append)
    {
        var nativePath = NativeFileSystem.ToNativePath(destinationPath);
        RefuseFifoOrSocketDestination(nativePath, destinationPath, followLinks: true);
        var creationFlags = Libc.OpenWriteOnly | Libc.OpenCreate | Libc.OpenCloseOnExec | (append ? Libc.OpenAppend : Libc.OpenTruncate);
        var fileDescriptor = Libc.Open(nativePath, creationFlags, DefaultCreationMode);
        if (fileDescriptor < 0)
        {
            throw IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), destinationPath);
        }

        try
        {
            WriteAll(fileDescriptor, NativeText.ToBytes(content), destinationPath);
        }
        catch
        {
            Libc.Close(fileDescriptor);
            throw;
        }

        CloseChecked(fileDescriptor, destinationPath);
    }

    /// <summary>
    /// Creates the destination and writes the text only if it does not exist (O_CREAT|O_EXCL): the race-free
    /// `noclobber` primitive (SPEC-STDLIB §7.2). A final symbolic link is refused with `already_exists`.
    /// </summary>
    public static void WriteFileExclusive(string content, string destinationPath)
    {
        var nativePath = NativeFileSystem.ToNativePath(destinationPath);
        var fileDescriptor = Libc.Open(
            nativePath,
            Libc.OpenWriteOnly | Libc.OpenCreate | Libc.OpenExclusive | Libc.OpenCloseOnExec,
            DefaultCreationMode);
        if (fileDescriptor < 0)
        {
            throw IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), destinationPath);
        }

        try
        {
            WriteAll(fileDescriptor, NativeText.ToBytes(content), destinationPath);
        }
        catch
        {
            Libc.Close(fileDescriptor);
            throw;
        }

        CloseChecked(fileDescriptor, destinationPath);
    }

    /// <summary>
    /// Creates the destination exclusively and applies <paramref name="permissions"/> exactly (SPEC-STDLIB §7.2). The
    /// creation mode is already the requested mode, so the transient mode is <c>permissions &amp; ~umask</c>, never more
    /// permissive; <c>fchmod(2)</c> on the descriptor then makes it exact without a rename race. A failing mode or write
    /// removes the new file when the path still resolves to the inode captured from the descriptor; the check and the
    /// removal are separate syscalls, so this cleanup is best-effort.
    /// </summary>
    public static void WriteFileExclusiveWithPermissions(string content, string destinationPath, long permissions)
    {
        if (permissions < 0 || permissions > PermissionBitsMask)
        {
            throw new IoFailureException(IoErrorKinds.InvalidArgument, destinationPath, "the permissions must be between 0 and 0o777");
        }

        var nativePath = NativeFileSystem.ToNativePath(destinationPath);
        var fileDescriptor = Libc.Open(
            nativePath,
            Libc.OpenWriteOnly | Libc.OpenCreate | Libc.OpenExclusive | Libc.OpenCloseOnExec,
            (uint)permissions);
        if (fileDescriptor < 0)
        {
            throw IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), destinationPath);
        }

        var statusError = NativeFileSystem.TryGetStatusFromDescriptor(fileDescriptor, out var createdStatus);
        if (statusError != 0)
        {
            Libc.Close(fileDescriptor);
            throw IoFailureException.FromErrno(statusError, destinationPath);
        }

        try
        {
            if (Libc.ChangeFileMode(fileDescriptor, (uint)permissions) != 0)
            {
                throw IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), destinationPath);
            }

            WriteAll(fileDescriptor, NativeText.ToBytes(content), destinationPath);
        }
        catch (IoFailureException failure)
        {
            // Keep the descriptor open while the cleanup runs so the captured inode cannot be recycled.
            var remaining = FileSystemMutations.RemoveCreatedEntryOrOriginal(
                failure,
                nativePath,
                destinationPath,
                createdStatus,
                directory: false);
            Libc.Close(fileDescriptor);
            throw remaining;
        }

        CloseChecked(fileDescriptor, destinationPath);
    }

    /// <summary>
    /// Writes <paramref name="content"/> through a scratch file created in the destination directory, then replaces the
    /// destination with <c>rename(2)</c> (atomic replace, SPEC-STDLIB §7.10a). On any failure the scratch file is removed,
    /// so a reader observes either the previous or the new content, never a truncated file. A symbolic link destination is
    /// replaced by a regular file (unlike <see cref="WriteFile"/>, which writes through the link).
    /// </summary>
    public static void WriteFileAtomically(string content, string destinationPath)
    {
        var nativeDestination = NativeFileSystem.ToNativePath(destinationPath);
        RefuseFifoOrSocketDestination(nativeDestination, destinationPath, followLinks: false);

        if (NativeFileSystem.TryGetStatus(nativeDestination, false, out var destinationStatus) == 0 && destinationStatus.IsDirectory)
        {
            throw IoFailureException.FromErrno(Errno.IsADirectory, destinationPath);
        }

        var bytes = NativeText.ToBytes(content);
        for (var attempt = 0; attempt < AtomicTemporaryNameAttempts; attempt++)
        {
            var temporaryPath = TemporarySiblingPath(destinationPath);
            var nativeTemporary = NativeText.ToNullTerminated(temporaryPath);
            var temporaryDescriptor = Libc.Open(
                nativeTemporary,
                Libc.OpenWriteOnly | Libc.OpenCreate | Libc.OpenExclusive | Libc.OpenCloseOnExec,
                DefaultCreationMode);
            if (temporaryDescriptor < 0)
            {
                var openError = Marshal.GetLastPInvokeError();
                if (openError == Errno.AlreadyExists)
                {
                    continue;
                }

                throw IoFailureException.FromErrno(openError, destinationPath);
            }

            try
            {
                WriteAll(temporaryDescriptor, bytes, destinationPath);
                Synchronize(temporaryDescriptor, destinationPath);
            }
            catch
            {
                Libc.Close(temporaryDescriptor);
                RemoveTemporary(temporaryPath);
                throw;
            }

            if (Libc.Close(temporaryDescriptor) != 0)
            {
                var closeError = Marshal.GetLastPInvokeError();
                RemoveTemporary(temporaryPath);
                throw IoFailureException.FromErrno(closeError, destinationPath);
            }

            if (Libc.Rename(nativeTemporary, nativeDestination) != 0)
            {
                var renameError = Marshal.GetLastPInvokeError();
                RemoveTemporary(temporaryPath);
                throw IoFailureException.FromErrno(renameError, destinationPath);
            }

            return;
        }

        throw new IoFailureException(IoErrorKinds.Other, destinationPath, "could not create a temporary file");
    }

    /// <summary>Path of the scratch file: a hidden name in the directory of the destination (same file system).</summary>
    private static string TemporarySiblingPath(string destinationPath)
    {
        var lastSeparator = destinationPath.LastIndexOf('/');
        var directory = lastSeparator switch
        {
            < 0 => ".",
            0 => "/",
            _ => destinationPath[..lastSeparator],
        };

        return PathText.Join(directory, AtomicTemporaryPrefix + FileSystemMutations.CreateTemporarySuffix());
    }

    private static void RemoveTemporary(string temporaryPath) =>
        Libc.Unlink(NativeText.ToNullTerminated(temporaryPath));

    /// <summary>Flushes the scratch file to disk, retrying EINTR like the other blocking loops.</summary>
    private static void Synchronize(int fileDescriptor, string subject)
    {
        while (Libc.SynchronizeFile(fileDescriptor) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == Errno.Interrupted)
            {
                continue;
            }

            throw IoFailureException.FromErrno(errno, subject);
        }
    }

    public static void CopyFile(string sourcePath, string destinationPath, bool overwrite, Action throwIfInterrupted)
    {
        var nativeSource = NativeFileSystem.ToNativePath(sourcePath);
        var nativeDestination = NativeFileSystem.ToNativePath(destinationPath);
        var statusError = NativeFileSystem.TryGetStatus(nativeSource, true, out var sourceStatus);
        if (statusError != 0)
        {
            throw IoFailureException.FromErrno(statusError, sourcePath);
        }

        if (sourceStatus.IsDirectory)
        {
            throw IoFailureException.FromErrno(Errno.IsADirectory, sourcePath);
        }

        if (!sourceStatus.IsRegularFile)
        {
            throw IoFailureException.NotARegularFile(sourcePath);
        }

        var sourceDescriptor = Libc.Open(nativeSource, Libc.OpenReadOnly | Libc.OpenCloseOnExec, 0);
        if (sourceDescriptor < 0)
        {
            throw IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), sourcePath);
        }

        try
        {
            CopyOpenedSource(sourceDescriptor, sourceStatus, sourcePath, destinationPath, nativeDestination, overwrite, throwIfInterrupted);
        }
        finally
        {
            Libc.Close(sourceDescriptor);
        }
    }

    private static void CopyOpenedSource(
        int sourceDescriptor,
        NativeFileStatus sourceStatus,
        string sourcePath,
        string destinationPath,
        byte[] nativeDestination,
        bool overwrite,
        Action throwIfInterrupted)
    {
        var destinationExists = NativeFileSystem.TryGetStatus(nativeDestination, true, out var destinationStatus) == 0;
        if (destinationExists)
        {
            if (destinationStatus.IsDirectory)
            {
                throw IoFailureException.FromErrno(Errno.IsADirectory, destinationPath);
            }

            if (destinationStatus.IsSameFileAs(sourceStatus))
            {
                throw IoFailureException.SameFile(sourcePath);
            }

            if (!overwrite)
            {
                throw IoFailureException.FromErrno(Errno.AlreadyExists, destinationPath);
            }

            // S16: never block on an existing FIFO or socket with no reader; refuse before truncation
            // (filesystem.copy_file, without overwrite, already reports `already_exists` above).
            if (destinationStatus.IsFifo || destinationStatus.IsSocket)
            {
                throw IoFailureException.NotARegularFile(destinationPath);
            }
        }

        var createsDestination = !destinationExists;
        var destinationFlags = Libc.OpenWriteOnly | Libc.OpenCloseOnExec;
        if (createsDestination)
        {
            destinationFlags |= Libc.OpenCreate | (overwrite ? Libc.OpenTruncate : Libc.OpenExclusive);
        }
        else
        {
            destinationFlags |= Libc.OpenTruncate;
        }

        var destinationDescriptor = Libc.Open(nativeDestination, destinationFlags, sourceStatus.Mode & PermissionBitsMask);
        if (destinationDescriptor < 0)
        {
            throw IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), destinationPath);
        }

        try
        {
            CopyContent(sourceDescriptor, destinationDescriptor, sourcePath, destinationPath, throwIfInterrupted);
            if (createsDestination && Libc.ChangeFileMode(destinationDescriptor, sourceStatus.Mode & PermissionBitsMask) != 0)
            {
                throw IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), destinationPath);
            }
        }
        catch
        {
            Libc.Close(destinationDescriptor);
            if (createsDestination)
            {
                Libc.Unlink(nativeDestination);
            }

            throw;
        }

        CloseChecked(destinationDescriptor, destinationPath);
    }

    private static void CopyContent(
        int sourceDescriptor,
        int destinationDescriptor,
        string sourcePath,
        string destinationPath,
        Action throwIfInterrupted)
    {
        var chunk = new byte[CopyChunkBytes];
        while (true)
        {
            throwIfInterrupted();
            var bytesRead = Libc.Read(sourceDescriptor, chunk, (nuint)chunk.Length);
            if (bytesRead < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == Errno.Interrupted)
                {
                    continue;
                }

                throw IoFailureException.FromErrno(errno, sourcePath);
            }

            if (bytesRead == 0)
            {
                return;
            }

            WriteAll(destinationDescriptor, chunk.AsSpan(0, (int)bytesRead), destinationPath);
        }
    }

    private static void WriteAll(int fileDescriptor, ReadOnlySpan<byte> bytes, string subject)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var bytesWritten = Libc.Write(fileDescriptor, bytes[offset..], (nuint)(bytes.Length - offset));
            if (bytesWritten < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == Errno.Interrupted)
                {
                    continue;
                }

                throw IoFailureException.FromErrno(errno, subject);
            }

            offset += (int)bytesWritten;
        }
    }

    private static void CloseChecked(int fileDescriptor, string subject)
    {
        if (Libc.Close(fileDescriptor) != 0)
        {
            throw IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), subject);
        }
    }

    private static IoFailureException TooLarge(string filePath) =>
        new(IoErrorKinds.TooLarge, filePath, $"file too large (maximum {MaximumReadableFileBytes} bytes)");

    private static IoFailureException BoundedTooLarge(string filePath, long maximumBytes) =>
        new(IoErrorKinds.TooLarge, filePath, $"file is larger than the maximum of {maximumBytes} bytes");

    /// <summary>Refuses a destination that exists as a FIFO or socket before opening it for writing (S16).</summary>
    private static void RefuseFifoOrSocketDestination(ReadOnlySpan<byte> nativePath, string destinationPath, bool followLinks)
    {
        if (NativeFileSystem.IsFifoOrSocket(nativePath, followLinks))
        {
            throw IoFailureException.NotARegularFile(destinationPath);
        }
    }

    /// <summary>
    /// Streaming counterpart of <see cref="WriteFileAtomically(string, string)"/>: the caller pushes the bytes of a
    /// pipeline's last stage into a scratch file in the destination directory, then <see cref="Commit"/> fsyncs it and
    /// renames it over the destination. Until the commit a reader only sees the previous file; <see cref="Dispose"/>
    /// removes the scratch file of an aborted write (SPEC-STDLIB §7.10,).
    /// </summary>
    internal sealed class AtomicFileTarget : IDisposable
    {
        private readonly string destinationPath;
        private readonly byte[] nativeDestination;
        private int descriptor;
        private string? temporaryPath;
        private bool committed;
        private readonly object gate = new();

        private AtomicFileTarget(string destinationPath, byte[] nativeDestination, int descriptor, string temporaryPath)
        {
            this.destinationPath = destinationPath;
            this.nativeDestination = nativeDestination;
            this.descriptor = descriptor;
            this.temporaryPath = temporaryPath;
        }

        public static AtomicFileTarget Create(string destinationPath)
        {
            var nativeDestination = NativeFileSystem.ToNativePath(destinationPath);
            RefuseFifoOrSocketDestination(nativeDestination, destinationPath, followLinks: false);

            if (NativeFileSystem.TryGetStatus(nativeDestination, false, out var destinationStatus) == 0 && destinationStatus.IsDirectory)
            {
                throw IoFailureException.FromErrno(Errno.IsADirectory, destinationPath);
            }

            for (var attempt = 0; attempt < AtomicTemporaryNameAttempts; attempt++)
            {
                var temporaryPath = TemporarySiblingPath(destinationPath);
                var nativeTemporary = NativeText.ToNullTerminated(temporaryPath);
                var temporaryDescriptor = Libc.Open(
                    nativeTemporary,
                    Libc.OpenWriteOnly | Libc.OpenCreate | Libc.OpenExclusive | Libc.OpenCloseOnExec,
                    DefaultCreationMode);
                if (temporaryDescriptor < 0)
                {
                    var openError = Marshal.GetLastPInvokeError();
                    if (openError == Errno.AlreadyExists)
                    {
                        continue;
                    }

                    throw IoFailureException.FromErrno(openError, destinationPath);
                }

                return new AtomicFileTarget(destinationPath, nativeDestination, temporaryDescriptor, temporaryPath);
            }

            throw new IoFailureException(IoErrorKinds.Other, destinationPath, "could not create a temporary file");
        }

        public void Append(ReadOnlySpan<byte> bytes)
        {
            lock (gate)
            {
                WriteAll(descriptor, bytes, destinationPath);
            }
        }

        /// <summary>fsync, close and rename the scratch file over the destination; the target is then complete.</summary>
        public void Commit()
        {
            lock (gate)
            {
                CommitCore();
            }
        }

        private void CommitCore()
        {
            if (committed)
            {
                return;
            }

            Synchronize(descriptor, destinationPath);
            var openDescriptor = descriptor;
            descriptor = -1;
            if (Libc.Close(openDescriptor) != 0)
            {
                var closeError = Marshal.GetLastPInvokeError();
                Abandon();
                throw IoFailureException.FromErrno(closeError, destinationPath);
            }

            if (Libc.Rename(NativeText.ToNullTerminated(temporaryPath!), nativeDestination) != 0)
            {
                var renameError = Marshal.GetLastPInvokeError();
                Abandon();
                throw IoFailureException.FromErrno(renameError, destinationPath);
            }

            committed = true;
            temporaryPath = null;
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (descriptor >= 0)
                {
                    Libc.Close(descriptor);
                    descriptor = -1;
                }

                Abandon();
            }
        }

        private void Abandon()
        {
            if (committed || temporaryPath is null)
            {
                return;
            }

            RemoveTemporary(temporaryPath);
            temporaryPath = null;
        }
    }
}
