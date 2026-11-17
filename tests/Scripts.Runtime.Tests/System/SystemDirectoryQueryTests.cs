using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>Extracts the path/kind pairs of directory listings, walks and reports.</summary>
internal static class DirectoryResultTexts
{
    public static (string Path, string Kind)[] Entries(Value result) =>
        [.. Ok<ListValue>(result).Items.Select(item =>
        {
            var entry = Assert.IsType<RecordValue>(item);
            Assert.Equal("directory_entry", entry.TypeName);
            return (Assert.IsType<PathValue>(entry.Get("path")).Text, entry.GetText("kind"));
        })];

    public static string[] Paths(Value result) =>
        [.. Entries(result).Select(entry => entry.Path)];

    public static (string Path, string Kind)[] ReportEntries(Value result) =>
        Pairs(Ok<RecordValue>(result).Get("entries"));

    public static (string Path, string Kind)[] ReportSkips(Value result) =>
        Pairs(Ok<RecordValue>(result).Get("skipped_paths"));

    public static string[] ReportPaths(Value result) =>
        [.. ReportEntries(result).Select(entry => entry.Path)];

    private static (string Path, string Kind)[] Pairs(Value list) =>
        [.. Assert.IsType<ListValue>(list).Items.Select(item =>
        {
            var record = Assert.IsType<RecordValue>(item);
            var field = record.TypeName == "walk_skip" ? "path_text" : "path";
            var path = record.Get(field);
            return (path is PathValue pathValue ? pathValue.Text : record.GetText(field), record.GetText("kind"));
        })];
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemListDirectoryTests
{
    [Fact]
    public void Lists_direct_entries_sorted_by_utf16_ordinal_including_hidden_files()
    {
        using var workspace = new SystemTestWorkspace();
        foreach (var name in new[] { "b", "B", ".hidden", "é", "日", "\uFF5E", "😀", "a b", "-x", "e" })
        {
            workspace.Write(name, string.Empty);
        }

        Directory.CreateDirectory(workspace.Combine("sub"));
        File.WriteAllText(workspace.Combine("sub", "nested"), string.Empty);

        var entries = DirectoryResultTexts.Entries(Call("filesystem.list_directory", PathOf(workspace.Root)));

        string[] expectedNames = ["-x", ".hidden", "B", "a b", "b", "e", "sub", "é", "日", "😀", "\uFF5E"];
        Assert.Equal(expectedNames.Select(name => workspace.Root + "/" + name), entries.Select(entry => entry.Path));
        Assert.Equal("directory", entries.Single(entry => entry.Path == workspace.Root + "/sub").Kind);
        Assert.All(
            entries.Where(entry => entry.Path != workspace.Root + "/sub"),
            entry => Assert.Equal("file", entry.Kind));
    }

    [Fact]
    public void Joins_with_the_provided_directory_text()
    {
        using var workspace = new SystemTestWorkspace();
        workspace.Write("x", string.Empty);

        WithWorkingDirectory(workspace.Root, () =>
            Assert.Equal(["./x"], DirectoryResultTexts.Paths(Call("filesystem.list_directory", PathOf(".")))));
        Assert.Equal([workspace.Root + "/x"], DirectoryResultTexts.Paths(Call("filesystem.list_directory", PathOf(workspace.Root + "/"))));
        var rootEntries = DirectoryResultTexts.Paths(Call("filesystem.list_directory", PathOf("/")));
        Assert.Contains("/proc", rootEntries);
        Assert.DoesNotContain(rootEntries, entry => entry.StartsWith("//", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_directory_is_an_empty_list_and_symlinks_are_listed()
    {
        using var workspace = new SystemTestWorkspace();
        var empty = workspace.Combine("empty");
        Directory.CreateDirectory(empty);
        var other = workspace.Combine("other");
        Directory.CreateDirectory(other);
        File.CreateSymbolicLink(workspace.Combine("other", "link"), workspace.Root);

        Assert.Empty(Ok<ListValue>(Call("filesystem.list_directory", PathOf(empty))).Items);
        Assert.Equal([other + "/link"], DirectoryResultTexts.Paths(Call("filesystem.list_directory", PathOf(other))));
    }

    [Fact]
    public void Reports_error_kinds()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("f", "x");

        AssertIoError(Call("filesystem.list_directory", PathOf(file)), "not_a_directory", file);
        AssertIoError(Call("filesystem.list_directory", PathOf(workspace.Combine("missing"))), "not_found", workspace.Combine("missing"));
        AssertIoError(Call("filesystem.list_directory", PathOf("a\0")), "invalid_path");
        AssertIoError(Call("filesystem.list_directory", PathOf(string.Empty)), "not_found");
    }

    [Fact]
    public void Unreadable_directory_is_permission_denied()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        var locked = workspace.Combine("locked");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);

        AssertIoError(Call("filesystem.list_directory", PathOf(locked)), "permission_denied", locked);
    }

    [Fact]
    public void Entry_name_that_is_not_utf8_fails_the_whole_call_with_invalid_path()
    {
        using var workspace = new SystemTestWorkspace();
        workspace.Write("fine", string.Empty);
        SystemRawFiles.CreateFileWithRawName(workspace.Root, [0x62, 0xFF, 0x63]);

        AssertIoError(Call("filesystem.list_directory", PathOf(workspace.Root)), "invalid_path", workspace.Root);
    }

    [Fact]
    public void Follows_a_symlink_to_a_directory_given_as_argument()
    {
        using var workspace = new SystemTestWorkspace();
        workspace.Write("real/x", string.Empty);
        var link = workspace.Combine("link");
        File.CreateSymbolicLink(link, workspace.Combine("real"));

        Assert.Equal([link + "/x"], DirectoryResultTexts.Paths(Call("filesystem.list_directory", PathOf(link))));
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemGetFileStatusTests
{
    [Fact]
    public void Describes_a_regular_file()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Write("cinq.txt", "cinq!");
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddSeconds(1_000_000_000).AddMilliseconds(900));

        var status = Ok<RecordValue>(Call("filesystem.get_path_status", PathOf(path)));

        Assert.Equal("path_status", status.TypeName);
        Assert.Equal("file", status.GetText("kind"));
        Assert.Equal(5, status.GetInteger("size_bytes"));
        Assert.Equal(1_000_000_000, status.GetInteger("modified_unix_seconds"));
        Assert.Equal(493, status.GetInteger("permissions"));
    }

    [Fact]
    public void Modification_time_before_the_epoch_is_floored()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Write("old.txt", "x");
        File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddMilliseconds(-500));

