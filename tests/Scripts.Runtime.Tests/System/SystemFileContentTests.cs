using Scripts.Runtime.Platform;
using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

internal static class SystemRawFiles
{
    public static bool RunningAsRoot => Libc.GetEffectiveUserIdentifier() == 0;

    /// <summary>Creates an empty file whose name is raw bytes (possibly invalid UTF-8).</summary>
    public static void CreateFileWithRawName(string directory, byte[] name)
    {
        var nativePath = NativeFileSystem.Combine(System.Text.Encoding.UTF8.GetBytes(directory), name);
        var descriptor = Libc.Open(nativePath, Libc.OpenWriteOnly | Libc.OpenCreate, 0x1A4);
        Assert.True(descriptor >= 0, "creating the file with a raw name failed");
        Libc.Close(descriptor);
    }

    public static void MakeFifo(string path)
    {
        using var process = System.Diagnostics.Process.Start("mkfifo", path)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    public static UnixFileMode ModeOf(string path) => File.GetUnixFileMode(path);

    /// <summary>Writable stream that does not take the exclusive advisory lock System.IO uses by default.</summary>
    public static FileStream OpenShared(string path) => new(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);

    public static void CreateHardLink(string linkPath, string existingPath)
    {
        using var process = System.Diagnostics.Process.Start("ln", [existingPath, linkPath])!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemReadFileTests
{
    [Fact]
    public void Reads_unicode_content_and_keeps_the_byte_order_mark()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("bom.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. "café 日本 😀"u8.ToArray()]);

        var content = Ok<StringValue>(Call("filesystem.read_file", PathOf(path))).Text;

        Assert.Equal("\uFEFFcafé 日本 😀", content);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0xC0, 0x80 })]
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61, 0x00 })]
    [InlineData(new byte[] { 0x61, 0xE2, 0x82 })]
    public void Rejects_invalid_utf8_with_invalid_encoding(byte[] bytes)
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("bad.txt");
        File.WriteAllBytes(path, bytes);

        AssertIoError(Call("filesystem.read_file", PathOf(path)), "invalid_encoding", path);
    }

    [Fact]
    public void Reads_an_empty_file_and_a_procfs_file_reporting_size_zero()
    {
        using var workspace = new SystemTestWorkspace();
        var empty = workspace.Write("empty.txt", string.Empty);

        Assert.Equal(string.Empty, Ok<StringValue>(Call("filesystem.read_file", PathOf(empty))).Text);
        Assert.Equal("Linux\n", Ok<StringValue>(Call("filesystem.read_file", PathOf("/proc/sys/kernel/ostype"))).Text);
    }

    [Fact]
    public void Reads_a_large_file_exactly()
    {
        using var workspace = new SystemTestWorkspace();
        var text = new string('x', 5_000_000) + "é";
        var path = workspace.Write("large.txt", text);

        Assert.Equal(text, Ok<StringValue>(Call("filesystem.read_file", PathOf(path))).Text);
    }

    [Fact]
    public void Missing_file_and_empty_path_are_not_found_with_the_provided_subject()
    {
        AssertIoError(Call("filesystem.read_file", PathOf("/scripts-inexistant/absent.txt")), "not_found", "/scripts-inexistant/absent.txt");
        AssertIoError(Call("filesystem.read_file", PathOf(string.Empty)), "not_found", string.Empty);
    }

    [Fact]
    public void Directory_is_a_directory_error_and_component_file_is_not_a_directory()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("file.txt", "x");

        AssertIoError(Call("filesystem.read_file", PathOf(workspace.Root)), "is_a_directory", workspace.Root);
        AssertIoError(Call("filesystem.read_file", PathOf(file + "/below")), "not_a_directory", file + "/below");
    }

    [Fact]
    public void Fifo_is_other_without_blocking()
    {
        using var workspace = new SystemTestWorkspace();
        var fifo = workspace.Combine("pipe");
        SystemRawFiles.MakeFifo(fifo);

        AssertIoError(Call("filesystem.read_file", PathOf(fifo)), "other", fifo);
    }

    [Fact]
    public void Unreadable_file_is_permission_denied()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        var path = workspace.Write("secret.txt", "x");
        File.SetUnixFileMode(path, UnixFileMode.None);

        AssertIoError(Call("filesystem.read_file", PathOf(path)), "permission_denied", path);
    }

    [Fact]
    public void Symlink_loop_and_overlong_names_are_invalid_path()
    {
        using var workspace = new SystemTestWorkspace();
        var loop = workspace.Combine("loop");
        File.CreateSymbolicLink(loop, loop);

        AssertIoError(Call("filesystem.read_file", PathOf(loop)), "invalid_path", loop);
        AssertIoError(Call("filesystem.read_file", PathOf("/" + new string('a', 5000))), "invalid_path");
    }

    [Fact]
    public void Nul_in_path_is_invalid_path_before_any_system_call()
    {
        AssertIoError(Call("filesystem.read_file", PathOf("a\0b")), "invalid_path", "a\0b");
    }

    [Fact]
    public void File_larger_than_256_mebibytes_is_too_large()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("sparse.bin");
        using (var stream = File.Create(path))
        {
            stream.SetLength((1L << 28) + 1);
        }

        AssertIoError(Call("filesystem.read_file", PathOf(path)), "too_large", path);
    }

    [Fact]
    public void Follows_symlinks()
    {
        using var workspace = new SystemTestWorkspace();
        var target = workspace.Write("target.txt", "cible");
        var link = workspace.Combine("link");
        File.CreateSymbolicLink(link, target);

        Assert.Equal("cible", Ok<StringValue>(Call("filesystem.read_file", PathOf(link))).Text);
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemReadFileNoFollowTests
{
    [Fact]
    public void Reads_a_regular_file_with_the_same_whole_file_semantics()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Write("note.txt", "bonjour");

        Assert.Equal("bonjour", Ok<StringValue>(Call("filesystem.read_file_no_follow", PathOf(path))).Text);
    }

    [Fact]
    public void Refuses_a_final_symlink_to_a_file_and_a_dangling_one()
    {
        using var workspace = new SystemTestWorkspace();
        var target = workspace.Write("target.txt", "secret");
        var link = workspace.Combine("link");
        File.CreateSymbolicLink(link, target);
        var dangling = workspace.Combine("dangling");
        File.CreateSymbolicLink(dangling, workspace.Combine("missing"));

        AssertIoError(Call("filesystem.read_file_no_follow", PathOf(link)), "invalid_path", link);
        AssertIoError(Call("filesystem.read_file_no_follow", PathOf(dangling)), "invalid_path", dangling);
    }

    [Fact]
    public void A_final_symlink_with_a_trailing_slash_is_not_followed()
    {
        using var workspace = new SystemTestWorkspace();
        var target = workspace.Write("target.txt", "secret");
        var link = workspace.Combine("link");
        File.CreateSymbolicLink(link, target);

        AssertIoError(Call("filesystem.read_file_no_follow", PathOf(link + "/")), "invalid_path", link + "/");
    }

    [Fact]
    public void Follows_an_intermediate_directory_symlink()
    {
        using var workspace = new SystemTestWorkspace();
        var directory = workspace.Combine("real");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "target.txt");
        File.WriteAllText(file, "contenu");
        var directoryLink = workspace.Combine("directory-link");
        File.CreateSymbolicLink(directoryLink, directory);

        Assert.Equal(
            "contenu",
            Ok<StringValue>(Call("filesystem.read_file_no_follow", PathOf(Path.Combine(directoryLink, "target.txt")))).Text);
    }

    [Fact]
    public void Directory_is_a_directory_and_a_missing_path_is_not_found()
    {
        using var workspace = new SystemTestWorkspace();

        AssertIoError(Call("filesystem.read_file_no_follow", PathOf(workspace.Root)), "is_a_directory", workspace.Root);
        AssertIoError(
            Call("filesystem.read_file_no_follow", PathOf(workspace.Combine("absent.txt"))),
            "not_found",
            workspace.Combine("absent.txt"));
    }

    [Fact]
    public void Nul_in_path_is_invalid_path()
    {
        AssertIoError(Call("filesystem.read_file_no_follow", PathOf("a\0b")), "invalid_path", "a\0b");
    }

    [Fact]
    public void Invalid_utf8_is_invalid_encoding()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("bad.bin");
        File.WriteAllBytes(path, [0xFF]);

        AssertIoError(Call("filesystem.read_file_no_follow", PathOf(path)), "invalid_encoding", path);
    }

    [Fact]
    public void Fifo_is_other_without_blocking()
    {
        using var workspace = new SystemTestWorkspace();
        var fifo = workspace.Combine("pipe");
        SystemRawFiles.MakeFifo(fifo);

        AssertIoError(Call("filesystem.read_file_no_follow", PathOf(fifo)), "other", fifo);
    }

    [Fact]
    public void Socket_is_other_without_blocking()
    {
        using var workspace = new SystemTestWorkspace();
        var socketPath = workspace.Combine("socket");
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.Unix,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Unspecified);
        socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath));

        AssertIoError(Call("filesystem.read_file_no_follow", PathOf(socketPath)), "other", socketPath);
    }

    [Fact]
    public void File_larger_than_256_mebibytes_is_too_large()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("sparse.bin");
        using (var stream = File.Create(path))
        {
            stream.SetLength((1L << 28) + 1);
        }

        AssertIoError(Call("filesystem.read_file_no_follow", PathOf(path)), "too_large", path);
    }

    [Fact]
    public void Large_unreadable_file_is_permission_denied_before_too_large()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("sparse.bin");
        using (var stream = File.Create(path))
        {
            stream.SetLength((1L << 28) + 1);
        }

        File.SetUnixFileMode(path, UnixFileMode.None);

        AssertIoError(Call("filesystem.read_file_no_follow", PathOf(path)), "permission_denied", path);
    }

    [Fact]
    public void Parent_regular_file_is_not_a_directory()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("file.txt", "x");

        AssertIoError(Call("filesystem.read_file_no_follow", PathOf(file + "/below")), "not_a_directory", file + "/below");
    }

    [Fact]
    public void Trailing_slash_on_a_regular_file_is_not_a_directory()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("file.txt", "x");

        AssertIoError(Call("filesystem.read_file_no_follow", PathOf(file + "/")), "not_a_directory", file + "/");
    }

    [Fact]
    public void Empty_path_is_not_found()
    {
        AssertIoError(Call("filesystem.read_file_no_follow", PathOf(string.Empty)), "not_found", string.Empty);
    }

    [Fact]
    public void Symlink_loop_and_overlong_names_are_invalid_path()
    {
        using var workspace = new SystemTestWorkspace();
        var loop = workspace.Combine("loop");
        File.CreateSymbolicLink(loop, loop);

        AssertIoError(Call("filesystem.read_file_no_follow", PathOf(loop)), "invalid_path", loop);
        AssertIoError(Call("filesystem.read_file_no_follow", PathOf("/" + new string('a', 5000))), "invalid_path");
    }

    [Fact]
    public void No_follow_open_rejects_a_final_symlink_swapped_after_the_preview()
    {
        using var workspace = new SystemTestWorkspace();
        var regular = workspace.Write("regular.txt", "x");
        var link = workspace.Combine("link");
        File.CreateSymbolicLink(link, regular);

        // The preview passed on the regular file; O_NOFOLLOW makes the readable open fail on the link (ELOOP).
        var opened = RegularFileOpener.OpenReadableNoFollow(
            NativeFileSystem.ToNativePath(link),
            NativeFileSystem.ToNativePath(regular),
            static () => { });

        Assert.False(opened.IsOpened);
        Assert.Equal(RegularFileFault.Errno, opened.Fault);
        Assert.Equal(Errno.TooManyLinks, opened.Errno);
    }

    [Fact]
    public void The_descriptor_revalidation_rejects_a_directory_swapped_after_the_preview()
    {
        using var workspace = new SystemTestWorkspace();
        var regular = workspace.Write("regular.txt", "x");
        var directory = workspace.Combine("directory");
        Directory.CreateDirectory(directory);

        var opened = RegularFileOpener.OpenReadableNoFollow(
            NativeFileSystem.ToNativePath(directory),
            NativeFileSystem.ToNativePath(regular),
            static () => { });

        Assert.False(opened.IsOpened);
        Assert.Equal(RegularFileFault.Directory, opened.Fault);
    }

    [Fact]
    public async Task No_follow_open_does_not_block_on_a_fifo_swapped_after_the_preview()
    {
        using var workspace = new SystemTestWorkspace();
        var regular = workspace.Write("regular.txt", "x");
        var fifo = workspace.Combine("fifo");
        SystemRawFiles.MakeFifo(fifo);

        // O_NONBLOCK keeps the readable open of a swapped FIFO from blocking; the descriptor check reports it.
        // The bounded wait makes a regression fail instead of hanging the serialized collection.
        var openTask = Task.Run(() => RegularFileOpener.OpenReadableNoFollow(
            NativeFileSystem.ToNativePath(fifo),
            NativeFileSystem.ToNativePath(regular),
            static () => { }));
        var completed = await Task.WhenAny(openTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(openTask, completed);

        var opened = await openTask;
        Assert.False(opened.IsOpened);
        Assert.Equal(RegularFileFault.NotRegular, opened.Fault);
    }

    [Fact]
    public void No_follow_open_does_not_block_on_a_socket_swapped_after_the_preview()
    {
        using var workspace = new SystemTestWorkspace();
        var regular = workspace.Write("regular.txt", "x");
        var socketPath = workspace.Combine("socket");
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.Unix,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Unspecified);
        socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath));

        var opened = RegularFileOpener.OpenReadableNoFollow(
            NativeFileSystem.ToNativePath(socketPath),
            NativeFileSystem.ToNativePath(regular),
            static () => { });

        Assert.False(opened.IsOpened);
        Assert.Equal(RegularFileFault.NotRegular, opened.Fault);
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemWriteFileTests
{
    [Fact]
    public void Creates_then_overwrites_with_utf8_bytes_and_no_bom()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("out.txt");

        OkUnit(Call("filesystem.write_file_overwrite", Text("long old value"), PathOf(path)));
        OkUnit(Call("filesystem.write_file_overwrite", Text("é😀"), PathOf(path)));

        Assert.Equal([0xC3, 0xA9, 0xF0, 0x9F, 0x98, 0x80], File.ReadAllBytes(path));
    }

    [Fact]
    public void Empty_content_truncates()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Write("f.txt", "abc");

        OkUnit(Call("filesystem.write_file_overwrite", Text(string.Empty), PathOf(path)));

        Assert.Empty(File.ReadAllBytes(path));
    }

    [Fact]
    public void New_files_use_0666_minus_umask_and_existing_files_keep_their_permissions()
    {
        using var workspace = new SystemTestWorkspace();
        var created = workspace.Combine("new.txt");
        var existing = workspace.Write("old.txt", "x");
        File.SetUnixFileMode(existing, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        OkUnit(Call("filesystem.write_file_overwrite", Text("a"), PathOf(created)));
        OkUnit(Call("filesystem.write_file_overwrite", Text("b"), PathOf(existing)));

        var createdMode = SystemRawFiles.ModeOf(created);
        Assert.True(createdMode.HasFlag(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        Assert.False(createdMode.HasFlag(UnixFileMode.UserExecute));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, SystemRawFiles.ModeOf(existing));
    }

    [Fact]
    public void Writes_through_a_symlink()
    {
        using var workspace = new SystemTestWorkspace();
        var target = workspace.Write("target.txt", "old");
        var link = workspace.Combine("link");
        File.CreateSymbolicLink(link, target);

        OkUnit(Call("filesystem.write_file_overwrite", Text("new"), PathOf(link)));

        Assert.Equal("new", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void Reports_errors_with_the_destination_as_subject()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("file.txt", "x");

        AssertIoError(Call("filesystem.write_file_overwrite", Text("a"), PathOf(workspace.Combine("missing", "f.txt"))), "not_found", workspace.Combine("missing", "f.txt"));
        AssertIoError(Call("filesystem.write_file_overwrite", Text("a"), PathOf(workspace.Root)), "is_a_directory", workspace.Root);
        AssertIoError(Call("filesystem.write_file_overwrite", Text("a"), PathOf(file + "/x")), "not_a_directory", file + "/x");
        AssertIoError(Call("filesystem.write_file_overwrite", Text("a"), PathOf("a\0b")), "invalid_path", "a\0b");
        AssertIoError(Call("filesystem.write_file_overwrite", Text("a"), PathOf(string.Empty)), "not_found", string.Empty);
    }

    [Fact]
    public void Read_only_directory_is_permission_denied()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        var directory = workspace.Combine("ro");
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        AssertIoError(Call("filesystem.write_file_overwrite", Text("a"), PathOf(directory + "/f")), "permission_denied", directory + "/f");
    }

    [Theory]
    [InlineData("with space.txt")]
    [InlineData("new\nline.txt")]
    [InlineData("-rf")]
    [InlineData("--help")]
    [InlineData("日本語 😀.txt")]
    [InlineData("$HOME `x` ;*?")]
    public void Awkward_names_round_trip(string name)
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine(name);

        OkUnit(Call("filesystem.write_file_overwrite", Text("content"), PathOf(path)));

        Assert.Equal("content", Ok<StringValue>(Call("filesystem.read_file", PathOf(path))).Text);
        Assert.Contains(name, Directory.EnumerateFileSystemEntries(workspace.Root).Select(Path.GetFileName));
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemWriteFileAtomicallyTests
{
    private static IEnumerable<string> TemporaryFiles(string directory) =>
        Directory.EnumerateFileSystemEntries(directory)
            .Where(path => Path.GetFileName(path).StartsWith(".scs-tmp-", StringComparison.Ordinal));

    [Fact]
    public void Replaces_the_destination_and_leaves_no_temporary_file()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Write("note.txt", "ancien");

        OkUnit(Call("filesystem.write_file_overwrite_atomically", Text("é😀"), PathOf(path)));

        Assert.Equal("é😀", File.ReadAllText(path));
        Assert.Empty(TemporaryFiles(workspace.Root));
    }

    [Fact]
    public void Replaces_the_permissions_of_an_existing_destination()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Write("old.txt", "x");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        OkUnit(Call("filesystem.write_file_overwrite_atomically", Text("y"), PathOf(path)));

        var mode = SystemRawFiles.ModeOf(path);
        Assert.True(mode.HasFlag(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        Assert.False(mode.HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void Creates_a_new_file_and_replaces_a_symlink_without_touching_its_target()
    {
        using var workspace = new SystemTestWorkspace();
        var created = workspace.Combine("new.txt");
        var target = workspace.Write("target.txt", "target");
        var link = workspace.Combine("link");
        File.CreateSymbolicLink(link, target);

        OkUnit(Call("filesystem.write_file_overwrite_atomically", Text("new"), PathOf(created)));
        OkUnit(Call("filesystem.write_file_overwrite_atomically", Text("replaced"), PathOf(link)));

        Assert.Equal("new", File.ReadAllText(created));
        Assert.Equal("replaced", File.ReadAllText(link));
        Assert.Null(new FileInfo(link).LinkTarget);
        Assert.Equal("target", File.ReadAllText(target));
        Assert.Empty(TemporaryFiles(workspace.Root));
    }

    [Fact]
    public void Replaces_a_symlink_to_a_directory_or_a_fifo_instead_of_following_it()
    {
        using var workspace = new SystemTestWorkspace();
        var directory = workspace.Combine("real_directory");
        Directory.CreateDirectory(directory);
        var realFifo = workspace.Combine("real_pipe");
        SystemRawFiles.MakeFifo(realFifo);

        var linkToDirectory = workspace.Combine("link_to_directory");
        File.CreateSymbolicLink(linkToDirectory, directory);
        var linkToFifo = workspace.Combine("link_to_pipe");
        File.CreateSymbolicLink(linkToFifo, realFifo);

        OkUnit(Call("filesystem.write_file_overwrite_atomically", Text("file"), PathOf(linkToDirectory)));
        OkUnit(Call("filesystem.write_file_overwrite_atomically", Text("file"), PathOf(linkToFifo)));

        // The links themselves are replaced by regular files; their targets are untouched.
        Assert.Null(new FileInfo(linkToDirectory).LinkTarget);
        Assert.Null(new FileInfo(linkToFifo).LinkTarget);
        Assert.Equal("file", File.ReadAllText(linkToDirectory));
        Assert.Equal("file", File.ReadAllText(linkToFifo));
        Assert.True(Directory.Exists(directory));
        Assert.Empty(TemporaryFiles(workspace.Root));
    }

    [Fact]
    public void Writes_a_temporary_file_next_to_the_destination_within_the_same_directory()
    {
        using var workspace = new SystemTestWorkspace();
        var nested = workspace.Combine("sub");
        Directory.CreateDirectory(nested);
        var path = nested + "/note.txt";

        OkUnit(Call("filesystem.write_file_overwrite_atomically", Text("x"), PathOf(path)));

        Assert.Equal("x", File.ReadAllText(path));
        // The scratch file is gone from the destination directory (and never appears in the parent).
        Assert.Empty(TemporaryFiles(nested));
        Assert.Empty(TemporaryFiles(workspace.Root));
    }

    [Fact]
    public void Reports_errors_with_the_destination_as_subject()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("file.txt", "x");

        AssertIoError(Call("filesystem.write_file_overwrite_atomically", Text("a"), PathOf(workspace.Root)), "is_a_directory", workspace.Root);
        AssertIoError(Call("filesystem.write_file_overwrite_atomically", Text("a"), PathOf(workspace.Combine("missing", "f.txt"))), "not_found", workspace.Combine("missing", "f.txt"));
        AssertIoError(Call("filesystem.write_file_overwrite_atomically", Text("a"), PathOf(file + "/x")), "not_a_directory", file + "/x");
        AssertIoError(Call("filesystem.write_file_overwrite_atomically", Text("a"), PathOf("a\0b")), "invalid_path", "a\0b");
        AssertIoError(Call("filesystem.write_file_overwrite_atomically", Text("a"), PathOf(string.Empty)), "not_found", string.Empty);
        Assert.Empty(TemporaryFiles(workspace.Root));
    }

    [Fact]
    public void Fifo_destination_is_other_without_blocking()
    {
        using var workspace = new SystemTestWorkspace();
        var fifo = workspace.Combine("pipe");
        SystemRawFiles.MakeFifo(fifo);

        AssertIoError(Call("filesystem.write_file_overwrite_atomically", Text("a"), PathOf(fifo)), "other", fifo);
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemAppendFileTests
{
    [Fact]
    public void Creates_then_appends()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("log.txt");

        OkUnit(Call("filesystem.append_file", Text("a\n"), PathOf(path)));
        OkUnit(Call("filesystem.append_file", Text("é\n"), PathOf(path)));

        Assert.Equal("a\né\n", File.ReadAllText(path));
    }

    [Fact]
    public void Reports_directory_missing_parent_and_nul()
    {
        using var workspace = new SystemTestWorkspace();

        AssertIoError(Call("filesystem.append_file", Text("a"), PathOf(workspace.Root)), "is_a_directory", workspace.Root);
        AssertIoError(Call("filesystem.append_file", Text("a"), PathOf(workspace.Combine("no", "f"))), "not_found");
        AssertIoError(Call("filesystem.append_file", Text("a"), PathOf("x\0")), "invalid_path");
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemPathExistsTests
{
    [Fact]
    public void Follows_links_and_never_fails()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("f.txt", "x");
        var dangling = workspace.Combine("dangling");
        File.CreateSymbolicLink(dangling, workspace.Combine("nowhere"));
        var valid = workspace.Combine("valid");
        File.CreateSymbolicLink(valid, file);

        Assert.Equal(BooleanValue.True, Call("filesystem.path_exists", PathOf("/")));
        Assert.Equal(BooleanValue.True, Call("filesystem.path_exists", PathOf(file)));
        Assert.Equal(BooleanValue.True, Call("filesystem.path_exists", PathOf(workspace.Root)));
        Assert.Equal(BooleanValue.True, Call("filesystem.path_exists", PathOf(valid)));
        Assert.Equal(BooleanValue.False, Call("filesystem.path_exists", PathOf(dangling)));
        Assert.Equal(BooleanValue.False, Call("filesystem.path_exists", PathOf(workspace.Combine("missing"))));
        Assert.Equal(BooleanValue.False, Call("filesystem.path_exists", PathOf(string.Empty)));
        Assert.Equal(BooleanValue.False, Call("filesystem.path_exists", PathOf("a\0b")));
        Assert.Equal(BooleanValue.False, Call("filesystem.path_exists", PathOf(file + "/below")));
    }

    [Fact]
    public void Component_without_search_permission_is_false()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        var locked = workspace.Combine("locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(locked + "/f", "x");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        Assert.Equal(BooleanValue.False, Call("filesystem.path_exists", PathOf(locked + "/f")));
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemReadFileBoundedTests
{
    [Fact]
    public void Reads_whole_content_when_the_file_is_at_most_the_bound()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("note.txt", "café 😀");

        // Under the bound and exactly at the byte-count bound both return the whole file, byte for byte.
        Assert.Equal("café 😀", Ok<StringValue>(Call("filesystem.read_file_bounded", PathOf(file), Integer(64))).Text);
        Assert.Equal("café 😀", Ok<StringValue>(Call("filesystem.read_file_bounded", PathOf(file), Integer(10))).Text);
    }

    [Fact]
    public void Keeps_the_byte_order_mark_like_read_file()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("bom.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. "hello"u8.ToArray()]);

        Assert.Equal("\uFEFFhello", Ok<StringValue>(Call("filesystem.read_file_bounded", PathOf(path), Integer(10))).Text);
    }

    [Fact]
    public void A_file_holding_more_than_the_bound_is_too_large_with_the_provided_subject()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("note.txt", "hello"); // 5 bytes

        AssertIoError(Call("filesystem.read_file_bounded", PathOf(file), Integer(4)), "too_large", file);
        Assert.Equal("hello", Ok<StringValue>(Call("filesystem.read_file_bounded", PathOf(file), Integer(5))).Text);
    }

    [Fact]
    public void A_bound_that_splits_a_multibyte_character_reports_too_large_not_invalid_encoding()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("accent.txt", "é"); // 2 bytes: 0xC3 0xA9

        // The overflow byte decides before any decoding: the bound is on bytes, not on characters.
        AssertIoError(Call("filesystem.read_file_bounded", PathOf(file), Integer(1)), "too_large", file);
        Assert.Equal("é", Ok<StringValue>(Call("filesystem.read_file_bounded", PathOf(file), Integer(2))).Text);
    }

    [Fact]
    public void Zero_bound_accepts_an_empty_file_and_refuses_any_content()
    {
        using var workspace = new SystemTestWorkspace();
        var empty = workspace.Write("empty.txt", string.Empty);
        var full = workspace.Write("full.txt", "x");

        Assert.Equal(string.Empty, Ok<StringValue>(Call("filesystem.read_file_bounded", PathOf(empty), Integer(0))).Text);
        AssertIoError(Call("filesystem.read_file_bounded", PathOf(full), Integer(0)), "too_large", full);
    }

    [Fact]
    public void A_negative_bound_is_invalid_argument_with_the_provided_subject()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("note.txt", "x");

        AssertIoError(Call("filesystem.read_file_bounded", PathOf(file), Integer(-1)), "invalid_argument", file);
    }

    [Fact]
    public void A_negative_bound_is_rejected_before_a_nul_path_is_examined()
    {
        // The bound is validated first: an invalid argument wins over the invalid path.
        AssertIoError(Call("filesystem.read_file_bounded", PathOf("a\0b"), Integer(-1)), "invalid_argument", "a\0b");
        AssertIoError(Call("filesystem.read_file_bounded", PathOf("a\0b"), Integer(8)), "invalid_path", "a\0b");
    }

    [Fact]
    public void Missing_directory_and_component_errors_match_read_file()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("file.txt", "x");

        AssertIoError(Call("filesystem.read_file_bounded", PathOf(workspace.Combine("missing.txt")), Integer(8)), "not_found", workspace.Combine("missing.txt"));
        AssertIoError(Call("filesystem.read_file_bounded", PathOf(workspace.Root), Integer(8)), "is_a_directory", workspace.Root);
        AssertIoError(Call("filesystem.read_file_bounded", PathOf(file + "/below"), Integer(8)), "not_a_directory", file + "/below");
    }

    [Theory]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0xC0, 0x80 })]
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]
    [InlineData(new byte[] { 0x61, 0xE2, 0x82 })]
    public void Rejects_invalid_utf8_with_invalid_encoding(byte[] bytes)
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("bad.txt");
        File.WriteAllBytes(path, bytes);

        AssertIoError(Call("filesystem.read_file_bounded", PathOf(path), Integer(bytes.Length + 4)), "invalid_encoding", path);
    }

    [Fact]
    public void Fifo_is_other_without_blocking()
    {
        using var workspace = new SystemTestWorkspace();
        var fifo = workspace.Combine("pipe");
        SystemRawFiles.MakeFifo(fifo);

        AssertIoError(Call("filesystem.read_file_bounded", PathOf(fifo), Integer(8)), "other", fifo);
    }

    [Fact]
    public void An_enormous_bound_is_capped_and_a_sparse_file_above_the_limit_is_too_large()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("sparse.bin");
        using (var stream = File.Create(path))
        {
            stream.SetLength((1L << 28) + 1);
        }

        // The statx size refuses the file before anything is read or allocated, whatever the bound.
        AssertIoError(Call("filesystem.read_file_bounded", PathOf(path), Integer(long.MaxValue)), "too_large", path);
    }

    [Fact]
    public void A_file_reporting_size_zero_is_still_bounded_by_the_read_loop()
    {
        // procfs reports a size of 0 (filesystem.read_file's empty/procfs test relies on it): the pre-check cannot see the
        // content, so the read loop itself must stop at the overflow byte.
        const string ostype = "/proc/sys/kernel/ostype";
        var content = Ok<StringValue>(Call("filesystem.read_file_bounded", PathOf(ostype), Integer(64))).Text;

        Assert.Equal("Linux\n", content);
        AssertIoError(Call("filesystem.read_file_bounded", PathOf(ostype), Integer(content.Length - 1)), "too_large", ostype);
    }
}
