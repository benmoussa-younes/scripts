using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Scripts.Runtime.Platform;

/// <summary>Filesystem mutations (SPEC-STDLIB §7.2). Failures are raised as IoFailureException.</summary>
internal static class FileSystemMutations
{
    private const uint DirectoryCreationMode = 0x1FF;
    private const uint PrivateDirectoryMode = 0x1C0;
    private const uint ExactCreationPermissionBits = 0x1FF;
    private const int TemporarySuffixLength = 8;
    private const int TemporaryPrefixMaximumLength = 64;
    private const int TemporaryNameAttempts = 128;
    private const string TemporarySuffixAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public static void RemoveFile(string filePath) =>
        ThrowIfFailed(Libc.Unlink(NativeFileSystem.ToNativePath(filePath)), filePath);

    public static void RemoveEmptyDirectory(string directoryPath) =>
        ThrowIfFailed(Libc.RemoveDirectory(NativeFileSystem.ToNativePath(directoryPath)), directoryPath);

    public static void MakeDirectory(string directoryPath) =>
        ThrowIfFailed(Libc.MakeDirectory(NativeFileSystem.ToNativePath(directoryPath), DirectoryCreationMode), directoryPath);

    /// <summary>
    /// Creates the directory and applies <paramref name="permissions"/> exactly (SPEC-STDLIB §7.2). The creation mode
    /// is the request, so the transient mode is <c>permissions &amp; ~umask</c>, never more permissive; the exact mode is
    /// then applied with <c>fchmod(2)</c> on a no-follow descriptor, never by path. The descriptor must be the directory
    /// captured after <c>mkdir</c>, otherwise the call fails with <c>invalid_path</c> without changing any mode. The
    /// descriptor step requires the owner read bit to survive the umask; when it does not, the call fails closed with
    /// <c>permission_denied</c> and removes the directory it created. Trailing slashes are trimmed before every
    /// no-follow operation.
    /// </summary>
    public static void MakeDirectoryWithPermissions(string directoryPath, long permissions)
    {
        if (permissions < 0 || permissions > ExactCreationPermissionBits)
        {
            throw new IoFailureException(IoErrorKinds.InvalidArgument, directoryPath, "the permissions must be between 0 and 0o777");
        }

        var trimmedPath = PathText.TrimTrailingSlashes(directoryPath);
        var nativePath = NativeFileSystem.ToNativePath(trimmedPath);
        ThrowIfFailed(Libc.MakeDirectory(nativePath, (uint)permissions), directoryPath);

        var statusError = NativeFileSystem.TryGetStatus(nativePath, followLinks: false, out var createdStatus);
        if (statusError != 0)
        {
            throw IoFailureException.FromErrno(statusError, directoryPath);
        }

        try
        {
            ApplyDirectoryModeFromPath(directoryPath, (uint)permissions, directoryPath, createdStatus);
        }
        catch (IoFailureException failure)
        {
            throw RemoveCreatedEntryOrOriginal(failure, nativePath, trimmedPath, createdStatus, directory: true);
        }
    }

