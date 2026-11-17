using Scripts.Runtime.Platform;
using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>Scratch directory on another filesystem (tmpfs under /dev/shm), or null when none is available.</summary>
public sealed class SystemOtherFilesystemWorkspace : IDisposable
{
    private SystemOtherFilesystemWorkspace(string root) => Root = root;

    public string Root { get; }

    public static SystemOtherFilesystemWorkspace? TryCreateDifferentFrom(string referenceDirectory)
    {
        if (!Directory.Exists("/dev/shm"))
        {
            return null;
        }

        var root = Path.Combine("/dev/shm", "scripts-system-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var workspace = new SystemOtherFilesystemWorkspace(root);
        var referenceError = NativeFileSystem.TryGetStatus(NativeText.ToNullTerminated(referenceDirectory), true, out var reference);
        var otherError = NativeFileSystem.TryGetStatus(NativeText.ToNullTerminated(root), true, out var other);
        if (referenceError != 0 || otherError != 0
            || (reference.DeviceMajor == other.DeviceMajor && reference.DeviceMinor == other.DeviceMinor))
        {
            workspace.Dispose();
            return null;
        }

        return workspace;
    }

    public void Dispose() => FileSystemMutations.RemoveDirectoryTree(Root, () => { });
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemCopyFileTests
{
    [Fact]
    public void Copies_content_and_the_exact_permission_bits()
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write("a.txt", "content é");
        File.SetUnixFileMode(source, (UnixFileMode)Convert.ToInt32("664", 8));
        var destination = workspace.Combine("b.txt");

        OkUnit(Call("filesystem.copy_file", PathOf(source), PathOf(destination)));

        Assert.Equal("content é", File.ReadAllText(destination));
        Assert.Equal((UnixFileMode)Convert.ToInt32("664", 8), SystemRawFiles.ModeOf(destination));
        Assert.Equal("content é", File.ReadAllText(source));
    }

    [Fact]
    public void Copies_binary_and_large_files_byte_for_byte_and_empty_files()
    {
        using var workspace = new SystemTestWorkspace();
        var bytes = new byte[5_000_001];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(workspace.Combine("big.bin"), bytes);
        workspace.Write("empty", string.Empty);

        OkUnit(Call("filesystem.copy_file", PathOf(workspace.Combine("big.bin")), PathOf(workspace.Combine("big.copy"))));
        OkUnit(Call("filesystem.copy_file", PathOf(workspace.Combine("empty")), PathOf(workspace.Combine("empty.copy"))));

        Assert.Equal(bytes, File.ReadAllBytes(workspace.Combine("big.copy")));
        Assert.Empty(File.ReadAllBytes(workspace.Combine("empty.copy")));
    }

    [Fact]
    public void Follows_a_source_symlink_and_creates_a_regular_destination()
    {
        using var workspace = new SystemTestWorkspace();
        var target = workspace.Write("target", "cible");
        var link = workspace.Combine("link");
        File.CreateSymbolicLink(link, target);
        var destination = workspace.Combine("copy");

        OkUnit(Call("filesystem.copy_file", PathOf(link), PathOf(destination)));

        Assert.Null(new FileInfo(destination).LinkTarget);
        Assert.Equal("cible", File.ReadAllText(destination));
    }

    [Fact]
    public void Existing_destination_even_a_dangling_link_is_already_exists_and_untouched()
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write("a", "new");
        var existing = workspace.Write("b", "old");
        var dangling = workspace.Combine("dangling");
        File.CreateSymbolicLink(dangling, workspace.Combine("nowhere"));

        AssertIoError(Call("filesystem.copy_file", PathOf(source), PathOf(existing)), "already_exists", existing);
        AssertIoError(Call("filesystem.copy_file", PathOf(source), PathOf(dangling)), "already_exists", dangling);

        Assert.Equal("old", File.ReadAllText(existing));
        Assert.False(File.Exists(workspace.Combine("nowhere")));
    }