        var status = Ok<RecordValue>(Call("filesystem.get_path_status", PathOf(path)));

        Assert.Equal(-1, status.GetInteger("modified_unix_seconds"));
    }

    [Fact]
    public void Does_not_follow_symlinks_and_classifies_kinds()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("f.txt", "abc");
        var link = workspace.Combine("link");
        File.CreateSymbolicLink(link, file);
        var directory = workspace.Combine("dir");
        Directory.CreateDirectory(directory);

        Assert.Equal("symlink", Ok<RecordValue>(Call("filesystem.get_path_status", PathOf(link))).GetText("kind"));
        Assert.Equal("directory", Ok<RecordValue>(Call("filesystem.get_path_status", PathOf(directory))).GetText("kind"));
        Assert.Equal("other", Ok<RecordValue>(Call("filesystem.get_path_status", PathOf("/dev/null"))).GetText("kind"));
    }

    [Fact]
    public void Reports_error_kinds()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("f", "x");

        AssertIoError(Call("filesystem.get_path_status", PathOf(workspace.Combine("missing"))), "not_found", workspace.Combine("missing"));
        AssertIoError(Call("filesystem.get_path_status", PathOf(file + "/x")), "not_a_directory", file + "/x");
        AssertIoError(Call("filesystem.get_path_status", PathOf("x\0")), "invalid_path");
        AssertIoError(Call("filesystem.get_path_status", PathOf(string.Empty)), "not_found");
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemWalkDirectoryTests
{
    [Fact]
    public void Walks_depth_first_pre_order_with_sorted_siblings_and_without_the_root()
    {
        using var workspace = new SystemTestWorkspace();
        workspace.Write("b.txt", string.Empty);
        workspace.Write("a/x.txt", string.Empty);
        workspace.Write("a/c/deep.txt", string.Empty);
        workspace.Write("a/b", string.Empty);
        workspace.Write(".hidden/h", string.Empty);

        var walked = DirectoryResultTexts.ReportPaths(Call("filesystem.walk_directory", PathOf(workspace.Root)));

        string[] expected = [".hidden", ".hidden/h", "a", "a/b", "a/c", "a/c/deep.txt", "a/x.txt", "b.txt"];
        Assert.Equal(expected.Select(relative => workspace.Root + "/" + relative), walked);
    }

    [Fact]
    public void Lists_symlinks_to_directories_without_traversing_them_even_loops()
    {
        using var workspace = new SystemTestWorkspace();
        workspace.Write("real/inside.txt", string.Empty);
        File.CreateSymbolicLink(workspace.Combine("link"), workspace.Combine("real"));
        File.CreateSymbolicLink(workspace.Combine("real", "back"), workspace.Root);
        File.CreateSymbolicLink(workspace.Combine("self"), workspace.Combine("self"));

        var walked = DirectoryResultTexts.ReportPaths(Call("filesystem.walk_directory", PathOf(workspace.Root)));

        string[] expected = ["link", "real", "real/back", "real/inside.txt", "self"];
        Assert.Equal(expected.Select(relative => workspace.Root + "/" + relative), walked);
    }

    [Fact]
    public void Follows_a_root_that_is_a_symlink_to_a_directory()
    {
        using var workspace = new SystemTestWorkspace();
        workspace.Write("real/x", string.Empty);
        var link = workspace.Combine("link");
        File.CreateSymbolicLink(link, workspace.Combine("real"));

        Assert.Equal([link + "/x"], DirectoryResultTexts.ReportPaths(Call("filesystem.walk_directory", PathOf(link))));
    }

    [Fact]
    public void Keeps_relative_root_text_and_handles_very_deep_trees_without_stack_growth()
    {
        using var workspace = new SystemTestWorkspace();
        var relativeChain = string.Join("/", Enumerable.Repeat("d", 300));
        Directory.CreateDirectory(workspace.Combine(relativeChain));

        WithWorkingDirectory(workspace.Root, () =>
        {
            var walked = DirectoryResultTexts.ReportPaths(Call("filesystem.walk_directory", PathOf(".")));

            Assert.Equal(300, walked.Length);
            Assert.Equal("./d", walked[0]);
            Assert.Equal("./" + relativeChain, walked[^1]);
        });
    }

    [Fact]
    public void Reports_root_error_kinds()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("f", "x");

        AssertIoError(Call("filesystem.walk_directory", PathOf(file)), "not_a_directory", file);
        AssertIoError(Call("filesystem.walk_directory", PathOf(workspace.Combine("missing"))), "not_found", workspace.Combine("missing"));
        AssertIoError(Call("filesystem.walk_directory", PathOf("x\0")), "invalid_path");
    }

    [Fact]
    public void A_known_d_type_answers_without_stat_even_when_the_path_does_not_exist()
    {
        // The whole point of the d_type fast path: a known type is answered from the byte the
        // directory stream already returned, so the path text is never resolved. A path that does
        // not exist therefore still yields the right answer, and no statx can be observed.
        using var workspace = new SystemTestWorkspace();
        var absent = workspace.Combine("never-created");

        // The raw d_type values are used directly: DT_DIR = 4, DT_REG = 8, DT_LNK = 10, and
        // DT_UNKNOWN = 0. Production only names DT_UNKNOWN and DT_DIR, so the test pins them here.
        const byte DtUnknown = 0;
        const byte DtDir = 4;
        const byte DtReg = 8;
        const byte DtLnk = 10;

        Assert.True(Scripts.Runtime.Platform.NativeFileSystem.IsDirectoryEntry(absent, DtDir));
        Assert.False(Scripts.Runtime.Platform.NativeFileSystem.IsDirectoryEntry(absent, DtReg));
        Assert.False(Scripts.Runtime.Platform.NativeFileSystem.IsDirectoryEntry(absent, DtLnk));
        Assert.False(Scripts.Runtime.Platform.NativeFileSystem.IsDirectoryEntry(absent, DtUnknown));
    }

    [Fact]
    public void An_unknown_d_type_falls_back_to_a_no_follow_stat()
    {
        using var workspace = new SystemTestWorkspace();
        var directory = workspace.Combine("real");
        Directory.CreateDirectory(directory);
        var file = workspace.Write("real.txt", string.Empty);
        var link = workspace.Combine("link");
        File.CreateSymbolicLink(link, directory);

        const byte Unknown = 0; // DT_UNKNOWN
        Assert.True(Scripts.Runtime.Platform.NativeFileSystem.IsDirectoryEntry(directory, Unknown));
        Assert.False(Scripts.Runtime.Platform.NativeFileSystem.IsDirectoryEntry(file, Unknown));
        // A symlink to a directory is not a directory when the final link is not followed.
        Assert.False(Scripts.Runtime.Platform.NativeFileSystem.IsDirectoryEntry(link, Unknown));
        // A path that cannot be inspected is not a directory.
        Assert.False(Scripts.Runtime.Platform.NativeFileSystem.IsDirectoryEntry(workspace.Combine("missing"), Unknown));
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemFileSystemEntriesTests
{
    [Fact]
    public void Walk_yields_the_pre_order_entries_with_their_kinds()
    {
        using var workspace = new SystemTestWorkspace();
        workspace.Write("b.txt", string.Empty);
        workspace.Write("a/x.txt", string.Empty);
        workspace.Write("a/c/deep.txt", string.Empty);
        Directory.CreateDirectory(workspace.Combine("empty"));
        File.CreateSymbolicLink(workspace.Combine("link"), workspace.Combine("a"));

        var entries = DirectoryResultTexts.ReportEntries(Call("filesystem.walk_directory", PathOf(workspace.Root)));
        Assert.Empty(DirectoryResultTexts.ReportSkips(Call("filesystem.walk_directory", PathOf(workspace.Root))));
        Assert.Equal(
            [("a", "directory"), ("a/c", "directory"), ("a/c/deep.txt", "file"), ("a/x.txt", "file"),
                ("b.txt", "file"), ("empty", "directory"), ("link", "symlink")],
            entries.Select(entry => (entry.Path[(workspace.Root.Length + 1)..], entry.Kind)));
    }

    [Fact]
    public void List_names_the_kind_of_each_direct_entry()
    {
        using var workspace = new SystemTestWorkspace();
        workspace.Write("b.txt", string.Empty);
        Directory.CreateDirectory(workspace.Combine("a"));
        File.CreateSymbolicLink(workspace.Combine("link"), workspace.Combine("a"));

        var entries = DirectoryResultTexts.Entries(Call("filesystem.list_directory", PathOf(workspace.Root)));
        Assert.Equal(
            [("a", "directory"), ("b.txt", "file"), ("link", "symlink")],
            entries.Select(entry => (entry.Path[(workspace.Root.Length + 1)..], entry.Kind)));
    }

    [Fact]
    public void An_empty_directory_lists_no_entries()
    {
        using var workspace = new SystemTestWorkspace();
        var empty = workspace.Combine("empty");
        Directory.CreateDirectory(empty);

        Assert.Empty(Ok<ListValue>(Call("filesystem.list_directory", PathOf(empty))).Items);
        Assert.Empty(DirectoryResultTexts.ReportEntries(Call("filesystem.walk_directory", PathOf(empty))));
    }

    [Fact]
    public void Entry_kind_name_maps_each_known_d_type_without_stats()
    {
        // Characterises the d_type -> kind mapping directly: a known value is answered from the byte,
        // so a path that does not exist is never resolved (and no statx can be observed).
        const string absent = "/scripts-never-created-directory-entry";
        Assert.Equal("file", Scripts.Runtime.Platform.NativeFileSystem.EntryKindName(absent, 8));      // DT_REG
        Assert.Equal("directory", Scripts.Runtime.Platform.NativeFileSystem.EntryKindName(absent, 4)); // DT_DIR
        Assert.Equal("symlink", Scripts.Runtime.Platform.NativeFileSystem.EntryKindName(absent, 10));  // DT_LNK
        Assert.Equal("other", Scripts.Runtime.Platform.NativeFileSystem.EntryKindName(absent, 1));     // DT_FIFO
        Assert.Equal("other", Scripts.Runtime.Platform.NativeFileSystem.EntryKindName(absent, 12));    // DT_SOCK
        // Only DT_UNKNOWN (0) falls back to a no-follow stat, which fails here: not a directory.
        Assert.Equal("other", Scripts.Runtime.Platform.NativeFileSystem.EntryKindName(absent, 0));
    }

    [Fact]
    public void Reports_error_kinds_like_walk_and_list()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("f", "x");

        AssertIoError(Call("filesystem.walk_directory", PathOf(file)), "not_a_directory", file);
        AssertIoError(Call("filesystem.walk_directory", PathOf(workspace.Combine("missing"))), "not_found", workspace.Combine("missing"));
        AssertIoError(Call("filesystem.list_directory", PathOf(file)), "not_a_directory", file);
        AssertIoError(Call("filesystem.list_directory", PathOf("x\0")), "invalid_path");
    }

    [Fact]
    public void Walk_has_no_skips_when_nothing_is_unreadable()
    {
        using var workspace = new SystemTestWorkspace();
        workspace.Write("b.txt", string.Empty);
        workspace.Write("a/x.txt", string.Empty);
        workspace.Write("a/c/deep.txt", string.Empty);
        File.CreateSymbolicLink(workspace.Combine("link"), workspace.Combine("a"));

        var report = Call("filesystem.walk_directory", PathOf(workspace.Root));

        Assert.Empty(DirectoryResultTexts.ReportSkips(report));
        Assert.Equal(
            [
                workspace.Root + "/a", workspace.Root + "/a/c", workspace.Root + "/a/c/deep.txt",
                workspace.Root + "/a/x.txt", workspace.Root + "/b.txt", workspace.Root + "/link",
            ],
            DirectoryResultTexts.ReportEntries(report).Select(entry => entry.Path));
    }

    [Fact]
    public void Walk_keeps_an_unreadable_directory_but_not_its_children()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        workspace.Write("a/x.txt", string.Empty);
        workspace.Write("b.txt", string.Empty);
        workspace.Write("locked/secret.txt", string.Empty);
        var locked = workspace.Combine("locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        var report = Call("filesystem.walk_directory", PathOf(workspace.Root));

        string[] expectedEntries =
        [
            workspace.Root + "/a", workspace.Root + "/a/x.txt", workspace.Root + "/b.txt", locked,
        ];
        Assert.Equal(expectedEntries, DirectoryResultTexts.ReportEntries(report).Select(entry => entry.Path));
        Assert.Equal([(locked, "permission_denied")], DirectoryResultTexts.ReportSkips(report));
    }

    [Fact]
    public void Walk_skips_a_non_utf8_name_with_its_lenient_path()
    {
        using var workspace = new SystemTestWorkspace();
        workspace.Write("a.txt", string.Empty);
        var directory = workspace.Combine("sub");
        Directory.CreateDirectory(directory);
        SystemRawFiles.CreateFileWithRawName(directory, [0xC3]);

        var report = Call("filesystem.walk_directory", PathOf(workspace.Root));

        Assert.Equal(
            [workspace.Root + "/a.txt", directory],
            DirectoryResultTexts.ReportEntries(report).Select(entry => entry.Path));
        // One U+FFFD replaces the invalid byte sequence so the skip still names the child.
        Assert.Equal([(directory + "/\uFFFD", "invalid_encoding")], DirectoryResultTexts.ReportSkips(report));
    }

    [Fact]
    public void Walk_reports_root_failures_as_errors()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("f", "x");

        AssertIoError(Call("filesystem.walk_directory", PathOf(file)), "not_a_directory", file);
        AssertIoError(Call("filesystem.walk_directory", PathOf(workspace.Combine("missing"))), "not_found", workspace.Combine("missing"));
        AssertIoError(Call("filesystem.walk_directory", PathOf("x\0")), "invalid_path");
    }
}
