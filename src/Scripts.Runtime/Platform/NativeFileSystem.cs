using System.Runtime.InteropServices;

namespace Scripts.Runtime.Platform;

internal readonly record struct NativeFileStatus(
    uint Mode,
    long SizeBytes,
    long ModifiedSeconds,
    ulong InodeNumber,
    uint DeviceMajor,
    uint DeviceMinor,
    uint OwnerUserId,
    uint OwnerGroupId,
    uint LinkCount,
    long AccessedSeconds,
    long ChangedSeconds)
{
    public bool IsRegularFile => (Mode & Libc.FileTypeMask) == Libc.FileTypeRegular;

    public bool IsDirectory => (Mode & Libc.FileTypeMask) == Libc.FileTypeDirectory;

    public bool IsSymbolicLink => (Mode & Libc.FileTypeMask) == Libc.FileTypeSymbolicLink;

    public bool IsFifo => (Mode & Libc.FileTypeMask) == Libc.FileTypeFifo;

    public bool IsSocket => (Mode & Libc.FileTypeMask) == Libc.FileTypeSocket;

    public string KindName => IsRegularFile ? "file" : IsDirectory ? "directory" : IsSymbolicLink ? "symlink" : "other";

    public bool IsSameFileAs(NativeFileStatus other) =>
        InodeNumber == other.InodeNumber && DeviceMajor == other.DeviceMajor && DeviceMinor == other.DeviceMinor;
}

/// <summary>One directory entry as raw bytes; NativeType is the dirent d_type (0 = unknown).</summary>
internal readonly record struct NativeDirectoryEntry(byte[] Name, byte NativeType);

/// <summary>Thin errno-returning layer over libc (0 = success, otherwise the errno value).</summary>
internal static unsafe class NativeFileSystem
{
    private const int DirectoryEntryTypeOffset = 18;
    private const int DirectoryEntryNameOffset = 19;

    public static byte[] ToNativePath(string path) =>
        PathText.ContainsNul(path)
            ? throw IoFailureException.InvalidPath(path, "the path contains a NUL character")
            : NativeText.ToNullTerminated(path);

    public static int TryGetStatus(ReadOnlySpan<byte> nativePath, bool followLinks, out NativeFileStatus status)
    {
        var flags = followLinks ? 0 : Libc.SymbolicLinkNoFollow;
        if (Libc.StatX(Libc.CurrentDirectoryFileDescriptor, nativePath, flags, Libc.StatxBasicStatistics, out var buffer) != 0)
        {
            status = default;
            return Marshal.GetLastPInvokeError();
        }

        return TryConvert(buffer, out status);
    }

    /// <summary>
    /// statx(2) on an already-open descriptor with <c>AT_EMPTY_PATH</c> (the type check cannot race the path's
    /// replacement between a path stat and its open). A wrapper says <c>other</c> only after the descriptor is known.
    /// </summary>
    public static int TryGetStatusFromDescriptor(int fileDescriptor, out NativeFileStatus status)
    {
        ReadOnlySpan<byte> emptyPath = [0];
        if (Libc.StatX(
                fileDescriptor,
                emptyPath,
                Libc.AtEmptyPath | Libc.AtStatxSyncAsStat,
                Libc.StatxBasicStatistics,
                out var buffer) != 0)
        {
            status = default;
            return Marshal.GetLastPInvokeError();
        }

        return TryConvert(buffer, out status);
    }

    private static int TryConvert(StatxBuffer buffer, out NativeFileStatus status)
    {
        if (buffer.SizeBytes > (ulong)long.MaxValue)
        {
            status = default;
            return Errno.RangeExceeded;
        }

        status = new NativeFileStatus(
            buffer.Mode,
            (long)buffer.SizeBytes,
            buffer.ModificationTime.Seconds,
            buffer.InodeNumber,
            buffer.DeviceMajor,
            buffer.DeviceMinor,
            buffer.OwnerIdentifier,
            buffer.GroupIdentifier,
            buffer.LinkCount,
            buffer.AccessTime.Seconds,
            buffer.ChangeTime.Seconds);
        return 0;
    }

    /// <summary>
    /// True when the existing path designates a FIFO or a socket. When <paramref name="followLinks"/> is true the final
    /// link is followed (whole-file writes, which would block on such a target without a peer, refuse it: S16, error
    /// `other`); the atomic replace passes false, because <c>rename(2)</c> replaces the link itself. A path that does not
    /// exist or cannot be inspected is not blocking.
    /// </summary>
    public static bool IsFifoOrSocket(ReadOnlySpan<byte> nativePath, bool followLinks = true)
    {
        if (TryGetStatus(nativePath, followLinks, out var status) != 0)
        {
            return false;
        }

        return status.IsFifo || status.IsSocket;
    }

    public static NativeFileStatus GetStatusOrThrow(string path, bool followLinks)
    {
        var errno = TryGetStatus(ToNativePath(path), followLinks, out var status);
        return errno == 0 ? status : throw IoFailureException.FromErrno(errno, path);
    }

    public static int TryReadDirectory(ReadOnlySpan<byte> nativePath, List<NativeDirectoryEntry> entries)
    {
        var directoryStream = Libc.OpenDirectory(nativePath);
        if (directoryStream == 0)
        {
            return Marshal.GetLastPInvokeError();
        }

        try
        {
            DrainDirectory(directoryStream, entries);
            return 0;
        }
        finally
        {
            Libc.CloseDirectory(directoryStream);
        }
    }