    [Fact]
    public void Directories_non_regular_sources_and_same_file_are_rejected_with_the_right_subject()
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write("a", "x");
        var directory = workspace.Combine("dir");
        Directory.CreateDirectory(directory);
        var hardLink = workspace.Combine("hard");
        SystemRawFiles.CreateHardLink(hardLink, source);

        AssertIoError(Call("filesystem.copy_file", PathOf(directory), PathOf(workspace.Combine("x"))), "is_a_directory", directory);
        AssertIoError(Call("filesystem.copy_file", PathOf(source), PathOf(directory)), "is_a_directory", directory);
        AssertIoError(Call("filesystem.copy_file", PathOf("/dev/null"), PathOf(workspace.Combine("y"))), "other", "/dev/null");
        AssertIoError(Call("filesystem.copy_file", PathOf(source), PathOf(source)), "same_file", source);
        AssertIoError(Call("filesystem.copy_file", PathOf(source), PathOf(hardLink)), "same_file", source);
        Assert.Equal("x", File.ReadAllText(source));
    }

    [Fact]
    public void Evaluates_errors_in_the_documented_order()
    {
        using var workspace = new SystemTestWorkspace();
        var existing = workspace.Write("existing", "x");
        var directory = workspace.Combine("dir");
        Directory.CreateDirectory(directory);

        AssertIoError(Call("filesystem.copy_file", PathOf(workspace.Combine("missing")), PathOf(directory)), "not_found", workspace.Combine("missing"));
        AssertIoError(Call("filesystem.copy_file", PathOf(directory), PathOf(existing)), "is_a_directory", directory);
        AssertIoError(Call("filesystem.copy_file", PathOf(existing), PathOf(directory)), "is_a_directory", directory);
    }

    [Fact]
    public void Missing_destination_parent_and_nul_paths()
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write("a", "x");
        var destination = workspace.Combine("no", "b");

        AssertIoError(Call("filesystem.copy_file", PathOf(source), PathOf(destination)), "not_found", destination);
        AssertIoError(Call("filesystem.copy_file", PathOf("a\0"), PathOf(destination)), "invalid_path", "a\0");
        AssertIoError(Call("filesystem.copy_file", PathOf(source), PathOf("b\0")), "invalid_path", "b\0");
        AssertIoError(Call("filesystem.copy_file", PathOf(source + "/x"), PathOf(destination)), "not_a_directory", source + "/x");
    }

    [Fact]
    public void Unreadable_source_and_unwritable_destination_directory_are_permission_denied()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        var secret = workspace.Write("secret", "x");
        File.SetUnixFileMode(secret, UnixFileMode.None);
        var readable = workspace.Write("readable", "x");
        var locked = workspace.Combine("ro");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        AssertIoError(Call("filesystem.copy_file", PathOf(secret), PathOf(workspace.Combine("out"))), "permission_denied", secret);
        AssertIoError(Call("filesystem.copy_file", PathOf(readable), PathOf(locked + "/out")), "permission_denied", locked + "/out");
        Assert.False(File.Exists(workspace.Combine("out")));
    }

    [Theory]
    [InlineData("with space")]
    [InlineData("new\nline")]
    [InlineData("-leading")]
    [InlineData("日本 😀")]
    public void Awkward_names_are_never_reparsed(string name)
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write(name, "x");

        OkUnit(Call("filesystem.copy_file", PathOf(source), PathOf(workspace.Combine(name + ".copy"))));

        Assert.Equal("x", File.ReadAllText(workspace.Combine(name + ".copy")));
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemCopyFileOverwriteTests
{
    [Fact]
    public void Replaces_content_and_keeps_the_permissions_of_the_existing_destination()
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write("a", "new");
        var destination = workspace.Write("b", "much longer old content");
        File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(source, (UnixFileMode)Convert.ToInt32("755", 8));

        OkUnit(Call("filesystem.copy_file_overwrite", PathOf(source), PathOf(destination)));

        Assert.Equal("new", File.ReadAllText(destination));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, SystemRawFiles.ModeOf(destination));
    }

    [Fact]
    public void Creates_a_missing_destination_with_the_source_permission_bits()
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write("a", "x");
        File.SetUnixFileMode(source, (UnixFileMode)Convert.ToInt32("750", 8));
        var destination = workspace.Combine("b");

        OkUnit(Call("filesystem.copy_file_overwrite", PathOf(source), PathOf(destination)));

        Assert.Equal((UnixFileMode)Convert.ToInt32("750", 8), SystemRawFiles.ModeOf(destination));
    }

    [Fact]
    public void Writes_through_destination_symlinks_including_dangling_ones()
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write("a", "new");
        var target = workspace.Write("target", "old");
        var link = workspace.Combine("link");
        File.CreateSymbolicLink(link, target);
        var dangling = workspace.Combine("dangling");
        File.CreateSymbolicLink(dangling, workspace.Combine("created-through-link"));

        OkUnit(Call("filesystem.copy_file_overwrite", PathOf(source), PathOf(link)));
        OkUnit(Call("filesystem.copy_file_overwrite", PathOf(source), PathOf(dangling)));

        Assert.Equal("new", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal("new", File.ReadAllText(workspace.Combine("created-through-link")));
    }

    [Fact]
    public void Same_file_is_detected_before_truncation_so_the_source_survives()
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write("a", "precious");
        var hardLink = workspace.Combine("hard");
        SystemRawFiles.CreateHardLink(hardLink, source);
        var symbolicLink = workspace.Combine("soft");
        File.CreateSymbolicLink(symbolicLink, source);

        AssertIoError(Call("filesystem.copy_file_overwrite", PathOf(source), PathOf(source)), "same_file", source);
        AssertIoError(Call("filesystem.copy_file_overwrite", PathOf(source), PathOf(hardLink)), "same_file", source);
        AssertIoError(Call("filesystem.copy_file_overwrite", PathOf(source), PathOf(symbolicLink)), "same_file", source);

        Assert.Equal("precious", File.ReadAllText(source));
    }

    [Fact]
    public void Directory_destination_and_other_error_kinds()
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write("a", "x");
        var directory = workspace.Combine("dir");
        Directory.CreateDirectory(directory);

        AssertIoError(Call("filesystem.copy_file_overwrite", PathOf(source), PathOf(directory)), "is_a_directory", directory);
        AssertIoError(Call("filesystem.copy_file_overwrite", PathOf(directory), PathOf(workspace.Combine("x"))), "is_a_directory", directory);
        AssertIoError(Call("filesystem.copy_file_overwrite", PathOf(workspace.Combine("missing")), PathOf(source)), "not_found", workspace.Combine("missing"));
        AssertIoError(Call("filesystem.copy_file_overwrite", PathOf(source), PathOf(workspace.Combine("no", "b"))), "not_found", workspace.Combine("no", "b"));
    }

    [Fact]
    public void Write_failure_in_the_middle_of_the_copy_is_other_with_the_destination_as_subject_and_keeps_the_device()
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write("a", "content");

        AssertIoError(Call("filesystem.copy_file_overwrite", PathOf(source), PathOf("/dev/full")), "other", "/dev/full");
        Assert.True(File.Exists("/dev/full"));
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemMovePathTests
{
    [Fact]
    public void Renames_files_directories_and_symlinks_themselves()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("a.txt", "content");
        workspace.Write("dir/inner", "x");
        var target = workspace.Write("target", "t");
        File.CreateSymbolicLink(workspace.Combine("link"), target);

        OkUnit(Call("filesystem.move_path", PathOf(file), PathOf(workspace.Combine("b c.txt"))));
        OkUnit(Call("filesystem.move_path", PathOf(workspace.Combine("dir")), PathOf(workspace.Combine("-moved"))));
        OkUnit(Call("filesystem.move_path", PathOf(workspace.Combine("link")), PathOf(workspace.Combine("link2"))));

        Assert.False(File.Exists(file));
        Assert.Equal("content", File.ReadAllText(workspace.Combine("b c.txt")));
        Assert.Equal("x", File.ReadAllText(workspace.Combine("-moved", "inner")));
        Assert.NotNull(new FileInfo(workspace.Combine("link2")).LinkTarget);
        Assert.Equal("t", File.ReadAllText(target));
    }

    [Fact]
    public void Refuses_to_replace_existing_destinations_and_leaves_both_intact()
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write("a", "source");
        var existing = workspace.Write("b", "existing");
        workspace.Write("dir/x", "x");
        Directory.CreateDirectory(workspace.Combine("empty"));

        AssertIoError(Call("filesystem.move_path", PathOf(source), PathOf(existing)), "already_exists", existing);
        AssertIoError(Call("filesystem.move_path", PathOf(source), PathOf(workspace.Combine("dir"))), "already_exists", workspace.Combine("dir"));
        AssertIoError(Call("filesystem.move_path", PathOf(workspace.Combine("empty")), PathOf(workspace.Combine("dir"))), "already_exists", workspace.Combine("dir"));

        Assert.Equal("source", File.ReadAllText(source));
        Assert.Equal("existing", File.ReadAllText(existing));
    }

    [Fact]
    public void Same_file_is_detected_without_following_links()
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write("a", "x");
        var hardLink = workspace.Combine("hard");
        SystemRawFiles.CreateHardLink(hardLink, source);

        AssertIoError(Call("filesystem.move_path", PathOf(source), PathOf(source)), "same_file", source);
        AssertIoError(Call("filesystem.move_path", PathOf(source), PathOf(hardLink)), "same_file", source);
        AssertIoError(Call("filesystem.move_path_overwrite", PathOf(source), PathOf(hardLink)), "same_file", source);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(hardLink));
    }

    [Fact]
    public void Not_found_subject_is_the_source_when_missing_otherwise_the_destination()
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write("a", "x");

        AssertIoError(Call("filesystem.move_path", PathOf(workspace.Combine("missing")), PathOf(workspace.Combine("b"))), "not_found", workspace.Combine("missing"));
        AssertIoError(Call("filesystem.move_path", PathOf(source), PathOf(workspace.Combine("no", "b"))), "not_found", workspace.Combine("no", "b"));
        AssertIoError(Call("filesystem.move_path", PathOf("a\0"), PathOf(workspace.Combine("b"))), "invalid_path", "a\0");
        AssertIoError(Call("filesystem.move_path", PathOf(source), PathOf("b\0")), "invalid_path", "b\0");
    }

    [Fact]
    public void Destination_below_a_regular_file_is_not_a_directory_with_the_destination_subject()
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write("a", "x");
        var file = workspace.Write("f", "x");

        AssertIoError(Call("filesystem.move_path", PathOf(source), PathOf(file + "/b")), "not_a_directory", file + "/b");
    }

    [Fact]
    public void Moving_a_directory_into_itself_is_other_with_the_source_as_subject()
    {
        using var workspace = new SystemTestWorkspace();
        var directory = workspace.Combine("dir");
        Directory.CreateDirectory(directory);

        AssertIoError(Call("filesystem.move_path", PathOf(directory), PathOf(directory + "/inside")), "other", directory);
    }

    [Fact]
    public void Across_filesystems_files_are_copied_then_removed_and_directories_are_cross_device()
    {
        using var workspace = new SystemTestWorkspace();
        using var other = SystemOtherFilesystemWorkspace.TryCreateDifferentFrom(workspace.Root);
        if (other is null)
        {
            return;
        }

        var file = workspace.Write("a", "voyage");
        File.SetUnixFileMode(file, (UnixFileMode)Convert.ToInt32("640", 8));
        var directory = workspace.Combine("dir");
        Directory.CreateDirectory(directory);
        var existing = Path.Combine(other.Root, "existing");
        File.WriteAllText(existing, "old");

        OkUnit(Call("filesystem.move_path", PathOf(file), PathOf(Path.Combine(other.Root, "moved"))));
        AssertIoError(Call("filesystem.move_path", PathOf(directory), PathOf(Path.Combine(other.Root, "dir"))), "cross_device", directory);
        AssertIoError(Call("filesystem.move_path", PathOf(workspace.Write("b", "y")), PathOf(existing)), "already_exists", existing);

        Assert.False(File.Exists(file));
        Assert.Equal("voyage", File.ReadAllText(Path.Combine(other.Root, "moved")));
        Assert.Equal((UnixFileMode)Convert.ToInt32("640", 8), SystemRawFiles.ModeOf(Path.Combine(other.Root, "moved")));
        Assert.True(Directory.Exists(directory));
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemMovePathOverwriteTests
{
    [Fact]
    public void Replaces_files_and_empty_directories_atomically()
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write("a", "new");
        var destination = workspace.Write("b", "old");
        workspace.Write("srcdir/x", "x");
        Directory.CreateDirectory(workspace.Combine("emptydir"));

        OkUnit(Call("filesystem.move_path_overwrite", PathOf(source), PathOf(destination)));
        OkUnit(Call("filesystem.move_path_overwrite", PathOf(workspace.Combine("srcdir")), PathOf(workspace.Combine("emptydir"))));

        Assert.False(File.Exists(source));
        Assert.Equal("new", File.ReadAllText(destination));
        Assert.Equal("x", File.ReadAllText(workspace.Combine("emptydir", "x")));
    }

    [Fact]
    public void Mismatched_kinds_and_non_empty_directories_keep_their_errno_kinds()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("file", "x");
        workspace.Write("dir/x", "x");
        workspace.Write("other/y", "y");

        AssertIoError(Call("filesystem.move_path_overwrite", PathOf(workspace.Combine("dir")), PathOf(file)), "not_a_directory", file);
        AssertIoError(Call("filesystem.move_path_overwrite", PathOf(file), PathOf(workspace.Combine("dir"))), "is_a_directory", workspace.Combine("dir"));
        AssertIoError(Call("filesystem.move_path_overwrite", PathOf(workspace.Combine("other")), PathOf(workspace.Combine("dir"))), "directory_not_empty", workspace.Combine("dir"));
    }

    [Fact]
    public void Across_filesystems_a_file_overwrites_and_a_directory_is_cross_device()
    {
        using var workspace = new SystemTestWorkspace();
        using var other = SystemOtherFilesystemWorkspace.TryCreateDifferentFrom(workspace.Root);
        if (other is null)
        {
            return;
        }

        var file = workspace.Write("a", "new");
        var existing = Path.Combine(other.Root, "existing");
        File.WriteAllText(existing, "longer old content");
        Directory.CreateDirectory(workspace.Combine("dir"));

        OkUnit(Call("filesystem.move_path_overwrite", PathOf(file), PathOf(existing)));
        AssertIoError(Call("filesystem.move_path_overwrite", PathOf(workspace.Combine("dir")), PathOf(Path.Combine(other.Root, "dir"))), "cross_device");

        Assert.Equal("new", File.ReadAllText(existing));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Across_filesystems_a_symlink_destination_is_replaced_and_never_followed()
    {
        // A3: rename(2) replaces the destination link instead of following it; the cross-filesystem fallback must
        // do the same (unlink then copy to the destination itself). The source is created on the
        // main filesystem and the destination on another one (/dev/shm), which forces the
        // MoveAcrossDevices branch. The test is skipped if no second filesystem is available.
        using var workspace = new SystemTestWorkspace();
        using var other = SystemOtherFilesystemWorkspace.TryCreateDifferentFrom(workspace.Root);
        if (other is null)
        {
            return;
        }

        var source = workspace.Write("source", "SOURCE");
        var victim = Path.Combine(other.Root, "victim");
        File.WriteAllText(victim, "VICTIME");
        var link = Path.Combine(other.Root, "link");
        File.CreateSymbolicLink(link, victim);

        OkUnit(Call("filesystem.move_path_overwrite", PathOf(source), PathOf(link)));

        // The link was replaced by a regular file containing the source; the victim is not modified.
        Assert.Null(new FileInfo(link).LinkTarget);
        Assert.Equal("SOURCE", File.ReadAllText(link));
        Assert.Equal("VICTIME", File.ReadAllText(victim));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void Across_filesystems_a_dangling_symlink_destination_is_replaced_and_never_followed()
    {
        // Variant A3 with a dangling link: no target must be created through the link.
        using var workspace = new SystemTestWorkspace();
        using var other = SystemOtherFilesystemWorkspace.TryCreateDifferentFrom(workspace.Root);
        if (other is null)
        {
            return;
        }

        var source = workspace.Write("source", "SOURCE");
        var missingTarget = Path.Combine(other.Root, "nowhere");
        var link = Path.Combine(other.Root, "dangling");
        File.CreateSymbolicLink(link, missingTarget);

        OkUnit(Call("filesystem.move_path_overwrite", PathOf(source), PathOf(link)));

        Assert.Null(new FileInfo(link).LinkTarget);
        Assert.Equal("SOURCE", File.ReadAllText(link));
        Assert.False(File.Exists(missingTarget));
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemHardLinkTests
{
    [Fact]
    public void Creates_a_second_name_for_the_same_inode_and_shares_the_content()
    {
        using var workspace = new SystemTestWorkspace();
        var existing = workspace.Write("existing.txt", "contenu");
        var link = workspace.Combine("link.txt");

        OkUnit(Call("filesystem.make_hard_link", PathOf(existing), PathOf(link)));

        var existingStatus = Ok<RecordValue>(Call("filesystem.get_path_status", PathOf(existing)));
        var linkStatus = Ok<RecordValue>(Call("filesystem.get_path_status", PathOf(link)));
        Assert.Equal(existingStatus.GetInteger("inode"), linkStatus.GetInteger("inode"));
        Assert.Equal(2, linkStatus.GetInteger("link_count"));

        File.Delete(existing);
        Assert.Equal("contenu", File.ReadAllText(link));
    }

    [Fact]
    public void Refuses_an_existing_destination_without_touching_it()
    {
        using var workspace = new SystemTestWorkspace();
        var existing = workspace.Write("existing.txt", "source");
        var link = workspace.Write("link.txt", "victime");

        var error = ErrorRecord(Call("filesystem.make_hard_link", PathOf(existing), PathOf(link)), "error");

        Assert.Equal("already_exists", error.GetText("kind"));
        Assert.Equal("victime", File.ReadAllText(link));
        Assert.Equal("source", File.ReadAllText(existing));
    }

    [Fact]
    public void Refuses_a_directory_source()
    {
        using var workspace = new SystemTestWorkspace();
        var directory = workspace.Combine("directory");
        Directory.CreateDirectory(directory);
        var link = workspace.Combine("directory-link");

        var error = ErrorRecord(Call("filesystem.make_hard_link", PathOf(directory), PathOf(link)), "error");

        Assert.Contains(error.GetText("kind"), new[] { "permission_denied", "is_a_directory", "other" });
        Assert.False(File.Exists(link));
    }

    [Fact]
    public void Reports_cross_device_between_file_systems()
    {
        using var workspace = new SystemTestWorkspace();
        using var other = SystemOtherFilesystemWorkspace.TryCreateDifferentFrom(workspace.Root);
        if (other is null)
        {
            throw Xunit.Sdk.SkipException.ForSkip("no second filesystem is available");
        }

        var existing = workspace.Write("existing.txt", "contenu");
        var link = Path.Combine(other.Root, "link.txt");

        var error = ErrorRecord(Call("filesystem.make_hard_link", PathOf(existing), PathOf(link)), "error");

        Assert.Equal("cross_device", error.GetText("kind"));
        Assert.False(File.Exists(link));
    }
}
