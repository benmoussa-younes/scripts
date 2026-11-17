using System.Runtime.InteropServices;

namespace Scripts.Runtime.Platform;

/// <summary>Read-only filesystem queries (SPEC-STDLIB §7.2). Failures are raised as IoFailureException.</summary>
internal static class FileSystemQueries
{
    public static bool PathExists(string path) =>
        !PathText.ContainsNul(path)
        && NativeFileSystem.TryGetStatus(NativeText.ToNullTerminated(path), true, out _) == 0;

    public static NativeFileStatus GetStatus(string path) =>
        NativeFileSystem.GetStatusOrThrow(PathText.TrimTrailingSlashes(path), false);

    public static StatVfsBuffer GetFileSystemStatus(string path)
    {
        if (PathText.ContainsNul(path))
        {
            throw IoFailureException.InvalidPath(path, "path contains NUL");
        }

        var nativePath = NativeFileSystem.ToNativePath(path);
        if (Libc.StatVfs(nativePath, out var buffer) != 0)
        {
            throw IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), path);
        }

        return buffer;
    }

    public static List<DirectoryEntryResult> ListDirectoryEntries(string directoryPath)
    {
        var entries = ReadSortedEntries(directoryPath, directoryPath);
        var results = new List<DirectoryEntryResult>(entries.Count);
        foreach (var entry in entries)
        {
            var entryPath = PathText.Join(directoryPath, entry.Name);
            results.Add(new DirectoryEntryResult(entryPath, NativeFileSystem.EntryKindName(entryPath, entry.NativeType)));
        }

        return results;
    }

    /// <summary>
    /// Expands one directory into its sorted children with their nature, using the same readdir/decode/sort
    /// and the same failures as <see cref="WalkDirectory"/>. Used by <c>parallel.walk_directory</c> to expand
    /// several directories at once while the deterministic pre-order fold stays sequential.
    /// </summary>
    public static List<DirectoryChild> ExpandDirectory(string directoryPath, string subject)
    {
        var entries = ReadSortedEntries(directoryPath, subject);
        var children = new List<DirectoryChild>(entries.Count);
        foreach (var entry in entries)
        {
            var entryPath = PathText.Join(directoryPath, entry.Name);
            children.Add(new DirectoryChild(entryPath, NativeFileSystem.IsDirectoryEntry(entryPath, entry.NativeType)));
        }

        return children;
    }

    /// <summary>
    /// Expands an explicit glob under <paramref name="rootPath"/> (SPEC-STDLIB §7.2): `**` crosses
    /// directories, special files are skipped, a symlink to a directory is never traversed, and the
    /// results follow the pre-order of <see cref="WalkDirectory"/> (sorted entries, prefix first).
    /// </summary>
    public static List<string> Glob(string rootPath, GlobPattern pattern, Action throwIfInterrupted)
    {
        var nativeRoot = NativeFileSystem.ToNativePath(rootPath);
        var rootError = NativeFileSystem.TryGetStatus(nativeRoot, true, out var rootStatus);
        if (rootError != 0)
        {
            throw IoFailureException.FromErrno(rootError, rootPath);
        }

        if (!rootStatus.IsDirectory)
        {
            throw IoFailureException.FromErrno(Errno.NotADirectory, rootPath);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<string>();
        Expand(rootPath, 0, false);
        matches.Sort(CompareWalkOrder);
        return matches;

        void Add(string path)
        {
            if (!seen.Add(path))
            {
                return;
            }

            matches.Add(path);
            if (matches.Count > SystemLimits.MaximumUnits)
            {
                throw SystemLimits.ListTooLong(matches.Count);
            }
        }

        void AddDescendants(string directoryPath)
        {
            throwIfInterrupted();
            foreach (var entry in ListDirectoryEntries(directoryPath))
            {
                AddIfVisible(entry);
                if (entry.Kind == "directory")
                {
                    AddDescendants(entry.Path);
                }
            }
        }

        void AddIfVisible(DirectoryEntryResult entry)
        {
            if (entry.Kind is "file" or "directory" or "symlink")
            {
                Add(entry.Path);
            }
        }

        void Expand(string directoryPath, int patternIndex, bool includeSelf)
        {
            throwIfInterrupted();
            var component = pattern.Components[patternIndex];
            if (component == "**")
            {
                if (IsTerminalDoubleStarRun(patternIndex))
                {
                    if (includeSelf)
                    {
                        Add(directoryPath);
                    }

                    AddDescendants(directoryPath);
                    return;
                }

                Expand(directoryPath, patternIndex + 1, false);
                foreach (var entry in ListDirectoryEntries(directoryPath))
                {
                    if (entry.Kind == "directory")
                    {
                        Expand(entry.Path, patternIndex, true);
                    }
                }

                return;
            }

            foreach (var entry in ListDirectoryEntries(directoryPath))
            {
                if (!GlobPattern.MatchesName(PathText.LastComponent(entry.Path), component))
                {
                    continue;
                }

                if (patternIndex == pattern.Components.Count - 1)
                {
                    AddIfVisible(entry);
                }
                else if (IsTerminalDoubleStarRun(patternIndex + 1))
                {
                    AddIfVisible(entry);
                    if (entry.Kind == "directory")
                    {
                        Expand(entry.Path, patternIndex + 1, false);
                    }
                }
                else if (entry.Kind == "directory")
                {
                    Expand(entry.Path, patternIndex + 1, true);
                }
            }
        }

        bool IsTerminalDoubleStarRun(int start)
        {
            for (var index = start; index < pattern.Components.Count; index++)
            {
                if (pattern.Components[index] != "**")
                {
                    return false;
                }
            }

            return start < pattern.Components.Count;
        }
    }

    /// <summary>Pre-order of <c>walk_directory</c>: compare one component at a time, a prefix first.</summary>
    internal static int CompareWalkOrder(string left, string right)
    {
        var leftComponents = left.Split('/');
        var rightComponents = right.Split('/');
        var shared = Math.Min(leftComponents.Length, rightComponents.Length);
        for (var index = 0; index < shared; index++)
        {
            var comparison = string.CompareOrdinal(leftComponents[index], rightComponents[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftComponents.Length.CompareTo(rightComponents.Length);
    }

    public static List<string> WalkDirectory(string rootPath, Action throwIfInterrupted)
    {
        var results = new List<string>();
        var pendingDirectories = new Stack<WalkFrame>();
        pendingDirectories.Push(new WalkFrame(rootPath, ReadSortedEntries(rootPath, rootPath)));
        while (pendingDirectories.Count > 0)
        {
            throwIfInterrupted();
            var frame = pendingDirectories.Peek();
            if (frame.NextIndex == frame.Entries.Count)
            {
                pendingDirectories.Pop();
                continue;
            }

            var entry = frame.Entries[frame.NextIndex++];
            var entryPath = PathText.Join(frame.DirectoryPath, entry.Name);
            results.Add(entryPath);
            if (results.Count > SystemLimits.MaximumUnits)
            {
                throw SystemLimits.ListTooLong(results.Count);
            }

            if (NativeFileSystem.IsDirectoryEntry(entryPath, entry.NativeType))
            {
                pendingDirectories.Push(new WalkFrame(entryPath, ReadSortedEntries(entryPath, rootPath)));
            }
        }

        return results;
    }

    /// <summary>
    /// Walks a directory tree in pre-order (SPEC-STDLIB §7.10a): a directory that cannot be listed keeps
    /// its entry and loses its children (one skip), and an undecodable child name is omitted and reported
    /// as a skip. A failure on the root itself is still thrown.
    /// </summary>
    public static WalkReportResult WalkDirectoryReport(string rootPath, Action throwIfInterrupted)
    {
        var entries = new List<DirectoryEntryResult>();
        var skipped = new List<WalkSkipResult>();
        var pendingDirectories = new Stack<WalkFrame>();
        pendingDirectories.Push(new WalkFrame(rootPath, ReadSortedEntriesPartial(rootPath, rootPath)));
        while (pendingDirectories.Count > 0)
        {
            throwIfInterrupted();
            var frame = pendingDirectories.Peek();
            if (frame.NextIndex == frame.Entries.Count)
            {
                pendingDirectories.Pop();
                continue;
            }

            var entry = frame.Entries[frame.NextIndex++];
            var entryPath = PathText.Join(frame.DirectoryPath, entry.Name);
            if (!entry.IsValidName)
            {
                skipped.Add(new WalkSkipResult(entryPath, IoErrorKinds.InvalidEncoding));
                EnsureReportWithinLimits(entries.Count, skipped.Count);
                continue;
            }

            entries.Add(new DirectoryEntryResult(entryPath, NativeFileSystem.EntryKindName(entryPath, entry.NativeType)));
            EnsureReportWithinLimits(entries.Count, skipped.Count);
            if (NativeFileSystem.IsDirectoryEntry(entryPath, entry.NativeType))
            {
                try
                {
                    pendingDirectories.Push(new WalkFrame(entryPath, ReadSortedEntriesPartial(entryPath, entryPath)));
                }
                catch (IoFailureException failure)
                {
                    var kind = failure.Kind == IoErrorKinds.PermissionDenied ? IoErrorKinds.PermissionDenied : IoErrorKinds.Other;
                    skipped.Add(new WalkSkipResult(entryPath, kind));
                    EnsureReportWithinLimits(entries.Count, skipped.Count);
                }
            }
        }

        return new WalkReportResult(entries, skipped);
    }

    private static void EnsureReportWithinLimits(int entryCount, int skipCount)
    {
        var total = entryCount + skipCount;
        if (total > SystemLimits.MaximumUnits)
        {
            throw SystemLimits.ListTooLong(total);
        }
    }

    public static string CurrentDirectory()
    {
        var errno = NativeFileSystem.TryGetCurrentDirectory(out var nativePath);
        if (errno != 0)
        {
            throw IoFailureException.FromErrno(errno, string.Empty);
        }

        return NativeText.TryDecodeStrict(nativePath, out var path)
            ? path
            : throw IoFailureException.InvalidPath(string.Empty, "the current directory is not valid UTF-8 text");
    }

    private static List<DecodedEntry> ReadSortedEntries(string directoryPath, string subject)
    {
        var entries = ReadSortedEntriesPartial(directoryPath, subject);
        foreach (var entry in entries)
        {
            if (!entry.IsValidName)
            {
                throw IoFailureException.InvalidPath(subject, "an entry name is not valid UTF-8 text");
            }
        }

        return entries;
    }

    /// <summary>
    /// Reads, decodes and sorts one directory's entries without rejecting an undecodable name: the entry
    /// keeps a lenient decoding and <see cref="DecodedEntry.IsValidName"/> false, so the caller decides
    /// between a fatal <c>invalid_path</c> (a strict listing) and a reported <c>invalid_encoding</c> skip.
    /// </summary>
    private static List<DecodedEntry> ReadSortedEntriesPartial(string directoryPath, string subject)
    {
        var rawEntries = new List<NativeDirectoryEntry>();
        var errno = NativeFileSystem.TryReadDirectory(NativeFileSystem.ToNativePath(directoryPath), rawEntries);
        if (errno != 0)
        {
            throw IoFailureException.FromErrno(errno, subject);
        }

        if (rawEntries.Count > SystemLimits.MaximumUnits)
        {
            throw SystemLimits.ListTooLong(rawEntries.Count);
        }

        var entries = new List<DecodedEntry>(rawEntries.Count);
        foreach (var rawEntry in rawEntries)
        {
            var isValid = NativeText.TryDecodeStrict(rawEntry.Name, out var name);
            if (!isValid)
            {
                name = NativeText.DecodeLenient(rawEntry.Name);
            }

            entries.Add(new DecodedEntry(name, rawEntry.NativeType, isValid));
        }

        entries.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        return entries;
    }

    private readonly record struct DecodedEntry(string Name, byte NativeType, bool IsValidName);

    private sealed class WalkFrame(string directoryPath, List<DecodedEntry> entries)
    {
        public string DirectoryPath { get; } = directoryPath;

        public List<DecodedEntry> Entries { get; } = entries;

        public int NextIndex { get; set; }
    }
}

/// <summary>One directory entry with its nature (SPEC-STDLIB §7.10a): a path and its <c>kind</c>.</summary>
internal readonly record struct DirectoryEntryResult(string Path, string Kind);

/// <summary>One child of an expanded directory: its path and whether it is a directory to descend into.</summary>
internal readonly record struct DirectoryChild(string Path, bool IsDirectory);

/// <summary>One path a partial walk could not list or decode (SPEC-STDLIB §7.10a): path and reason.</summary>
internal readonly record struct WalkSkipResult(string Path, string Kind);

/// <summary>The partial result of <see cref="FileSystemQueries.WalkDirectoryReport"/>.</summary>
internal readonly record struct WalkReportResult(List<DirectoryEntryResult> Entries, List<WalkSkipResult> Skipped);