    /// <summary>
    /// Opens an already-created directory without following a final symbolic link, checks the descriptor is the
    /// directory captured after <c>mkdir</c>, then applies <paramref name="permissions"/> on that descriptor. The
    /// trailing slash is trimmed first: on Linux <c>"d/"</c> resolves as <c>"d/."</c>, so a final symlink would
    /// become a prefix component and defeat <c>O_NOFOLLOW</c>. A final symlink or a different directory is
    /// <c>invalid_path</c>; cleanup of the created inode is the caller's responsibility.
    /// </summary>
    internal static void ApplyDirectoryModeFromPath(
        string directoryPath,
        uint permissions,
        string subject,
        NativeFileStatus expectedStatus)
    {
        var nativePath = NativeFileSystem.ToNativePath(PathText.TrimTrailingSlashes(directoryPath));
        var directoryDescriptor = Libc.Open(
            nativePath,
            Libc.OpenReadOnly | Libc.OpenDirectoryFlag | Libc.OpenNoFollow | Libc.OpenCloseOnExec,
            0);
        if (directoryDescriptor < 0)
        {
            var openError = Marshal.GetLastPInvokeError();
            // O_DIRECTORY|O_NOFOLLOW reports ELOOP for a final symlink in some kernels and ENOTDIR in others:
            // both mean the path created as a directory was replaced by a non-directory entry.
            throw openError switch
            {
                Errno.TooManyLinks => IoFailureException.InvalidPath(subject, "the created path is a symbolic link"),
                Errno.NotADirectory => IoFailureException.InvalidPath(subject, "the created path is not a directory"),
                _ => IoFailureException.FromErrno(openError, subject),
            };
        }

        try
        {
            var statusError = NativeFileSystem.TryGetStatusFromDescriptor(directoryDescriptor, out var openedStatus);
            if (statusError != 0)
            {
                throw IoFailureException.FromErrno(statusError, subject);
            }

            if (!openedStatus.IsSameFileAs(expectedStatus))
            {
                throw IoFailureException.InvalidPath(subject, "the created path was replaced by another directory");
            }

            if (Libc.ChangeFileMode(directoryDescriptor, permissions) != 0)
            {
                throw IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), subject);
            }
        }
        finally
        {
            Libc.Close(directoryDescriptor);
        }
    }

    /// <summary>
    /// Removes the entry this call created when the path still resolves without following to the recorded device and
    /// inode. The comparison is immediately before the removal, but the check and the <c>unlink</c>/<c>rmdir</c> are
    /// separate syscalls, so the guarantee is best-effort: a swap in the remaining window is not detectable and the
    /// entry then at the path may be removed. When the guarded removal fails, the original error keeps its kind and
    /// subject and its message names the remaining path.
    /// </summary>
    internal static IoFailureException RemoveCreatedEntryOrOriginal(
        IoFailureException original,
        byte[] nativePath,
        string pathText,
        NativeFileStatus createdStatus,
        bool directory)
    {
        var statusError = NativeFileSystem.TryGetStatus(nativePath, followLinks: false, out var currentStatus);
        if (statusError == Errno.NoSuchEntry)
        {
            return original;
        }

        if (statusError != 0)
        {
            return RemainingPath(original, pathText);
        }

        if (!currentStatus.IsSameFileAs(createdStatus))
        {
            return original;
        }

        var removalError = directory ? Libc.RemoveDirectory(nativePath) : Libc.Unlink(nativePath);
        if (removalError == 0 || Marshal.GetLastPInvokeError() == Errno.NoSuchEntry)
        {
            return original;
        }

        return RemainingPath(original, pathText);
    }

    private static IoFailureException RemainingPath(IoFailureException original, string pathText) =>
        new(original.Kind, original.Subject, $"{original.Reason}; the created path may remain at {pathText}");

    public static void ChangeDirectory(string directoryPath) =>
        ThrowIfFailed(Libc.ChangeDirectory(NativeFileSystem.ToNativePath(directoryPath)), directoryPath);

    public static void MakeDirectoryTree(string directoryPath)
    {
        var nativePath = NativeFileSystem.ToNativePath(directoryPath);
        if (NativeFileSystem.TryGetStatus(nativePath, true, out var existingStatus) == 0)
        {
            if (!existingStatus.IsDirectory)
            {
                throw IoFailureException.FromErrno(Errno.AlreadyExists, directoryPath);
            }

            return;
        }

        var prefixEnds = new List<int>();
        for (var index = 1; index < directoryPath.Length; index++)
        {
            if (directoryPath[index] == '/' && directoryPath[index - 1] != '/')
            {
                prefixEnds.Add(index);
            }
        }

        prefixEnds.Add(directoryPath.Length);
        foreach (var prefixEnd in prefixEnds)
        {
            var isFinalComponent = prefixEnd == prefixEnds[^1];
            var nativePrefix = NativeText.ToNullTerminated(directoryPath[..prefixEnd]);
            if (Libc.MakeDirectory(nativePrefix, DirectoryCreationMode) == 0)
            {
                continue;
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno != Errno.AlreadyExists)
            {
                throw IoFailureException.FromErrno(errno, directoryPath);
            }

            var prefixIsDirectory = NativeFileSystem.TryGetStatus(nativePrefix, true, out var prefixStatus) == 0 && prefixStatus.IsDirectory;
            if (!prefixIsDirectory)
            {
                throw IoFailureException.FromErrno(isFinalComponent ? Errno.AlreadyExists : Errno.NotADirectory, directoryPath);
            }
        }
    }

    public static void RemoveDirectoryTree(string directoryPath, Action throwIfInterrupted)
    {
        NativeFileSystem.ToNativePath(directoryPath);
        if (directoryPath.Length == 0)
        {
            throw IoFailureException.InvalidPath(directoryPath, "empty path refused");
        }

        var targetPath = PathText.TrimTrailingSlashes(directoryPath);
        var lastComponent = PathText.LastComponent(targetPath);
        if (targetPath == "/" || lastComponent is "." or "..")
        {
            throw IoFailureException.InvalidPath(directoryPath, "recursive removal refused for the root, `.` and `..`");
        }

        var nativeTarget = NativeText.ToNullTerminated(targetPath);
        var statusError = NativeFileSystem.TryGetStatus(nativeTarget, false, out var targetStatus);
        if (statusError != 0)
        {
            throw IoFailureException.FromErrno(statusError, directoryPath);
        }

        if (targetStatus.IsSymbolicLink)
        {
            ThrowIfFailed(Libc.Unlink(nativeTarget), directoryPath);
            return;
        }

        if (!targetStatus.IsDirectory)
        {
            throw IoFailureException.FromErrno(Errno.NotADirectory, directoryPath);
        }

        RefuseCurrentDirectoryAncestor(nativeTarget, directoryPath);
        RemoveTreeContents(nativeTarget, directoryPath, throwIfInterrupted);
    }

    private const uint FileCreationMode = 0x1B6;
    private const uint PrivateFileMode = 0x180;
    private const uint PermissionBitsMask = 0xFFF;

    // S15: a tree copy propagates only the 0o777 permission bits, like `filesystem.copy_file`;
    // setuid/setgid/sticky are never reproduced.
    private const uint CopiedPermissionBitsMask = 0x1FF;
    private const int TemporaryFileAttempts = 128;
    private const int MaximumLinkTargetBytes = 65536;
    private const uint DirectoryOwnerWriteMode = 0x1C0;

    public static void MakeSymbolicLink(string targetPath, string linkPath)
    {
        var nativeTarget = NativeFileSystem.ToNativePath(targetPath);
        var nativeLink = NativeFileSystem.ToNativePath(linkPath);
        ThrowIfFailed(Libc.MakeSymbolicLink(nativeTarget, nativeLink), linkPath);
    }

    /// <summary>
    /// Creates a second directory entry for the same inode. link(2) never follows a final symbolic link and refuses
    /// a directory; the error subject is the destination when it already exists and the source otherwise.
    /// </summary>
    public static void MakeHardLink(string existingPath, string newPath)
    {
        var nativeExisting = NativeFileSystem.ToNativePath(existingPath);
        var nativeNew = NativeFileSystem.ToNativePath(newPath);
        if (Libc.MakeHardLink(nativeExisting, nativeNew) == 0)
        {
            return;
        }

        var errno = Marshal.GetLastPInvokeError();
        if (errno == Errno.CrossDevice)
        {
            throw new IoFailureException(
                IoErrorKinds.CrossDevice,
                existingPath,
                "cannot create a hard link between file systems");
        }

        throw IoFailureException.FromErrno(errno, SubjectOfLinkFailure(errno, existingPath, newPath, nativeExisting));
    }

    public static string ReadLink(string linkPath)
    {
        var nativePath = NativeFileSystem.ToNativePath(linkPath);
        var buffer = new byte[Libc.PathMaximumLength + 1];
        while (true)
        {
            var length = Libc.ReadSymbolicLink(nativePath, buffer, (nuint)buffer.Length);
            if (length < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == Errno.InvalidArgument)
                {
                    throw new IoFailureException(IoErrorKinds.InvalidArgument, linkPath, "is not a symbolic link");
                }

                throw IoFailureException.FromErrno(errno, linkPath);
            }

            if (length >= buffer.Length)
            {
                if (buffer.Length >= MaximumLinkTargetBytes)
                {
                    throw IoFailureException.InvalidPath(linkPath, "the symbolic link target is too long");
                }

                Array.Resize(ref buffer, buffer.Length * 2);
                continue;
            }

            if (!NativeText.TryDecodeStrict(buffer.AsSpan(0, (int)length), out var target))
            {
                throw new IoFailureException(IoErrorKinds.InvalidEncoding, linkPath, "the symbolic link target is not valid UTF-8");
            }

            return target;
        }
    }

    public static void SetPermissions(string targetPath, long mode)
    {
        if (mode < 0 || mode > PermissionBitsMask)
        {
            throw new IoFailureException(IoErrorKinds.InvalidArgument, targetPath, "the mode must be between 0 and 0o7777");
        }

        ThrowIfFailed(Libc.ChangeMode(NativeFileSystem.ToNativePath(targetPath), (uint)mode), targetPath);
    }

    public static void SetOwner(string targetPath, long userId, long groupId, bool followLinks)
    {
        // uint.MaxValue is (uid_t)-1 / (gid_t)-1, the chown(2) "leave unchanged" sentinel, not a real id.
        const long MaximumIdentifier = uint.MaxValue - 1;
        if (userId < 0 || userId > MaximumIdentifier)
        {
            throw new IoFailureException(IoErrorKinds.InvalidArgument, targetPath, "the user id must be between 0 and 4294967294");
        }

        if (groupId < 0 || groupId > MaximumIdentifier)
        {
            throw new IoFailureException(IoErrorKinds.InvalidArgument, targetPath, "the group id must be between 0 and 4294967294");
        }

        // A trailing slash would make lchown(2) follow a final symbolic link; trim it only for the
        // no-follow variant so the link is the target. chown(2) follows by contract and keeps the
        // original path (a file + "/" stays ENOTDIR).
        var ownerPath = followLinks ? targetPath : PathText.TrimTrailingSlashes(targetPath);
        var nativePath = NativeFileSystem.ToNativePath(ownerPath);
        var result = followLinks
            ? Libc.ChangeOwner(nativePath, (uint)userId, (uint)groupId)
            : Libc.ChangeOwnerNoFollow(nativePath, (uint)userId, (uint)groupId);
        ThrowIfFailed(result, targetPath);
    }

    public static string GetRealPath(string targetPath)
    {
        var nativePath = NativeFileSystem.ToNativePath(targetPath);
        var error = NativeFileSystem.TryResolvePath(nativePath, out var resolved);
        if (error == Errno.NotADirectory)
        {
            throw IoFailureException.InvalidPath(targetPath, "a component of the path is not a directory");
        }

        if (error != 0)
        {
            throw IoFailureException.FromErrno(error, targetPath);
        }

        if (!NativeText.TryDecodeStrict(resolved, out var text))
        {
            throw new IoFailureException(IoErrorKinds.InvalidEncoding, targetPath, "the resolved path is not valid UTF-8");
        }

        return text;
    }

    public static void CopyDirectoryTree(string sourcePath, string destinationPath, Action throwIfInterrupted)
    {
        var nativeSource = NativeFileSystem.ToNativePath(sourcePath);
        var sourceError = NativeFileSystem.TryGetStatus(nativeSource, true, out var sourceStatus);
        if (sourceError != 0)
        {
            throw IoFailureException.FromErrno(sourceError, sourcePath);
        }

        if (!sourceStatus.IsDirectory)
        {
            throw IoFailureException.FromErrno(Errno.NotADirectory, sourcePath);
        }

        var nativeDestination = NativeFileSystem.ToNativePath(destinationPath);
        var destinationError = NativeFileSystem.TryGetStatus(nativeDestination, false, out _);
        if (destinationError == 0)
        {
            throw IoFailureException.FromErrno(Errno.AlreadyExists, destinationPath);
        }

        if (destinationError != Errno.NoSuchEntry)
        {
            throw IoFailureException.FromErrno(destinationError, destinationPath);
        }

        RefuseDestinationInsideSource(sourcePath, destinationPath);

        // The destination is created writable and only receives the source mode once its content is in
        // place, so a readable non-writable source can still be copied (the final mode is applied after).
        var sourceMode = sourceStatus.Mode & CopiedPermissionBitsMask;
        MakeDirectory(destinationPath);
        SetPermissions(destinationPath, sourceMode | DirectoryOwnerWriteMode);
        CopyDirectoryContents(sourcePath, destinationPath, throwIfInterrupted);
        SetPermissions(destinationPath, sourceMode);
    }

    /// <summary>
    /// Refuses <c>destinationPath</c> when it is the source itself or lies inside it: copying into the
    /// tree being walked would copy the copy, and the walk would only stop at ENAMETOOLONG.
    /// </summary>
    private static void RefuseDestinationInsideSource(string sourcePath, string destinationPath)
    {
        var sourceError = NativeFileSystem.TryResolvePath(NativeFileSystem.ToNativePath(sourcePath), out var resolvedSource);
        if (sourceError != 0)
        {
            return;
        }

        var destinationParent = LexicalPath.Parent(destinationPath);
        var parentError = NativeFileSystem.TryResolvePath(NativeFileSystem.ToNativePath(destinationParent), out var resolvedParent);
        if (parentError != 0)
        {
            return;
        }

        var resolvedDestination = PathText.Join(
            NativeText.DecodeLenient(resolvedParent),
            LexicalPath.BaseName(destinationPath));
        if (resolvedDestination == NativeText.DecodeLenient(resolvedSource)
            || resolvedDestination.StartsWith(NativeText.DecodeLenient(resolvedSource) + "/", StringComparison.Ordinal))
        {
            throw new IoFailureException(
                IoErrorKinds.InvalidArgument,
                destinationPath,
                "the destination lies inside the source directory");
        }
    }

    private static void CopyDirectoryContents(string sourcePath, string destinationPath, Action throwIfInterrupted)
    {
        foreach (var entry in FileSystemQueries.ListDirectoryEntries(sourcePath))
        {
            throwIfInterrupted();
            var name = PathText.LastComponent(entry.Path);
            var destinationEntry = PathText.Join(destinationPath, name);
            switch (entry.Kind)
            {
                case "symlink":
                    MakeSymbolicLink(ReadLink(entry.Path), destinationEntry);
                    break;
                case "directory":
                    var directoryStatus = NativeFileSystem.GetStatusOrThrow(entry.Path, false);
                    var directoryMode = directoryStatus.Mode & CopiedPermissionBitsMask;
                    MakeDirectory(destinationEntry);
                    SetPermissions(destinationEntry, directoryMode | DirectoryOwnerWriteMode);
                    CopyDirectoryContents(entry.Path, destinationEntry, throwIfInterrupted);
                    SetPermissions(destinationEntry, directoryMode);
                    break;
                case "file":
                    var fileStatus = NativeFileSystem.GetStatusOrThrow(entry.Path, false);
                    FileContentOperations.CopyFile(entry.Path, destinationEntry, overwrite: false, throwIfInterrupted);
                    SetPermissions(destinationEntry, fileStatus.Mode & CopiedPermissionBitsMask);
                    break;
                default:
                    throw new IoFailureException(IoErrorKinds.Other, entry.Path, "is not a regular file, directory or symbolic link");
            }
        }
    }

    public static void Touch(string targetPath)
    {
        // A trailing '/' is a path component, not part of the name: trim it before the no-follow call so a
        // final link is not followed through the slash (the same rule as filesystem.set_owner_no_follow).
        var trimmedPath = PathText.TrimTrailingSlashes(targetPath);
        var nativePath = NativeFileSystem.ToNativePath(trimmedPath);
        // The final component is not followed (touch -h): a link, including a dangling one, has a status and
        // gets its own times; the create branch runs only when the path itself is absent.
        var statusError = NativeFileSystem.TryGetStatus(nativePath, false, out var status);
        if (statusError == Errno.NoSuchEntry)
        {
            // O_NOFOLLOW closes the stat/open race: a symlink planted in between fails with ELOOP (invalid_path)
            // instead of creating its target. O_NONBLOCK makes a FIFO planted in between fail with ENXIO instead
            // of blocking the open; it has no effect on a regular file or on the creation itself.
            var fileDescriptor = Libc.Open(
                nativePath,
                Libc.OpenWriteOnly | Libc.OpenCreate | Libc.OpenCloseOnExec | Libc.OpenNoFollow | Libc.OpenNonBlock,
                FileCreationMode);
            if (fileDescriptor < 0)
            {
                throw IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), targetPath);
            }

            if (Libc.Close(fileDescriptor) != 0)
            {
                throw IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), targetPath);
            }

            return;
        }

        if (statusError != 0)
        {
            throw IoFailureException.FromErrno(statusError, targetPath);
        }

        try
        {
            var now = DateTime.UtcNow;
            if (status.IsDirectory)
            {
                Directory.SetLastAccessTimeUtc(trimmedPath, now);
                Directory.SetLastWriteTimeUtc(trimmedPath, now);
            }
            else
            {
                File.SetLastAccessTimeUtc(trimmedPath, now);
                File.SetLastWriteTimeUtc(trimmedPath, now);
            }
        }
        catch (FileNotFoundException)
        {
            throw IoFailureException.FromErrno(Errno.NoSuchEntry, targetPath);
        }
        catch (DirectoryNotFoundException)
        {
            throw IoFailureException.FromErrno(Errno.NoSuchEntry, targetPath);
        }
        catch (UnauthorizedAccessException)
        {
            throw IoFailureException.FromErrno(Errno.AccessDenied, targetPath);
        }
        catch (IOException)
        {
            throw new IoFailureException(IoErrorKinds.Other, targetPath, "could not update the timestamps");
        }
    }

    public static string MakeTemporaryFile(string prefix, string suffix)
    {
        foreach (var (value, label) in new[] { (prefix, "prefix"), (suffix, "suffix") })
        {
            if (value.Contains('/', StringComparison.Ordinal) || PathText.ContainsNul(value))
            {
                throw new IoFailureException(IoErrorKinds.InvalidArgument, value, $"the temporary-file {label} must not contain `/` or NUL");
            }
        }

        var directory = Path.GetTempPath();
        for (var attempt = 0; attempt < TemporaryFileAttempts; attempt++)
        {
            var name = prefix + Path.GetRandomFileName() + suffix;
            var candidate = PathText.Join(directory, name);
            var fileDescriptor = Libc.Open(
                NativeFileSystem.ToNativePath(candidate),
                Libc.OpenWriteOnly | Libc.OpenCreate | Libc.OpenExclusive | Libc.OpenCloseOnExec,
                PrivateFileMode);
            if (fileDescriptor < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == Errno.AlreadyExists)
                {
                    continue;
                }

                throw IoFailureException.FromErrno(errno, candidate);
            }

            // The creation mode is masked by the process umask; fchmod(2) guarantees the private 0600 mode.
            if (Libc.ChangeFileMode(fileDescriptor, PrivateFileMode) != 0)
            {
                var modeError = Marshal.GetLastPInvokeError();
                Libc.Close(fileDescriptor);
                Libc.Unlink(NativeFileSystem.ToNativePath(candidate));
                throw IoFailureException.FromErrno(modeError, candidate);
            }

            if (Libc.Close(fileDescriptor) != 0)
            {
                var closeError = Marshal.GetLastPInvokeError();
                Libc.Unlink(NativeFileSystem.ToNativePath(candidate));
                throw IoFailureException.FromErrno(closeError, candidate);
            }

            return candidate;
        }

        throw new IoFailureException(IoErrorKinds.AlreadyExists, prefix, "could not create a unique temporary file");
    }

    public static void MovePath(string sourcePath, string destinationPath, bool overwrite, Action throwIfInterrupted)
    {
        var nativeSource = NativeFileSystem.ToNativePath(sourcePath);
        var nativeDestination = NativeFileSystem.ToNativePath(destinationPath);
        var sourceKnown = NativeFileSystem.TryGetStatus(nativeSource, false, out var sourceStatus) == 0;
        if (sourceKnown
            && NativeFileSystem.TryGetStatus(nativeDestination, false, out var destinationStatus) == 0
            && sourceStatus.IsSameFileAs(destinationStatus))
        {
            throw IoFailureException.SameFile(sourcePath);
        }

        var errno = TryRename(nativeSource, nativeDestination, overwrite);
        if (errno == 0)
        {
            return;
        }

        if (errno == Errno.CrossDevice)
        {
            MoveAcrossDevices(sourcePath, destinationPath, nativeSource, nativeDestination, overwrite, throwIfInterrupted);
            return;
        }

        throw IoFailureException.FromErrno(errno, SubjectOfMoveFailure(errno, sourcePath, destinationPath, nativeSource));
    }

    public static string MakeTemporaryDirectory(string namePrefix)
    {
        if (namePrefix.Length > TemporaryPrefixMaximumLength || !namePrefix.All(IsTemporaryPrefixCharacter))
        {
            throw new IoFailureException(IoErrorKinds.InvalidArgument, namePrefix, "invalid prefix (expected: [A-Za-z0-9._-]{0,64})");
        }

        var temporaryRoot = Environment.GetEnvironmentVariable("TMPDIR");
        if (string.IsNullOrEmpty(temporaryRoot))
        {
            temporaryRoot = "/tmp";
        }

        for (var attempt = 0; attempt < TemporaryFileAttempts; attempt++)
        {
            var directoryPath = PathText.Join(temporaryRoot, namePrefix + CreateTemporarySuffix());
            var nativeDirectory = NativeText.ToNullTerminated(directoryPath);
            if (Libc.MakeDirectory(nativeDirectory, PrivateDirectoryMode) == 0)
            {
                // The creation mode is masked by the process umask, so a restrictive umask can leave the new
                // directory without any permission bit. chmod(2) does not require permission on the target,
                // only search on its parent, so the private 0700 mode can be applied by path even when
                // mkdir(2) created the directory as 0000. Removing it on failure leaves no partial result.
                if (Libc.ChangeMode(nativeDirectory, PrivateDirectoryMode) != 0)
                {
                    var modeError = Marshal.GetLastPInvokeError();
                    Libc.RemoveDirectory(nativeDirectory);
                    throw IoFailureException.FromErrno(modeError, directoryPath);
                }

                return directoryPath;
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno != Errno.AlreadyExists)
            {
                throw IoFailureException.FromErrno(errno, temporaryRoot);
            }
        }

        throw IoFailureException.FromErrno(Errno.AlreadyExists, temporaryRoot);
    }

    private static bool IsTemporaryPrefixCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-';

    /// <summary>Random suffix used by temporary names (directories and the atomic-write scratch file).</summary>
    internal static string CreateTemporarySuffix() =>
        string.Create(TemporarySuffixLength, 0, static (characters, _) =>
        {
            for (var position = 0; position < characters.Length; position++)
            {
                characters[position] = TemporarySuffixAlphabet[RandomNumberGenerator.GetInt32(TemporarySuffixAlphabet.Length)];
            }
        });

    private static void ThrowIfFailed(int result, string subject)
    {
        if (result != 0)
        {
            throw IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), subject);
        }
    }

    private static void RefuseCurrentDirectoryAncestor(byte[] nativeTarget, string directoryPath)
    {
        var resolveError = NativeFileSystem.TryResolvePath(nativeTarget, out var resolvedTarget);
        if (resolveError != 0)
        {
            throw IoFailureException.FromErrno(resolveError, directoryPath);
        }

        if (resolvedTarget.AsSpan().SequenceEqual("/"u8))
        {
            throw IoFailureException.InvalidPath(directoryPath, "recursive removal refused for the root");
        }

        var currentDirectoryError = NativeFileSystem.TryGetCurrentDirectory(out var currentDirectory);
        if (currentDirectoryError == Errno.NoSuchEntry)
        {
            return;
        }

        if (currentDirectoryError != 0)
        {
            throw IoFailureException.FromErrno(currentDirectoryError, directoryPath);
        }

        var isCurrentOrAncestor = currentDirectory.AsSpan().StartsWith(resolvedTarget)
            && (currentDirectory.Length == resolvedTarget.Length || currentDirectory[resolvedTarget.Length] == (byte)'/');
        if (isCurrentOrAncestor)
        {
            throw IoFailureException.InvalidPath(directoryPath, "recursive removal refused: current directory or ancestor of the current directory");
        }
    }

    private static void RemoveTreeContents(byte[] nativeTarget, string directoryPath, Action throwIfInterrupted)
    {
        // Race-free removal (A1): every step is relative to an open directory descriptor, so a directory swapped for a
        // symbolic link between the listing and the deletion is never followed (O_NOFOLLOW) and only the link is unlinked.
        var rootDescriptor = OpenDirectoryDescriptor(Libc.CurrentDirectoryFileDescriptor, nativeTarget, directoryPath);

        // The root directory itself is removed by its full path after its contents are gone (the descriptor protects the
        // traversal, but the final unlink is by path since we do not hold a descriptor on the parent).
        RemoveDirectoryContents(rootDescriptor, directoryPath, throwIfInterrupted);
        Libc.Close(rootDescriptor);
        ThrowIfFailed(Libc.UnlinkAt(Libc.CurrentDirectoryFileDescriptor, nativeTarget, Libc.RemoveDirectoryAtFlag), directoryPath);
    }

    /// <summary>Opens a directory relative to <paramref name="parentDescriptor"/> without following a final symbolic link.</summary>
    private static int OpenDirectoryDescriptor(int parentDescriptor, ReadOnlySpan<byte> path, string subject)
    {
        var descriptor = Libc.OpenAt(
            parentDescriptor,
            path,
            Libc.OpenReadOnly | Libc.OpenDirectoryFlag | Libc.OpenNoFollow | Libc.OpenCloseOnExec,
            0);
        if (descriptor < 0)
        {
            throw IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), subject);
        }

        return descriptor;
    }

    /// <summary>Empties the directory open on <paramref name="directoryDescriptor"/>; leaves that descriptor open.</summary>
    private static void RemoveDirectoryContents(int directoryDescriptor, string subject, Action throwIfInterrupted)
    {
        var entries = new List<NativeDirectoryEntry>();
        var readError = NativeFileSystem.TryReadDirectoryFromDescriptor(directoryDescriptor, entries);
        if (readError != 0)
        {
            throw IoFailureException.FromErrno(readError, subject);
        }

        foreach (var entry in entries)
        {
            throwIfInterrupted();
            var nativeName = NativeText.ToNullTerminated(entry.Name);
            var childDescriptor = Libc.OpenAt(
                directoryDescriptor,
                nativeName,
                Libc.OpenReadOnly | Libc.OpenDirectoryFlag | Libc.OpenNoFollow | Libc.OpenCloseOnExec,
                0);
            if (childDescriptor < 0)
            {
                var openError = Marshal.GetLastPInvokeError();
                // ENOTDIR/ELOOP: the entry is not a directory reachable without following a link (regular file, FIFO,
                // or symbolic link) -> unlink it. Any other error (EACCES...) is a real failure: report it instead of
                // unlinking, so the caller gets permission_denied rather than a misleading failure (review R4-1).
                if (openError is Errno.NotADirectory or Errno.InvalidArgument or Errno.TooManyLinks)
                {
                    ThrowIfFailed(Libc.UnlinkAt(directoryDescriptor, nativeName, 0), subject);
                    continue;
                }

                throw IoFailureException.FromErrno(openError, subject);
            }

            RemoveDirectoryContents(childDescriptor, subject, throwIfInterrupted);
            Libc.Close(childDescriptor);
            ThrowIfFailed(Libc.UnlinkAt(directoryDescriptor, nativeName, Libc.RemoveDirectoryAtFlag), subject);
        }
    }

    private static int TryRename(byte[] nativeSource, byte[] nativeDestination, bool overwrite)
    {
        if (overwrite)
        {
            return Libc.Rename(nativeSource, nativeDestination) == 0 ? 0 : Marshal.GetLastPInvokeError();
        }

        var noReplaceResult = Libc.RenameAt2(
            Libc.CurrentDirectoryFileDescriptor,
            nativeSource,
            Libc.CurrentDirectoryFileDescriptor,
            nativeDestination,
            Libc.RenameNoReplace);
        if (noReplaceResult == 0)
        {
            return 0;
        }

        var errno = Marshal.GetLastPInvokeError();
        if (errno != Errno.InvalidArgument)
        {
            return errno;
        }

        // EINVAL also means the filesystem lacks RENAME_NOREPLACE. Never fall back to
        // check-then-rename (a concurrent creation would be overwritten): link the source
        // to the destination (atomic no-overwrite) then unlink the source. A directory
        // cannot be hard-linked, so it fails closed.
        if (NativeFileSystem.TryGetStatus(nativeSource, false, out var sourceStatus) == 0 && sourceStatus.IsDirectory)
        {
            return Errno.NotSupported;
        }

        if (Libc.MakeHardLink(nativeSource, nativeDestination) != 0)
        {
            return Marshal.GetLastPInvokeError();
        }

        if (Libc.Unlink(nativeSource) == 0)
        {
            return 0;
        }

        var unlinkError = Marshal.GetLastPInvokeError();
        _ = Libc.Unlink(nativeDestination);
        return unlinkError;
    }

    private static void MoveAcrossDevices(
        string sourcePath,
        string destinationPath,
        byte[] nativeSource,
        byte[] nativeDestination,
        bool overwrite,
        Action throwIfInterrupted)
    {
        var isRegularFile = NativeFileSystem.TryGetStatus(nativeSource, false, out var sourceStatus) == 0 && sourceStatus.IsRegularFile;
        if (!isRegularFile)
        {
            throw IoFailureException.FromErrno(Errno.CrossDevice, sourcePath);
        }

        // rename(2) with overwrite replaces the destination entry itself, never following a symbolic link (A3).
        // The cross-filesystem fallback must therefore remove the existing destination first: otherwise the copy
        // would follow the link and write through it instead of replacing it.
        if (overwrite
            && NativeFileSystem.TryGetStatus(nativeDestination, false, out var destinationStatus) == 0
            && !destinationStatus.IsDirectory)
        {
            ThrowIfFailed(Libc.Unlink(nativeDestination), destinationPath);
        }

        FileContentOperations.CopyFile(sourcePath, destinationPath, overwrite, throwIfInterrupted);
        ThrowIfFailed(Libc.Unlink(nativeSource), sourcePath);
    }

    private static string SubjectOfMoveFailure(int errno, string sourcePath, string destinationPath, byte[] nativeSource) => errno switch
    {
        Errno.AlreadyExists or Errno.DirectoryNotEmpty or Errno.IsADirectory or Errno.NotADirectory => destinationPath,
        Errno.NoSuchEntry => NativeFileSystem.TryGetStatus(nativeSource, false, out _) == 0 ? destinationPath : sourcePath,
        _ => sourcePath,
    };

    private static string SubjectOfLinkFailure(int errno, string existingPath, string newPath, byte[] nativeExisting) => errno switch
    {
        Errno.AlreadyExists or Errno.IsADirectory or Errno.NotADirectory => newPath,
        Errno.NoSuchEntry => NativeFileSystem.TryGetStatus(nativeExisting, false, out _) == 0 ? newPath : existingPath,
        _ => existingPath,
    };
}
