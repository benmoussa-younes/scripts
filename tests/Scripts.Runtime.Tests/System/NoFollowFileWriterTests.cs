using System.Runtime.InteropServices;
using Scripts.Runtime.Platform;

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>
/// The no-follow writer used by <c>check --fix</c>: it opens the final inode in place,
/// refuses a final symbolic link (dangling included), and never follows one. The pre-check classifies without
/// following; the write open is the decisive check.
/// </summary>
[Collection(SystemTestCollection.Name)]
public sealed partial class NoFollowFileWriterTests
{
    private const int GetFileDescriptorFlags = 1;

    [Fact]
    public void Opens_and_truncates_a_regular_file_in_place_and_preserves_its_mode()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Write("regular.txt", "old content");
        var mode = (UnixFileMode)Convert.ToInt32("640", 8);
        File.SetUnixFileMode(path, mode);

        using (var stream = NoFollowFileWriter.OpenAndTruncate(path))
        {
            stream.Write("new content"u8);
        }

        using (var stream = NoFollowFileWriter.OpenAndTruncate(path + "/"))
        {
            stream.Write("slash content"u8);
        }

        Assert.False(NoFollowFileWriter.IsFinalSymbolicLink(path + "/"));
        Assert.Equal("slash content", File.ReadAllText(path));
        Assert.Equal(mode, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Refuses_a_final_symbolic_link_and_leaves_its_target_untouched()
    {
        using var workspace = new SystemTestWorkspace();
        var victim = workspace.Write("victim.txt", "victim content");
        var link = workspace.Combine("link.txt");
        File.CreateSymbolicLink(link, victim);

        var exception = Assert.Throws<IOException>(() => NoFollowFileWriter.OpenAndTruncate(link));

        Assert.Equal(NoFollowFileWriter.SymbolicLinkReason, exception.Message);
        Assert.True(NoFollowFileWriter.IsFinalSymbolicLink(link));
        Assert.True(NoFollowFileWriter.IsFinalSymbolicLink(link + "/"));
        Assert.Equal(NoFollowFileWriter.SymbolicLinkReason, Assert.Throws<IOException>(
            () => NoFollowFileWriter.OpenAndTruncate(link + "/")).Message);
        Assert.Equal("victim content", File.ReadAllText(victim));
        Assert.Equal(victim, new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void Refuses_a_dangling_final_symbolic_link_without_creating_its_target()
    {
        using var workspace = new SystemTestWorkspace();
        var missing = workspace.Combine("missing.txt");
        var link = workspace.Combine("dangling.txt");
        File.CreateSymbolicLink(link, missing);

        var exception = Assert.Throws<IOException>(() => NoFollowFileWriter.OpenCreateAndTruncate(link));

        Assert.Equal(NoFollowFileWriter.SymbolicLinkReason, exception.Message);
        Assert.True(NoFollowFileWriter.IsFinalSymbolicLink(link));
        Assert.True(NoFollowFileWriter.IsFinalSymbolicLink(link + "/"));
        Assert.Equal(NoFollowFileWriter.SymbolicLinkReason, Assert.Throws<IOException>(
            () => NoFollowFileWriter.OpenCreateAndTruncate(link + "/")).Message);
        Assert.False(File.Exists(missing));
        Assert.Equal(missing, new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void Classifies_a_regular_file_a_missing_path_a_directory_link_and_a_file_link()
    {
        using var workspace = new SystemTestWorkspace();
        var regular = workspace.Write("regular.txt", "regular");
        var missing = workspace.Combine("missing.txt");
        var target = Path.Combine(workspace.Root, "directory");
        Directory.CreateDirectory(target);
        var directoryLink = workspace.Combine("directory-link");
        File.CreateSymbolicLink(directoryLink, target);
        var fileLink = workspace.Combine("file-link");
        File.CreateSymbolicLink(fileLink, regular);

        Assert.False(NoFollowFileWriter.IsFinalSymbolicLink(regular));
        Assert.False(NoFollowFileWriter.IsFinalSymbolicLink(regular + "/"));
        Assert.False(NoFollowFileWriter.IsFinalSymbolicLink(missing));
        Assert.True(NoFollowFileWriter.IsFinalSymbolicLink(directoryLink));
        Assert.True(NoFollowFileWriter.IsFinalSymbolicLink(directoryLink + "/"));
        Assert.True(NoFollowFileWriter.IsFinalSymbolicLink(fileLink));
        Assert.True(NoFollowFileWriter.IsFinalSymbolicLink(fileLink + "/"));
    }

    [Fact]
    public void Creates_with_0666_minus_a_restrictive_umask()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("created.txt");

        var previous = SetProcessUmask(0x3F);
        try
        {
            using (var stream = NoFollowFileWriter.OpenCreateAndTruncate(path))
            {
                stream.Write("created"u8);
            }
        }
        finally
        {
            SetProcessUmask(previous);
        }

        Assert.Equal((UnixFileMode)Convert.ToInt32("600", 8), File.GetUnixFileMode(path));
        Assert.Equal("created", File.ReadAllText(path));
    }

    [Fact]
    public void The_returned_stream_owns_and_closes_the_descriptor()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Write("owned.txt", "owned");

        var stream = NoFollowFileWriter.OpenAndTruncate(path);
        var descriptor = (int)stream.SafeFileHandle.DangerousGetHandle();
        stream.Dispose();

        // F_GETFD on the closed descriptor fails with EBADF: the SafeFileHandle, not a second Libc.Close, owned it.
        Assert.Equal(-1, Libc.FileControl(descriptor, GetFileDescriptorFlags, 0));
        Assert.Equal(Errno.BadFileDescriptor, Marshal.GetLastPInvokeError());
    }

    // Test-only libc binding: `umask` is not in the production P/Invoke list.
    [LibraryImport("libc", EntryPoint = "umask")]
    private static partial uint SetProcessUmask(uint mask);
}
