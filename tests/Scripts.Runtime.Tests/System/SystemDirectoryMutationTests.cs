using System.Runtime.InteropServices;
using Scripts.Runtime.Platform;
using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

[Collection(SystemTestCollection.Name)]
public sealed class SystemRemoveTests
{
    [Fact]
    public void Remove_file_removes_files_and_only_the_link_of_a_symlink()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("f.txt", "x");
        var target = workspace.Write("target.txt", "keep");
        var link = workspace.Combine("link");
        File.CreateSymbolicLink(link, target);
        var dangling = workspace.Combine("dangling");
        File.CreateSymbolicLink(dangling, workspace.Combine("nowhere"));

        OkUnit(Call("filesystem.remove_file", PathOf(file)));
        OkUnit(Call("filesystem.remove_file", PathOf(link)));
        OkUnit(Call("filesystem.remove_file", PathOf(dangling)));

        Assert.False(File.Exists(file));
        Assert.Equal("keep", File.ReadAllText(target));
        Assert.Null(new FileInfo(link).LinkTarget);
        Assert.False(File.Exists(link));
    }

    [Fact]
    public void Remove_file_error_kinds()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("f", "x");

        AssertIoError(Call("filesystem.remove_file", PathOf(workspace.Combine("missing"))), "not_found", workspace.Combine("missing"));
        AssertIoError(Call("filesystem.remove_file", PathOf(workspace.Root)), "is_a_directory", workspace.Root);
        AssertIoError(Call("filesystem.remove_file", PathOf(file + "/x")), "not_a_directory", file + "/x");
        AssertIoError(Call("filesystem.remove_file", PathOf("a\0")), "invalid_path");
    }

    [Fact]
    public void Remove_file_in_a_read_only_directory_is_permission_denied()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("ro/f", "x");
        File.SetUnixFileMode(workspace.Combine("ro"), UnixFileMode.UserRead | UnixFileMode.UserExecute);

        AssertIoError(Call("filesystem.remove_file", PathOf(file)), "permission_denied", file);
    }

    [Fact]
    public void Remove_empty_directory_semantics()
    {
        using var workspace = new SystemTestWorkspace();
        var empty = workspace.Combine("empty");
        Directory.CreateDirectory(empty);
        var full = workspace.Combine("full");
        workspace.Write("full/x", "x");
        var file = workspace.Write("file", "x");

        OkUnit(Call("filesystem.remove_empty_directory", PathOf(empty)));

        Assert.False(Directory.Exists(empty));
        AssertIoError(Call("filesystem.remove_empty_directory", PathOf(full)), "directory_not_empty", full);
        AssertIoError(Call("filesystem.remove_empty_directory", PathOf(file)), "not_a_directory", file);
        AssertIoError(Call("filesystem.remove_empty_directory", PathOf(empty)), "not_found", empty);
        AssertIoError(Call("filesystem.remove_empty_directory", PathOf("a\0")), "invalid_path");
    }

    [Fact]
    public void Remove_empty_directory_of_dot_fails_with_other()
    {
        using var workspace = new SystemTestWorkspace();
        Directory.CreateDirectory(workspace.Combine("sub"));

        WithWorkingDirectory(workspace.Combine("sub"), () =>
            AssertIoError(Call("filesystem.remove_empty_directory", PathOf(".")), "other", "."));
    }

    [Fact]
    public void Remove_directory_tree_removes_nested_content_including_awkward_names_and_non_utf8_names()
    {
        using var workspace = new SystemTestWorkspace();
        var tree = workspace.Combine("tree");
        workspace.Write("tree/a b/c\nd/-rf", "x");
        workspace.Write("tree/日本/😀", "x");
        workspace.Write("tree/.hidden", "x");
        Directory.CreateDirectory(workspace.Combine("tree", "empty"));
        SystemRawFiles.CreateFileWithRawName(workspace.Combine("tree"), [0xFF, 0xFE]);
        workspace.Write("outside.txt", "keep");

        OkUnit(Call("filesystem.remove_directory_tree", PathOf(tree)));

        Assert.False(Directory.Exists(tree));
        Assert.True(File.Exists(workspace.Combine("outside.txt")));
    }

    [Fact]
    public void Remove_directory_tree_never_follows_symlinks()
    {
        using var workspace = new SystemTestWorkspace();
        workspace.Write("external/keep.txt", "keep");
        workspace.Write("tree/inner/file", "x");
        File.CreateSymbolicLink(workspace.Combine("tree", "inner", "to-external"), workspace.Combine("external"));
        File.CreateSymbolicLink(workspace.Combine("tree", "to-file"), workspace.Combine("external", "keep.txt"));

        OkUnit(Call("filesystem.remove_directory_tree", PathOf(workspace.Combine("tree"))));

        Assert.False(Directory.Exists(workspace.Combine("tree")));
        Assert.Equal("keep", File.ReadAllText(workspace.Combine("external", "keep.txt")));
    }

    [Fact]
    public void Remove_directory_tree_on_a_symlink_removes_only_the_link()
    {
        using var workspace = new SystemTestWorkspace();
        workspace.Write("real/keep.txt", "keep");
        var link = workspace.Combine("link");
        File.CreateSymbolicLink(link, workspace.Combine("real"));

        OkUnit(Call("filesystem.remove_directory_tree", PathOf(link)));

        Assert.Null(new FileInfo(link).LinkTarget);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(workspace.Combine("real", "keep.txt")));
    }

    [Fact]
    public void Remove_directory_tree_with_a_trailing_slash_on_a_symlink_still_removes_only_the_link()
    {
        using var workspace = new SystemTestWorkspace();
        workspace.Write("real/keep.txt", "keep");
        var link = workspace.Combine("link");
        File.CreateSymbolicLink(link, workspace.Combine("real"));

        OkUnit(Call("filesystem.remove_directory_tree", PathOf(link + "/")));

        Assert.True(File.Exists(workspace.Combine("real", "keep.txt")));
        Assert.False(Directory.Exists(link));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("/.")]
    [InlineData("/..")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("sub/.")]
    [InlineData("sub/..")]
    [InlineData("sub/../")]
    public void Remove_directory_tree_refuses_dangerous_paths_before_deleting_anything(string dangerousPath)
    {
        using var workspace = new SystemTestWorkspace();
        workspace.Write("sub/marker", "x");

        WithWorkingDirectory(workspace.Root, () =>
        {
            AssertIoError(Call("filesystem.remove_directory_tree", PathOf(dangerousPath)), "invalid_path", dangerousPath);

            Assert.True(File.Exists(workspace.Combine("sub", "marker")));
        });
    }

    [Fact]
    public void Remove_directory_tree_refuses_the_current_directory_and_its_ancestors()
    {
        using var workspace = new SystemTestWorkspace();
        workspace.Write("a/b/marker", "x");
        var current = workspace.Combine("a", "b");
        var relativeAlias = workspace.Combine("a", "b", "..", "b");

        WithWorkingDirectory(current, () =>
        {
            foreach (var target in new[] { current, workspace.Combine("a"), workspace.Root, "../b", "../../a", relativeAlias })
            {
                AssertIoError(Call("filesystem.remove_directory_tree", PathOf(target)), "invalid_path", target);
            }

            Assert.True(File.Exists(workspace.Combine("a", "b", "marker")));
        });
    }

    [Fact]
    public void Remove_directory_tree_compares_resolved_paths_so_symlinked_ancestors_are_refused()
    {
        using var workspace = new SystemTestWorkspace();
        workspace.Write("real/inner/marker", "x");
        File.CreateSymbolicLink(workspace.Combine("alias"), workspace.Combine("real"));

        WithWorkingDirectory(workspace.Combine("real", "inner"), () =>
        {
            AssertIoError(Call("filesystem.remove_directory_tree", PathOf(workspace.Combine("alias", "inner"))), "invalid_path");
            Assert.True(File.Exists(workspace.Combine("real", "inner", "marker")));

            OkUnit(Call("filesystem.remove_directory_tree", PathOf(workspace.Combine("alias"))));

            Assert.False(Directory.Exists(workspace.Combine("alias")));
            Assert.True(File.Exists(workspace.Combine("real", "inner", "marker")));
        });
    }

    [Fact]
    public void Remove_directory_tree_can_remove_a_sibling_of_the_current_directory()
    {
        using var workspace = new SystemTestWorkspace();
        workspace.Write("keep/f", "x");
        workspace.Write("gone/f", "x");

        WithWorkingDirectory(workspace.Combine("keep"), () =>
        {
            OkUnit(Call("filesystem.remove_directory_tree", PathOf("../gone")));

            Assert.False(Directory.Exists(workspace.Combine("gone")));
        });
    }

    [Fact]
    public void Remove_directory_tree_error_kinds()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("f", "x");

        AssertIoError(Call("filesystem.remove_directory_tree", PathOf(file)), "not_a_directory", file);
        AssertIoError(Call("filesystem.remove_directory_tree", PathOf(workspace.Combine("missing"))), "not_found", workspace.Combine("missing"));
        AssertIoError(Call("filesystem.remove_directory_tree", PathOf("a\0b")), "invalid_path");
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Remove_directory_tree_stops_at_the_first_error_leaving_a_partial_deletion()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        workspace.Write("tree/a/f", "x");
        workspace.Write("tree/b/f", "x");
        File.SetUnixFileMode(workspace.Combine("tree", "b"), UnixFileMode.UserRead | UnixFileMode.UserExecute);

        AssertIoError(Call("filesystem.remove_directory_tree", PathOf(workspace.Combine("tree"))), "permission_denied", workspace.Combine("tree"));

        Assert.True(Directory.Exists(workspace.Combine("tree")));
        Assert.True(File.Exists(workspace.Combine("tree", "b", "f")));
    }

    [Fact]
    public void Remove_directory_tree_on_an_unreadable_directory_reports_permission_denied()
    {
        // Regression (black-box review): a fully unreadable subdirectory used to be misreported as is_a_directory,
        // because the failed openat(O_DIRECTORY) was taken for "not a directory" and unlinkat then failed with EISDIR.
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        workspace.Write("tree/inner/f", "x");
        File.SetUnixFileMode(workspace.Combine("tree", "inner"), UnixFileMode.None);

        try
        {
            AssertIoError(Call("filesystem.remove_directory_tree", PathOf(workspace.Combine("tree"))), "permission_denied", workspace.Combine("tree"));
        }
        finally
        {
            File.SetUnixFileMode(
                workspace.Combine("tree", "inner"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemMakeDirectoryTests
{
    [Fact]
    public void Make_directory_creates_with_mode_0777_minus_umask()
    {
        using var workspace = new SystemTestWorkspace();
        var directory = workspace.Combine("new dir");

        OkUnit(Call("filesystem.make_directory", PathOf(directory)));

        var mode = SystemRawFiles.ModeOf(directory);
        Assert.True(mode.HasFlag(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
        Assert.False(mode.HasFlag(UnixFileMode.SetUser));
    }

    [Fact]
    public void Make_directory_error_kinds()
    {
        using var workspace = new SystemTestWorkspace();
        var directory = workspace.Combine("existing");
        Directory.CreateDirectory(directory);
        var file = workspace.Write("file", "x");

        AssertIoError(Call("filesystem.make_directory", PathOf(directory)), "already_exists", directory);
        AssertIoError(Call("filesystem.make_directory", PathOf(file)), "already_exists", file);
        AssertIoError(Call("filesystem.make_directory", PathOf(workspace.Combine("no", "child"))), "not_found", workspace.Combine("no", "child"));
        AssertIoError(Call("filesystem.make_directory", PathOf(file + "/child")), "not_a_directory", file + "/child");
        AssertIoError(Call("filesystem.make_directory", PathOf(string.Empty)), "not_found", string.Empty);
        AssertIoError(Call("filesystem.make_directory", PathOf("a\0")), "invalid_path");
    }

    [Fact]
    public void Make_directory_in_a_read_only_directory_is_permission_denied()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        var locked = workspace.Combine("ro");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        AssertIoError(Call("filesystem.make_directory", PathOf(locked + "/child")), "permission_denied", locked + "/child");
    }

    [Fact]
    public void Make_directory_tree_creates_parents_and_is_idempotent()
    {
        using var workspace = new SystemTestWorkspace();
        var deep = workspace.Combine("a b", "c\nd", "-e");

        OkUnit(Call("filesystem.make_directory_tree", PathOf(deep)));
        OkUnit(Call("filesystem.make_directory_tree", PathOf(deep)));
        OkUnit(Call("filesystem.make_directory_tree", PathOf(deep + "/")));
        OkUnit(Call("filesystem.make_directory_tree", PathOf(workspace.Root)));
        OkUnit(Call("filesystem.make_directory_tree", PathOf("/")));

        Assert.True(Directory.Exists(deep));
    }

    [Fact]
    public void Make_directory_tree_handles_relative_paths_and_dot_dot_components()
    {
        using var workspace = new SystemTestWorkspace();

        WithWorkingDirectory(workspace.Root, () =>
        {
            OkUnit(Call("filesystem.make_directory_tree", PathOf("rel/x/../y/./z")));

            Assert.True(Directory.Exists(workspace.Combine("rel", "x")));
            Assert.True(Directory.Exists(workspace.Combine("rel", "y", "z")));
        });
    }

    [Fact]
    public void Make_directory_tree_error_kinds()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("file", "x");
        var dangling = workspace.Combine("dangling");
        File.CreateSymbolicLink(dangling, workspace.Combine("nowhere"));

        AssertIoError(Call("filesystem.make_directory_tree", PathOf(file)), "already_exists", file);
        AssertIoError(Call("filesystem.make_directory_tree", PathOf(file + "/sub/deeper")), "not_a_directory", file + "/sub/deeper");
        AssertIoError(Call("filesystem.make_directory_tree", PathOf(dangling)), "already_exists", dangling);
        AssertIoError(Call("filesystem.make_directory_tree", PathOf(string.Empty)), "not_found", string.Empty);
        AssertIoError(Call("filesystem.make_directory_tree", PathOf("a\0")), "invalid_path");
    }

    [Fact]
    public void Make_directory_tree_is_not_atomic_and_reports_permission_denied()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        var locked = workspace.Combine("ro");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var target = workspace.Combine("created", "x");

        OkUnit(Call("filesystem.make_directory_tree", PathOf(target)));
        AssertIoError(Call("filesystem.make_directory_tree", PathOf(locked + "/a/b")), "permission_denied", locked + "/a/b");
    }
}

[Collection(SystemTestCollection.Name)]
public sealed partial class SystemMakeTemporaryDirectoryTests
{
    [Fact]
    public void Creates_a_private_directory_named_prefix_plus_eight_characters()
    {
        using var workspace = new SystemTestWorkspace();

        WithEnvironment("TMPDIR", workspace.Root, () =>
        {
            var created = Ok<PathValue>(Call("filesystem.make_temporary_directory", Text("scripts-spec-"))).Text;

            Assert.StartsWith(workspace.Root + "/scripts-spec-", created, StringComparison.Ordinal);
            var suffix = created[(workspace.Root.Length + 1 + "scripts-spec-".Length)..];
            Assert.Matches("^[a-z0-9]{8}$", suffix);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, SystemRawFiles.ModeOf(created));
        });
    }

    [Theory]
    [InlineData(0x40)] // 0100: strips the owner execute bit
    [InlineData(0x1FF)] // 0777: strips every permission bit
    public void Creates_a_private_0700_directory_regardless_of_the_process_umask(int mask)
    {
        using var workspace = new SystemTestWorkspace();

        WithEnvironment("TMPDIR", workspace.Root, () =>
        {
            var created = WithProcessUmask((uint)mask, () =>
                Ok<PathValue>(Call("filesystem.make_temporary_directory", Text("scripts-umask-"))).Text);

            // get_path_status only needs search permission on the parent, not on the directory itself,
            // so the mode is observable under 0777 without creating anything inside the new directory.
            var status = Ok<RecordValue>(Call("filesystem.get_path_status", PathOf(created)));
            Assert.Equal((long)Convert.ToInt32("700", 8), status.GetInteger("permissions"));

            // Create and read a file only under a normal umask: 0777 would mask the file mode too, and
            // the assertion is about the directory (make_temporary_file is out of scope for this card).
            WithProcessUmask(0, () =>
            {
                var file = Path.Combine(created, "inside.txt");
                OkUnit(Call("filesystem.write_file_overwrite", Text("inside"), PathOf(file)));
                Assert.Equal("inside", Ok<StringValue>(Call("filesystem.read_file", PathOf(file))).Text);
                return true;
            });
        });
    }

    private static T WithProcessUmask<T>(uint mask, Func<T> body)
    {
        var previous = SetProcessUmask(mask);
        try
        {
            return body();
        }
        finally
        {
            SetProcessUmask(previous);
        }
    }

    // Test-only libc binding: `umask` is not in the production P/Invoke list (DESIGN §11), and a test must not grow it.
    [LibraryImport("libc", EntryPoint = "umask")]
    private static partial uint SetProcessUmask(uint mask);

    [Fact]
    public void Accepts_empty_and_maximal_prefixes_and_creates_distinct_directories()
    {
        using var workspace = new SystemTestWorkspace();

        WithEnvironment("TMPDIR", workspace.Root + "/", () =>
        {
            var names = new HashSet<string>();
            for (var attempt = 0; attempt < 50; attempt++)
            {
                names.Add(Ok<PathValue>(Call("filesystem.make_temporary_directory", Text("a.B_c-"))).Text);
            }

            Assert.Equal(50, names.Count);
            Assert.Equal(8, Path.GetFileName(Ok<PathValue>(Call("filesystem.make_temporary_directory", Text(string.Empty))).Text).Length);
            Assert.True(Directory.Exists(Ok<PathValue>(Call("filesystem.make_temporary_directory", Text(new string('p', 64)))).Text));
        });
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("é")]
    [InlineData("with space")]
    [InlineData("nul\0")]
    [InlineData("..%")]
    public void Rejects_invalid_prefixes_without_touching_the_filesystem(string prefix)
    {
        using var workspace = new SystemTestWorkspace();

        WithEnvironment("TMPDIR", workspace.Root, () =>
        {
            AssertIoError(Call("filesystem.make_temporary_directory", Text(prefix)), "invalid_argument", prefix);

            Assert.Empty(Directory.GetFileSystemEntries(workspace.Root));
        });
    }

    [Fact]
    public void Rejects_a_prefix_of_65_characters()
    {
        AssertIoError(Call("filesystem.make_temporary_directory", Text(new string('a', 65))), "invalid_argument");
    }

    [Fact]
    public void Uses_slash_tmp_when_tmpdir_is_empty_or_absent()
    {
        foreach (var value in new string?[] { string.Empty, null })
        {
            WithEnvironment("TMPDIR", value, () =>
            {
                var created = Ok<PathValue>(Call("filesystem.make_temporary_directory", Text("scripts-default-"))).Text;
                try
                {
                    Assert.StartsWith("/tmp/scripts-default-", created, StringComparison.Ordinal);
                }
                finally
                {
                    Directory.Delete(created);
                }
            });
        }
    }

    [Fact]
    public void Reports_the_parent_directory_as_subject_on_failure()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("notdir", "x");

        WithEnvironment("TMPDIR", workspace.Combine("missing"), () =>
            AssertIoError(Call("filesystem.make_temporary_directory", Text("p-")), "not_found", workspace.Combine("missing")));
        WithEnvironment("TMPDIR", file, () =>
            AssertIoError(Call("filesystem.make_temporary_directory", Text("p-")), "not_a_directory", file));
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemWorkingDirectoryTests
{
    [Fact]
    public void Change_directory_then_current_directory_round_trips_and_resolves_relative_paths()
    {
        using var workspace = new SystemTestWorkspace();
        Directory.CreateDirectory(workspace.Combine("fixe"));
        var physicalRoot = Path.GetFullPath(workspace.Root);

        var previous = Directory.GetCurrentDirectory();
        try
        {
            using var context = NewContext();
            OkUnit(Call(context, "filesystem.set_current_directory", PathOf(workspace.Combine("fixe"))));
            Assert.Equal(physicalRoot + "/fixe", Ok<PathValue>(Call(context, "filesystem.get_current_directory")).Text);

            OkUnit(Call(context, "filesystem.set_current_directory", PathOf("..")));
            Assert.Equal(physicalRoot, Ok<PathValue>(Call(context, "filesystem.get_current_directory")).Text);
            OkUnit(Call(context, "filesystem.write_file_overwrite", Text("relatif"), PathOf("rel.txt")));
            Assert.Equal("relatif", File.ReadAllText(workspace.Combine("rel.txt")));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void Change_directory_error_kinds()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("f", "x");

        AssertIoError(Call("filesystem.set_current_directory", PathOf(workspace.Combine("missing"))), "not_found", workspace.Combine("missing"));
        AssertIoError(Call("filesystem.set_current_directory", PathOf(file)), "not_a_directory", file);
        AssertIoError(Call("filesystem.set_current_directory", PathOf("a\0")), "invalid_path");
        AssertIoError(Call("filesystem.set_current_directory", PathOf(string.Empty)), "not_found");
    }

    [Fact]
    public void Change_directory_into_a_non_traversable_directory_is_permission_denied()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        var locked = workspace.Combine("locked");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.UserRead);

        AssertIoError(Call("filesystem.set_current_directory", PathOf(locked)), "permission_denied", locked);
    }

    [Fact]
    public void Current_directory_fails_with_not_found_once_the_directory_is_deleted()
    {
        using var workspace = new SystemTestWorkspace();
        var doomed = workspace.Combine("doomed");
        Directory.CreateDirectory(doomed);

        var previous = Directory.GetCurrentDirectory();
        try
        {
            OkUnit(Call("filesystem.set_current_directory", PathOf(doomed)));
            Directory.Delete(doomed);

            AssertIoError(Call("filesystem.get_current_directory"), "not_found");
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void Children_inherit_the_changed_directory()
    {
        using var workspace = new SystemTestWorkspace();
        var previous = Directory.GetCurrentDirectory();
        try
        {
            using var context = NewContext();
            OkUnit(Call(context, "filesystem.set_current_directory", PathOf(workspace.Root)));

            var outcome = SystemScripts.Exec(context, "pwd", "-P");

            Assert.Equal(Path.GetFullPath(workspace.Root) + "\n", outcome.GetText("standard_output"));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemTouchTests
{
    private static readonly DateTime OldTimestamp = DateTime.UnixEpoch.AddSeconds(1_000_000_000);

    [Fact]
    public void Touch_on_a_link_to_a_file_updates_the_link_and_leaves_the_target_unchanged()
    {
        using var workspace = new SystemTestWorkspace();
        var target = workspace.Write("target.txt", "keep");
        var link = workspace.Combine("link");
        File.CreateSymbolicLink(link, target);
        SetFileTimes(target);
        SetFileTimes(link);

        AssertLinkTimesSetWithoutFollowing(link);
        Assert.Equal(OldSeconds, ModifiedSeconds(target, followLinks: true));

        OkUnit(Call("filesystem.touch", PathOf(link)));

        Assert.Equal(OldSeconds, ModifiedSeconds(target, followLinks: true));
        Assert.NotEqual(OldSeconds, ModifiedSeconds(link, followLinks: false));
        Assert.Equal("keep", File.ReadAllText(target));
    }

    [Fact]
    public void Touch_on_a_dangling_link_updates_the_link_and_does_not_create_the_target()
    {
        using var workspace = new SystemTestWorkspace();
        var absentTarget = workspace.Combine("nowhere");
        var link = workspace.Combine("dangling");
        File.CreateSymbolicLink(link, absentTarget);
        SetFileTimes(link);

        AssertLinkTimesSetWithoutFollowing(link);

        OkUnit(Call("filesystem.touch", PathOf(link)));

        Assert.NotEqual(OldSeconds, ModifiedSeconds(link, followLinks: false));
        Assert.False(File.Exists(absentTarget));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void Touch_on_a_link_to_a_directory_updates_the_link_and_leaves_the_directory_unchanged()
    {
        using var workspace = new SystemTestWorkspace();
        var directory = workspace.Combine("target-directory");
        Directory.CreateDirectory(directory);
        var link = workspace.Combine("link");
        File.CreateSymbolicLink(link, directory);
        SetDirectoryTimes(directory);
        SetFileTimes(link);

        AssertLinkTimesSetWithoutFollowing(link);
        Assert.Equal(OldSeconds, ModifiedSeconds(directory, followLinks: true));

        OkUnit(Call("filesystem.touch", PathOf(link)));

        Assert.Equal(OldSeconds, ModifiedSeconds(directory, followLinks: true));
        Assert.NotEqual(OldSeconds, ModifiedSeconds(link, followLinks: false));
    }

    [Fact]
    public void Touch_on_a_file_link_with_trailing_slashes_updates_the_link_and_leaves_the_target_unchanged()
    {
        using var workspace = new SystemTestWorkspace();
        var target = workspace.Write("target.txt", "keep");
        var link = workspace.Combine("link");
        File.CreateSymbolicLink(link, target);
        SetFileTimes(target);

        foreach (var spelling in new[] { link + "/", link + "//" })
        {
            SetFileTimes(link);
            AssertLinkTimesSetWithoutFollowing(link);

            OkUnit(Call("filesystem.touch", PathOf(spelling)));

            Assert.NotEqual(OldSeconds, ModifiedSeconds(link, followLinks: false));
            Assert.Equal(OldSeconds, ModifiedSeconds(target, followLinks: true));
        }

        Assert.Equal("keep", File.ReadAllText(target));
    }

    [Fact]
    public void Touch_on_a_directory_link_with_trailing_slashes_updates_the_link_and_leaves_the_directory_unchanged()
    {
        using var workspace = new SystemTestWorkspace();
        var directory = workspace.Combine("target-directory");
        Directory.CreateDirectory(directory);
        var link = workspace.Combine("link");
        File.CreateSymbolicLink(link, directory);
        SetDirectoryTimes(directory);

        foreach (var spelling in new[] { link + "/", link + "//" })
        {
            SetFileTimes(link);
            AssertLinkTimesSetWithoutFollowing(link);

            OkUnit(Call("filesystem.touch", PathOf(spelling)));

            Assert.NotEqual(OldSeconds, ModifiedSeconds(link, followLinks: false));
            Assert.Equal(OldSeconds, ModifiedSeconds(directory, followLinks: true));
        }
    }

    [Fact]
    public void Touch_on_a_dangling_link_with_a_trailing_slash_updates_the_link_and_does_not_create_the_target()
    {
        using var workspace = new SystemTestWorkspace();
        var absentTarget = workspace.Combine("nowhere");
        var link = workspace.Combine("dangling");
        File.CreateSymbolicLink(link, absentTarget);
        SetFileTimes(link);

        AssertLinkTimesSetWithoutFollowing(link);

        OkUnit(Call("filesystem.touch", PathOf(link + "/")));

        Assert.NotEqual(OldSeconds, ModifiedSeconds(link, followLinks: false));
        Assert.False(File.Exists(absentTarget));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void Touch_on_a_regular_file_with_a_trailing_slash_trims_the_slash_and_succeeds()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("file.txt", "keep");
        SetFileTimes(file);

        OkUnit(Call("filesystem.touch", PathOf(file + "/")));

        Assert.NotEqual(OldSeconds, ModifiedSeconds(file, followLinks: true));
        Assert.Equal("keep", File.ReadAllText(file));
    }

    [Fact]
    public void Touch_updates_a_regular_file_and_a_regular_directory()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("file.txt", "keep");
        var directory = workspace.Combine("directory");
        Directory.CreateDirectory(directory);
        SetFileTimes(file);
        SetDirectoryTimes(directory);

        OkUnit(Call("filesystem.touch", PathOf(file)));
        OkUnit(Call("filesystem.touch", PathOf(directory)));

        Assert.NotEqual(OldSeconds, ModifiedSeconds(file, followLinks: true));
        Assert.NotEqual(OldSeconds, ModifiedSeconds(directory, followLinks: true));
        Assert.Equal("keep", File.ReadAllText(file));
    }

    [Fact]
    public void Touch_creates_a_missing_path_as_an_empty_file()
    {
        using var workspace = new SystemTestWorkspace();
        var missing = workspace.Combine("created.txt");

        OkUnit(Call("filesystem.touch", PathOf(missing)));

        Assert.True(File.Exists(missing));
        Assert.Equal(0, new FileInfo(missing).Length);
    }

    private static long OldSeconds => (long)(OldTimestamp - DateTime.UnixEpoch).TotalSeconds;

    /// <summary>Sets both times through System.IO, which must update a final link itself on this runtime.</summary>
    private static void SetFileTimes(string path)
    {
        File.SetLastAccessTimeUtc(path, OldTimestamp);
        File.SetLastWriteTimeUtc(path, OldTimestamp);
    }

    private static void SetDirectoryTimes(string path)
    {
        Directory.SetLastAccessTimeUtc(path, OldTimestamp);
        Directory.SetLastWriteTimeUtc(path, OldTimestamp);
    }

    /// <summary>
    /// BUG-019 stop condition: the setup writes the link's times through <c>System.IO</c>, which must update the
    /// link itself. If it followed the final link, the target would have changed and the whole no-follow premise
    /// of this suite would be false on this runtime.
    /// </summary>
    private static void AssertLinkTimesSetWithoutFollowing(string link) =>
        Assert.True(
            ModifiedSeconds(link, followLinks: false) == OldSeconds,
            "BUG-019 stop condition: System.IO followed the final symbolic link on this runtime; stop and report before changing the approach");

    private static long ModifiedSeconds(string path, bool followLinks)
    {
        var error = NativeFileSystem.TryGetStatus(NativeFileSystem.ToNativePath(path), followLinks, out var status);
        Assert.Equal(0, error);
        return status.ModifiedSeconds;
    }
}
