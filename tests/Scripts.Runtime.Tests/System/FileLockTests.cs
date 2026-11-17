using System.Runtime.InteropServices;
using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

[Collection(SystemTestCollection.Name)]
public sealed partial class FileLockTests
{
    [Fact]
    public void Acquire_creates_the_lock_file_with_mode_0600_and_release_closes_the_descriptor()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        var lockPath = workspace.Combine("state.lock");
        using var context = NewContext();

        var lockValue = Ok<FileLockValue>(
            Call(context, "filesystem.acquire_file_lock", PathOf(lockPath), Integer(0)));
        Assert.True(File.Exists(lockPath));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(lockPath));
        Assert.False(lockValue.IsReleased);

        OkUnit(Call(context, "file_lock.release", lockValue));
        Assert.True(lockValue.IsReleased);
        Assert.True(File.Exists(lockPath), "releasing the lock must not remove the lock file");
    }

    [Fact]
    public void A_second_acquire_times_out_and_succeeds_after_release()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        var lockPath = workspace.Combine("state.lock");
        using var context = NewContext();

        var first = Ok<FileLockValue>(
            Call(context, "filesystem.acquire_file_lock", PathOf(lockPath), Integer(0)));

        AssertIoError(
            Call(context, "filesystem.acquire_file_lock", PathOf(lockPath), Integer(50)),
            "timed_out",
            lockPath);

        OkUnit(Call(context, "file_lock.release", first));
        var second = Ok<FileLockValue>(
            Call(context, "filesystem.acquire_file_lock", PathOf(lockPath), Integer(50)));
        OkUnit(Call(context, "file_lock.release", second));
    }

    [Fact]
    public void A_negative_timeout_is_invalid_argument_before_the_lock_file_is_created()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        var lockPath = workspace.Combine("state.lock");
        using var context = NewContext();

        AssertIoError(
            Call(context, "filesystem.acquire_file_lock", PathOf(lockPath), Integer(-1)),
            "invalid_argument",
            lockPath);
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public void Releasing_twice_is_ok()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        var lockPath = workspace.Combine("state.lock");
        using var context = NewContext();

        var lockValue = Ok<FileLockValue>(
            Call(context, "filesystem.acquire_file_lock", PathOf(lockPath), Integer(0)));
        OkUnit(Call(context, "file_lock.release", lockValue));
        OkUnit(Call(context, "file_lock.release", lockValue));
    }

    [Fact]
    public void A_symbolic_link_lock_path_is_not_followed_and_the_target_is_not_created()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        var targetPath = workspace.Combine("absent-target");
        var linkPath = workspace.Combine("state.lock");
        File.CreateSymbolicLink(linkPath, targetPath);
        using var context = NewContext();

        AssertIoError(
            Call(context, "filesystem.acquire_file_lock", PathOf(linkPath), Integer(0)),
            "invalid_path",
            linkPath);
        Assert.False(File.Exists(targetPath), "the symbolic link target must not be created");
    }

    [Fact]
    public void A_file_lock_has_no_JSON_form()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        var lockPath = workspace.Combine("state.lock");
        using var context = NewContext();
        var lockValue = Ok<FileLockValue>(
            Call(context, "filesystem.acquire_file_lock", PathOf(lockPath), Integer(0)));

        AssertIoError(Call(context, "json.encode", lockValue), "not_encodable");

        OkUnit(Call(context, "file_lock.release", lockValue));
    }

    [Fact]
    public void Lock_file_mode_is_0600_under_umask_0777()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        var lockPath = workspace.Combine("state.lock");
        using var context = NewContext();

        var lockValue = WithProcessUmask(0x1FF, () =>
            Ok<FileLockValue>(Call(context, "filesystem.acquire_file_lock", PathOf(lockPath), Integer(0))));

        var status = Ok<RecordValue>(Call(context, "filesystem.get_path_status", PathOf(lockPath)));
        Assert.Equal(384L, status.GetInteger("permissions"));
        OkUnit(Call(context, "file_lock.release", lockValue));
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

    // Test-only libc binding: `umask` is not in the production P/Invoke list.
    [LibraryImport("libc", EntryPoint = "umask")]
    private static partial uint SetProcessUmask(uint mask);
}