    /// <summary>
    /// Reads the entries of an already-open directory descriptor without following a final symbolic link (A1). A duplicate
    /// of the descriptor is handed to fdopendir so the caller's descriptor (and its offset) stays untouched.
    /// </summary>
    public static int TryReadDirectoryFromDescriptor(int fileDescriptor, List<NativeDirectoryEntry> entries)
    {
        var duplicate = Libc.Duplicate(fileDescriptor);
        if (duplicate < 0)
        {
            return Marshal.GetLastPInvokeError();
        }

        var directoryStream = Libc.OpenDirectoryFromFileDescriptor(duplicate);
        if (directoryStream == 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            Libc.Close(duplicate);
            return errno;
        }

        try
        {
            DrainDirectory(directoryStream, entries);
            return 0;
        }
        finally
        {
            Libc.CloseDirectory(directoryStream);
        }
    }

    private static void DrainDirectory(nint directoryStream, List<NativeDirectoryEntry> entries)
    {
        while (true)
        {
            var entryAddress = Libc.ReadDirectory(directoryStream);
            if (entryAddress == 0)
            {
                return;
            }

            var entryBytes = (byte*)entryAddress;
            var name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(entryBytes + DirectoryEntryNameOffset);
            if (name.SequenceEqual("."u8) || name.SequenceEqual(".."u8))
            {
                continue;
            }

            entries.Add(new NativeDirectoryEntry(name.ToArray(), entryBytes[DirectoryEntryTypeOffset]));
        }
    }

    public static int TryGetCurrentDirectory(out byte[] path)
    {
        const int MaximumBufferSize = 1 << 20;
        for (var bufferSize = 512; ; bufferSize *= 2)
        {
            var buffer = new byte[bufferSize];
            if (Libc.GetCurrentDirectory(buffer, (nuint)bufferSize) != 0)
            {
                path = NativeText.UntilNul(buffer).ToArray();
                return 0;
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno != Errno.RangeExceeded || bufferSize >= MaximumBufferSize)
            {
                path = [];
                return errno;
            }
        }
    }

    public static int TryResolvePath(ReadOnlySpan<byte> nativePath, out byte[] resolvedPath)
    {
        var buffer = new byte[Libc.PathMaximumLength + 1];
        if (Libc.ResolvePath(nativePath, buffer) == 0)
        {
            resolvedPath = [];
            return Marshal.GetLastPInvokeError();
        }

        resolvedPath = NativeText.UntilNul(buffer).ToArray();
        return 0;
    }

    /// <summary>Directory path (without NUL) joined with a raw entry name, NUL-terminated.</summary>
    public static byte[] Combine(ReadOnlySpan<byte> directoryPath, ReadOnlySpan<byte> name)
    {
        var needsSeparator = directoryPath.Length > 0 && directoryPath[^1] != (byte)'/';
        var combined = new byte[directoryPath.Length + (needsSeparator ? 1 : 0) + name.Length + 1];
        directoryPath.CopyTo(combined);
        var offset = directoryPath.Length;
        if (needsSeparator)
        {
            combined[offset++] = (byte)'/';
        }

        name.CopyTo(combined.AsSpan(offset));
        return combined;
    }

    /// <summary>
    /// True when a directory entry designates a directory. The <c>d_type</c> reported by
    /// <c>getdents64</c> answers it with no metadata syscall; only <c>DT_UNKNOWN</c> (some
    /// filesystems) falls back to a no-follow stat. <paramref name="entryPath"/> is encoded
    /// only for that fallback, so a known <c>d_type</c> never pays for it.
    /// </summary>
    /// <param name="entryPath">The entry path as text, encoded only for the <c>DT_UNKNOWN</c> fallback.</param>
    /// <param name="nativeType">The raw <c>d_type</c> (0 = <c>DT_UNKNOWN</c>).</param>
    public static bool IsDirectoryEntry(string entryPath, byte nativeType)
    {
        if (nativeType != Libc.DirectoryEntryTypeUnknown)
        {
            return nativeType == Libc.DirectoryEntryTypeDirectory;
        }

        return TryGetStatus(NativeText.ToNullTerminated(entryPath), false, out var status) == 0 && status.IsDirectory;
    }

    /// <summary>
    /// The nature of a directory entry, from the raw <c>d_type</c>: <c>"file"</c>, <c>"directory"</c>,
    /// <c>"symlink"</c> or <c>"other"</c> (the closed set of <c>directory_entry.kind</c> / <c>path_status.kind</c>).
    /// The <c>d_type</c> from <c>getdents64</c> answers with no metadata syscall; only <c>DT_UNKNOWN</c>
    /// (some filesystems) falls back to a no-follow stat, exactly like <see cref="IsDirectoryEntry"/>.
    /// </summary>
    /// <param name="entryPath">The entry path as text, encoded only for the <c>DT_UNKNOWN</c> fallback.</param>
    /// <param name="nativeType">The raw <c>d_type</c> (0 = <c>DT_UNKNOWN</c>).</param>
    public static string EntryKindName(string entryPath, byte nativeType) => nativeType switch
    {
        Libc.DirectoryEntryTypeRegular => "file",
        Libc.DirectoryEntryTypeDirectory => "directory",
        Libc.DirectoryEntryTypeSymbolicLink => "symlink",
        Libc.DirectoryEntryTypeUnknown => UnknownEntryKind(entryPath),
        _ => "other",
    };

    private static string UnknownEntryKind(string entryPath) =>
        TryGetStatus(NativeText.ToNullTerminated(entryPath), false, out var status) == 0 ? status.KindName : "other";
}
