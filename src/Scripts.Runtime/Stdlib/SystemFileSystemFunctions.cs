using Scripts.Runtime.Platform;

namespace Scripts.Runtime.Stdlib;

internal static class SystemFileSystemFunctions
{
    private const uint PermissionBitsMask = 0xFFF;

    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["filesystem.read_file"] = ReadFile;
        table["filesystem.read_file_no_follow"] = (context, arguments) =>
            SystemValues.GuardIo(() => SystemValues.Success(
                new StringValue(FileContentOperations.ReadFileNoFollow(SystemValues.PathAt(arguments, 0), context.ThrowIfInterrupted))));
        table["filesystem.for_each_line"] = ForEachLine;
        table["filesystem.write_file_overwrite"] = (_, arguments) => WriteFile(arguments, append: false);
        table["filesystem.append_file"] = (_, arguments) => WriteFile(arguments, append: true);
        table["filesystem.path_exists"] = PathExists;
        table["filesystem.get_path_status"] = GetPathStatus;
        table["filesystem.get_file_system_status"] = GetFileSystemStatus;
        table["filesystem.remove_file"] = (_, arguments) => Mutate(() => FileSystemMutations.RemoveFile(SystemValues.PathAt(arguments, 0)));
        table["filesystem.remove_empty_directory"] = (_, arguments) => Mutate(() => FileSystemMutations.RemoveEmptyDirectory(SystemValues.PathAt(arguments, 0)));
        table["filesystem.remove_directory_tree"] = RemoveDirectoryTree;
        table["filesystem.make_directory"] = (_, arguments) => Mutate(() => FileSystemMutations.MakeDirectory(SystemValues.PathAt(arguments, 0)));
        table["filesystem.make_directory_with_permissions"] = MakeDirectoryWithPermissions;
        table["filesystem.make_directory_tree"] = (_, arguments) => Mutate(() => FileSystemMutations.MakeDirectoryTree(SystemValues.PathAt(arguments, 0)));
        table["filesystem.copy_file"] = (context, arguments) => CopyFile(context, arguments, overwrite: false);
        table["filesystem.copy_file_overwrite"] = (context, arguments) => CopyFile(context, arguments, overwrite: true);
        table["filesystem.move_path"] = (context, arguments) => MovePath(context, arguments, overwrite: false);
        table["filesystem.move_path_overwrite"] = (context, arguments) => MovePath(context, arguments, overwrite: true);
        table["filesystem.get_current_directory"] = CurrentDirectory;
        table["filesystem.set_current_directory"] = (_, arguments) => Mutate(() => FileSystemMutations.ChangeDirectory(SystemValues.PathAt(arguments, 0)));
        table["filesystem.make_temporary_directory"] = MakeTemporaryDirectory;
        table["filesystem.copy_directory_tree"] = CopyDirectoryTree;
        table["filesystem.make_symbolic_link"] = MakeSymbolicLink;
        table["filesystem.make_hard_link"] = (_, arguments) => Mutate(() =>
            FileSystemMutations.MakeHardLink(SystemValues.PathAt(arguments, 0), SystemValues.PathAt(arguments, 1)));
        table["filesystem.read_link"] = ReadLink;
        table["filesystem.get_real_path"] = GetRealPath;
        table["filesystem.set_permissions"] = SetPermissions;
        table["filesystem.set_owner"] = (_, arguments) => SetOwner(arguments, followLinks: true);
        table["filesystem.set_owner_no_follow"] = (_, arguments) => SetOwner(arguments, followLinks: false);
        table["filesystem.make_temporary_file"] = MakeTemporaryFile;
        table["filesystem.write_file_exclusive"] = WriteFileExclusive;
        table["filesystem.write_file_exclusive_with_permissions"] = WriteFileExclusiveWithPermissions;
        table["filesystem.touch"] = Touch;
        table["filesystem.glob"] = Glob;
    }

    private static Value Mutate(Action operation) =>
        SystemValues.GuardIo(() =>
        {
            operation();
            return SystemValues.Success();
        });

    private static Value ReadFile(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardIo(() => SystemValues.Success(new StringValue(FileContentOperations.ReadFile(SystemValues.PathAt(arguments, 0)))));

    /// <summary>
    /// filesystem.for_each_line: streams the file (DESIGN §7.2); the visitor returns <c>ok(())</c> to continue or
    /// <c>error(e)</c> to stop, in which case the same error is returned after the file is closed.
    /// </summary>
    private static Value ForEachLine(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardIo(() =>
        {
            var filePath = SystemValues.PathAt(arguments, 0);
            var visit = (FunctionValue)arguments[1];
            Value? visitorError = null;
            LineStream.ForEachLine(
                filePath,
                line =>
                {
                    var result = (ResultValue)context.Invoke(visit, new StringValue(line));
                    if (result.IsOk)
                    {
                        return true;
                    }

                    visitorError = result.Payload;
                    return false;
                },
                context.ThrowIfInterrupted);

            return visitorError is null ? SystemValues.Success() : ResultValue.Error(visitorError);
        });

    private static Value WriteFile(Value[] arguments, bool append) =>
        Mutate(() => FileContentOperations.WriteFile(SystemValues.TextAt(arguments, 0), SystemValues.PathAt(arguments, 1), append));

    private static Value PathExists(RuntimeContext context, Value[] arguments) =>
        BooleanValue.From(FileSystemQueries.PathExists(SystemValues.PathAt(arguments, 0)));

    private static Value GetPathStatus(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardIo(() =>
        {
            var targetPath = SystemValues.PathAt(arguments, 0);
            var status = FileSystemQueries.GetStatus(targetPath);
            if (status.InodeNumber > (ulong)long.MaxValue)
            {
                throw new IoFailureException(
                    IoErrorKinds.Other,
                    targetPath,
                    "the inode number exceeds the signed 64-bit range");
            }

            return SystemValues.Success(new RecordValue(
                "path_status",
                new StringValue(status.KindName),
                IntegerValue.From(status.SizeBytes),
                IntegerValue.From(status.ModifiedSeconds),
                IntegerValue.From(status.Mode & PermissionBitsMask),
                IntegerValue.From(status.OwnerUserId),
                IntegerValue.From(status.OwnerGroupId),
                IntegerValue.From(status.DeviceMajor),
                IntegerValue.From(status.DeviceMinor),
                IntegerValue.From((long)status.InodeNumber),
                IntegerValue.From(status.LinkCount),
                IntegerValue.From(status.AccessedSeconds),
                IntegerValue.From(status.ChangedSeconds)));
        });

    private static Value GetFileSystemStatus(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardIo(() =>
        {
            var targetPath = SystemValues.PathAt(arguments, 0);
            var status = FileSystemQueries.GetFileSystemStatus(targetPath);
            return SystemValues.Success(new RecordValue(
                "file_system_status",
                SignedCounter(status.BlockSize, targetPath, "block size"),
                SignedCounter(status.FragmentSize, targetPath, "fragment size"),
                SignedCounter(status.Blocks, targetPath, "block count"),
                SignedCounter(status.FreeBlocks, targetPath, "free block count"),
                SignedCounter(status.AvailableBlocks, targetPath, "available block count"),
                SignedCounter(status.TotalInodes, targetPath, "inode count"),
                SignedCounter(status.FreeInodes, targetPath, "free inode count"),
                SignedCounter(status.AvailableInodes, targetPath, "available inode count"),
                IntegerValue.From(unchecked((long)status.FileSystemIdentifier)),
                SignedCounter(status.MaximumNameLength, targetPath, "maximum name length")));
        });

    private static IntegerValue SignedCounter(ulong value, string subject, string field)
    {
        if (value > (ulong)long.MaxValue)
        {
            throw new IoFailureException(
                IoErrorKinds.Other,
                subject,
                $"the {field} exceeds the signed 64-bit range");
        }

        return IntegerValue.From((long)value);
    }

    private static Value RemoveDirectoryTree(RuntimeContext context, Value[] arguments) =>
        Mutate(() => FileSystemMutations.RemoveDirectoryTree(SystemValues.PathAt(arguments, 0), context.ThrowIfInterrupted));

    private static Value CopyFile(RuntimeContext context, Value[] arguments, bool overwrite) =>
        Mutate(() => FileContentOperations.CopyFile(
            SystemValues.PathAt(arguments, 0),
            SystemValues.PathAt(arguments, 1),
            overwrite,
            context.ThrowIfInterrupted));

    private static Value MovePath(RuntimeContext context, Value[] arguments, bool overwrite) =>
        Mutate(() => FileSystemMutations.MovePath(
            SystemValues.PathAt(arguments, 0),
            SystemValues.PathAt(arguments, 1),
            overwrite,
            context.ThrowIfInterrupted));

    private static Value CurrentDirectory(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardIo(() => SystemValues.Success(new PathValue(FileSystemQueries.CurrentDirectory())));

    private static Value MakeTemporaryDirectory(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardIo(() => SystemValues.Success(new PathValue(FileSystemMutations.MakeTemporaryDirectory(SystemValues.TextAt(arguments, 0)))));

    private static Value CopyDirectoryTree(RuntimeContext context, Value[] arguments) =>
        Mutate(() => FileSystemMutations.CopyDirectoryTree(
            SystemValues.PathAt(arguments, 0),
            SystemValues.PathAt(arguments, 1),
            context.ThrowIfInterrupted));

    private static Value MakeSymbolicLink(RuntimeContext context, Value[] arguments) =>
        Mutate(() => FileSystemMutations.MakeSymbolicLink(
            SystemValues.PathAt(arguments, 0),
            SystemValues.PathAt(arguments, 1)));

    private static Value ReadLink(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardIo(() => SystemValues.Success(
            new PathValue(FileSystemMutations.ReadLink(SystemValues.PathAt(arguments, 0)))));

    private static Value GetRealPath(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardIo(() => SystemValues.Success(
            new PathValue(FileSystemMutations.GetRealPath(SystemValues.PathAt(arguments, 0)))));

    private static Value SetPermissions(RuntimeContext context, Value[] arguments) =>
        Mutate(() => FileSystemMutations.SetPermissions(
            SystemValues.PathAt(arguments, 0),
            SystemValues.IntegerAt(arguments, 1)));

    private static Value SetOwner(Value[] arguments, bool followLinks) =>
        Mutate(() => FileSystemMutations.SetOwner(
            SystemValues.PathAt(arguments, 0),
            SystemValues.IntegerAt(arguments, 1),
            SystemValues.IntegerAt(arguments, 2),
            followLinks));

    private static Value MakeTemporaryFile(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardIo(() => SystemValues.Success(
            new PathValue(FileSystemMutations.MakeTemporaryFile(
                SystemValues.TextAt(arguments, 0),
                SystemValues.TextAt(arguments, 1)))));

    private static Value WriteFileExclusive(RuntimeContext context, Value[] arguments) =>
        Mutate(() => FileContentOperations.WriteFileExclusive(
            SystemValues.TextAt(arguments, 0),
            SystemValues.PathAt(arguments, 1)));

    private static Value WriteFileExclusiveWithPermissions(RuntimeContext context, Value[] arguments) =>
        Mutate(() => FileContentOperations.WriteFileExclusiveWithPermissions(
            SystemValues.TextAt(arguments, 0),
            SystemValues.PathAt(arguments, 1),
            SystemValues.IntegerAt(arguments, 2)));

    private static Value MakeDirectoryWithPermissions(RuntimeContext context, Value[] arguments) =>
        Mutate(() => FileSystemMutations.MakeDirectoryWithPermissions(
            SystemValues.PathAt(arguments, 0),
            SystemValues.IntegerAt(arguments, 1)));

    private static Value Touch(RuntimeContext context, Value[] arguments) =>
        Mutate(() => FileSystemMutations.Touch(SystemValues.PathAt(arguments, 0)));

    private static Value Glob(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardIo(() =>
        {
            var patternText = SystemValues.TextAt(arguments, 1);
            if (!GlobPattern.TryCompile(patternText, out var pattern, out var reason))
            {
                throw new IoFailureException(IoErrorKinds.InvalidArgument, patternText, reason);
            }

            var paths = FileSystemQueries.Glob(
                SystemValues.PathAt(arguments, 0),
                pattern!,
                context.ThrowIfInterrupted);
            return SystemValues.Success(new ListValue([.. paths.Select(path => (Value)new PathValue(path))]));
        });
}
