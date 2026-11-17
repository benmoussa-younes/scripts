using Scripts.Runtime.Platform;

namespace Scripts.Runtime.Stdlib;

/// <summary>
/// Implementations of the <c>filesystem</c> intrinsic namespace (DESIGN §7.10a): the directory listing and
/// the walk yield <c>directory_entry</c> values, each carrying the entry <c>kind</c>, obtained from the
/// <c>d_type</c> the directory stream already returned (only <c>DT_UNKNOWN</c> falls back to a no-follow
/// stat). This lets a caller learn "file"/"directory"/… without a <c>filesystem.get_path_status</c> per entry.
/// <c>filesystem.walk_directory</c> additionally returns the paths it could not list or decode instead of
/// aborting the whole walk.
/// </summary>
internal static class SystemFileSystemEntriesFunctions
{
    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["filesystem.walk_directory"] = WalkDirectory;
        table["filesystem.list_directory"] = ListDirectory;
        table["filesystem.write_file_overwrite_atomically"] = WriteFileAtomically;
        table["filesystem.read_file_bounded"] = ReadFileBounded;
        table["filesystem.sha256_file"] = Sha256File;
    }

    private static Value Sha256File(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardIo(() => SystemValues.Success(new StringValue(
            FileContentOperations.Sha256File(SystemValues.PathAt(arguments, 0)))));

    private static Value ReadFileBounded(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardIo(() => SystemValues.Success(new StringValue(
            FileContentOperations.ReadFileBounded(SystemValues.PathAt(arguments, 0), SystemValues.IntegerAt(arguments, 1)))));

    private static Value WriteFileAtomically(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardIo(() =>
        {
            FileContentOperations.WriteFileAtomically(SystemValues.TextAt(arguments, 0), SystemValues.PathAt(arguments, 1));
            return SystemValues.Success();
        });

    private static Value WalkDirectory(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardIo(() =>
        {
            var report = FileSystemQueries.WalkDirectoryReport(
                SystemValues.PathAt(arguments, 0), context.ThrowIfInterrupted);
            return SystemValues.Success(new RecordValue(
                "walk_report",
                ToDirectoryEntryList(report.Entries),
                ToWalkSkipList(report.Skipped)));
        });

    private static Value ListDirectory(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardIo(() => SystemValues.Success(ToDirectoryEntryList(
            FileSystemQueries.ListDirectoryEntries(SystemValues.PathAt(arguments, 0)))));

    private static ListValue ToWalkSkipList(List<WalkSkipResult> skips)
    {
        var items = new List<Value>(skips.Count);
        foreach (var skip in skips)
        {
            items.Add(new RecordValue(
                "walk_skip",
                new StringValue(skip.Path),
                new StringValue(skip.Kind)));
        }

        return new ListValue(items);
    }

    private static ListValue ToDirectoryEntryList(List<DirectoryEntryResult> entries)
    {
        var items = new List<Value>(entries.Count);
        foreach (var entry in entries)
        {
            items.Add(new RecordValue(
                "directory_entry",
                new PathValue(entry.Path),
                new StringValue(entry.Kind)));
        }

        return new ListValue(items);
    }
}
