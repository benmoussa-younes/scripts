using System.Runtime.InteropServices;
using Scripts.Runtime.Platform;
using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>
/// The exact-creation-mode entries (STD-006a): the requested bits are applied exactly, the transient
/// mode is never more permissive, and an out-of-domain mode is refused before any system call.
/// </summary>
[Collection(SystemTestCollection.Name)]
public sealed partial class SystemFilePermissionTests
{
    [Fact]
    public void A_file_is_created_with_the_exact_mode_under_a_restrictive_umask()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("secret.txt");
        using var context = NewContext();

        WithProcessUmask(0x3F, () => OkUnit(Call(
            context,
            "filesystem.write_file_exclusive_with_permissions",
            Text("secret"),
            PathOf(path),
            Integer(0x1A0))));

        var status = Ok<RecordValue>(Call(context, "filesystem.get_path_status", PathOf(path)));
        Assert.Equal(416L, status.GetInteger("permissions"));
        Assert.Equal("secret", File.ReadAllText(path));
    }

    [Fact]
    public void A_0600_file_has_no_group_or_other_bits_under_a_restrictive_umask()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("private.txt");
        using var context = NewContext();

        WithProcessUmask(0x3F, () => OkUnit(Call(
            context,
            "filesystem.write_file_exclusive_with_permissions",
            Text("private"),
            PathOf(path),
            Integer(0x180))));

        var status = Ok<RecordValue>(Call(context, "filesystem.get_path_status", PathOf(path)));
        Assert.Equal(384L, status.GetInteger("permissions"));
    }

    [Fact]
    public void A_directory_is_created_with_the_exact_mode_under_a_restrictive_umask()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("private");
        using var context = NewContext();

        WithProcessUmask(0x3F, () => OkUnit(Call(
            context,
            "filesystem.make_directory_with_permissions",
            PathOf(path),
            Integer(0x1E8))));

        var status = Ok<RecordValue>(Call(context, "filesystem.get_path_status", PathOf(path)));
        Assert.Equal(488L, status.GetInteger("permissions"));
    }

    [Fact]
    public void A_umask_that_removes_owner_access_fails_closed_and_leaves_no_directory()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            throw Xunit.Sdk.SkipException.ForSkip("a root process bypasses the DAC check, so permission_denied is not observable");
        }

        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("private");
        using var context = NewContext();

        WithProcessUmask(0x1C0, () => AssertIoError(
            Call(context, "filesystem.make_directory_with_permissions", PathOf(path), Integer(0x1E8)),
            "permission_denied",
            path));

        var exists = Assert.IsType<BooleanValue>(Call(context, "filesystem.path_exists", PathOf(path)));
        Assert.False(exists.Flag);
    }

    [Fact]
    public void A_request_without_owner_read_fails_closed_and_leaves_no_directory()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            throw Xunit.Sdk.SkipException.ForSkip("a root process bypasses the DAC check, so permission_denied is not observable");
        }

        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("private");
        using var context = NewContext();

        WithProcessUmask(0, () => AssertIoError(
            Call(context, "filesystem.make_directory_with_permissions", PathOf(path), Integer(0x0C0)),
            "permission_denied",
            path));

        var exists = Assert.IsType<BooleanValue>(Call(context, "filesystem.path_exists", PathOf(path)));
        Assert.False(exists.Flag);
    }

    [Fact]
    public void A_trailing_slash_does_not_follow_a_final_symbolic_link_before_fchmod()
    {
        using var workspace = new SystemTestWorkspace();
        var targetPath = workspace.Combine("target");
        Directory.CreateDirectory(targetPath);
        File.SetUnixFileMode(targetPath, (UnixFileMode)0x1E8);
        var linkPath = workspace.Combine("link");
        Directory.CreateDirectory(linkPath);
        Directory.Delete(linkPath);
        File.CreateSymbolicLink(linkPath, targetPath);

        var statusError = NativeFileSystem.TryGetStatus(NativeFileSystem.ToNativePath(targetPath), followLinks: false, out var targetStatus);
        Assert.Equal(0, statusError);

        IoFailureException? failure = null;
        try
        {
            FileSystemMutations.ApplyDirectoryModeFromPath(linkPath + "/", 0x1C0, linkPath + "/", targetStatus);
        }
        catch (IoFailureException exception)
        {
            failure = exception;
        }

        // Without the trim, open("link/") follows the target and fchmod changes it: this assertion fails first.
        Assert.Equal((UnixFileMode)0x1E8, File.GetUnixFileMode(targetPath));
        Assert.NotNull(failure);
        Assert.Equal("invalid_path", failure!.Kind);
        Assert.NotNull(File.ResolveLinkTarget(linkPath, returnFinalTarget: false));
    }

    [Fact]
    public void A_different_directory_opened_with_the_expected_status_is_invalid_path()
    {
        using var workspace = new SystemTestWorkspace();
        var firstPath = workspace.Combine("first");
        Directory.CreateDirectory(firstPath);
        var secondPath = workspace.Combine("second");
        Directory.CreateDirectory(secondPath);
        File.SetUnixFileMode(secondPath, (UnixFileMode)0x1E8);

        var statusError = NativeFileSystem.TryGetStatus(
            NativeFileSystem.ToNativePath(firstPath),
            followLinks: false,
            out var firstStatus);
        Assert.Equal(0, statusError);

        var failure = Assert.Throws<IoFailureException>(() =>
            FileSystemMutations.ApplyDirectoryModeFromPath(secondPath, 0x1C0, secondPath, firstStatus));

        Assert.Equal("invalid_path", failure.Kind);
        Assert.Equal((UnixFileMode)0x1E8, File.GetUnixFileMode(secondPath));
    }

    [Fact]
    public void An_out_of_domain_file_mode_is_invalid_argument_before_anything_is_created()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("refused.txt");
        using var context = NewContext();

        AssertIoError(
            Call(context, "filesystem.write_file_exclusive_with_permissions", Text("x"), PathOf(path), Integer(0x200)),
            "invalid_argument",
            path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void An_out_of_domain_directory_mode_is_invalid_argument_before_anything_is_created()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("refused");
        using var context = NewContext();

        AssertIoError(
            Call(context, "filesystem.make_directory_with_permissions", PathOf(path), Integer(0x200)),
            "invalid_argument",
            path);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void An_existing_file_or_final_symlink_is_already_exists_and_untouched()
    {
        using var workspace = new SystemTestWorkspace();
        var existingPath = workspace.Write("existing.txt", "keep");
        var linkTarget = workspace.Combine("absent-target.txt");
        var linkPath = workspace.Combine("link.txt");
        File.CreateSymbolicLink(linkPath, linkTarget);
        using var context = NewContext();

        AssertIoError(
            Call(context, "filesystem.write_file_exclusive_with_permissions", Text("new"), PathOf(existingPath), Integer(0x180)),
            "already_exists",
            existingPath);
        Assert.Equal("keep", File.ReadAllText(existingPath));

        AssertIoError(
            Call(context, "filesystem.write_file_exclusive_with_permissions", Text("new"), PathOf(linkPath), Integer(0x180)),
            "already_exists",
            linkPath);
        Assert.False(File.Exists(linkTarget), "the symbolic-link target must not be created");
    }

    [Fact]
    public void Cleanup_removes_the_created_entry_and_returns_the_original_error()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Write("created.txt", "x");
        var statusError = NativeFileSystem.TryGetStatus(NativeFileSystem.ToNativePath(path), followLinks: false, out var status);
        Assert.Equal(0, statusError);
        var original = new IoFailureException("permission_denied", path, "permission denied");

        var returned = FileSystemMutations.RemoveCreatedEntryOrOriginal(
            original,
            NativeFileSystem.ToNativePath(path),
            path,
            status,
            directory: false);

        Assert.Same(original, returned);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Cleanup_leaves_a_replacement_entry_untouched_and_returns_the_original_error()
    {
        using var workspace = new SystemTestWorkspace();
        var createdPath = workspace.Write("created.txt", "x");
        var replacementPath = workspace.Write("replacement.txt", "y");
        var statusError = NativeFileSystem.TryGetStatus(NativeFileSystem.ToNativePath(createdPath), followLinks: false, out var status);
        Assert.Equal(0, statusError);
        File.Move(replacementPath, createdPath, overwrite: true);
        var original = new IoFailureException("permission_denied", createdPath, "permission denied");

        var returned = FileSystemMutations.RemoveCreatedEntryOrOriginal(
            original,
            NativeFileSystem.ToNativePath(createdPath),
            createdPath,
            status,
            directory: false);

        Assert.Same(original, returned);
        Assert.True(File.Exists(createdPath));
        Assert.Equal("y", File.ReadAllText(createdPath));
        Assert.False(File.Exists(replacementPath));
    }

    [Fact]
    public void Cleanup_of_an_already_gone_path_returns_the_original_error()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Write("gone.txt", "x");
        var statusError = NativeFileSystem.TryGetStatus(NativeFileSystem.ToNativePath(path), followLinks: false, out var status);
        Assert.Equal(0, statusError);
        File.Delete(path);
        var original = new IoFailureException("permission_denied", path, "permission denied");

        var returned = FileSystemMutations.RemoveCreatedEntryOrOriginal(
            original,
            NativeFileSystem.ToNativePath(path),
            path,
            status,
            directory: false);

        Assert.Same(original, returned);
    }

    [Fact]
    public void Cleanup_of_a_non_empty_directory_keeps_the_original_error_and_names_the_remaining_path()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("busy");
        Directory.CreateDirectory(path);
        workspace.Write("busy/child.txt", "x");
        var statusError = NativeFileSystem.TryGetStatus(NativeFileSystem.ToNativePath(path), followLinks: false, out var status);
        Assert.Equal(0, statusError);
        var original = new IoFailureException("permission_denied", path, "permission denied");

        var returned = FileSystemMutations.RemoveCreatedEntryOrOriginal(
            original,
            NativeFileSystem.ToNativePath(path),
            path,
            status,
            directory: true);

        Assert.Equal(original.Kind, returned.Kind);
        Assert.Equal(original.Subject, returned.Subject);
        Assert.Contains("may remain at", returned.Message, StringComparison.Ordinal);
        Assert.Contains(path, returned.Reason, StringComparison.Ordinal);
        Assert.True(Directory.Exists(path));
    }

    private static void WithProcessUmask(uint mask, Action body)
    {
        var previous = SetProcessUmask(mask);
        try
        {
            body();
        }
        finally
        {
            SetProcessUmask(previous);
        }
    }

    // Test-only libc binding: `umask` is not in the production P/Invoke list.
    [LibraryImport("libc", EntryPoint = "umask")]
    private static partial uint SetProcessUmask(uint mask);
}
