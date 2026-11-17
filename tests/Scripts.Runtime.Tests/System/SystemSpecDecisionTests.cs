using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>
/// Regression tests for two fixed specification decisions (S15, S16) that are easy to reintroduce:
/// S15 — `filesystem.copy_file*` propagates only the `0o777` permission bits, never setuid/setgid/sticky (like `cp` without `-p`).
/// S16 — whole-file writes refuse an existing FIFO or socket destination (error `other`) instead of blocking forever;
///       character devices stay writable.
/// </summary>
[Collection(SystemTestCollection.Name)]
public sealed class SystemSpecDecisionTests
{
    [Fact]
    public void Copying_does_not_propagate_setuid_setgid_or_sticky()
    {
        // S15: the source is 04755 (setuid); the copy must be 0755, never 04755.
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write("a.sh", "#!/bin/sh\n");
        File.SetUnixFileMode(source, (UnixFileMode)Convert.ToInt32("4755", 8));
        Assert.Equal((UnixFileMode)Convert.ToInt32("4755", 8), SystemRawFiles.ModeOf(source));

        var created = workspace.Combine("b.sh");
        OkUnit(Call("filesystem.copy_file", PathOf(source), PathOf(created)));
        Assert.Equal((UnixFileMode)Convert.ToInt32("755", 8), SystemRawFiles.ModeOf(created));
        Assert.False(SystemRawFiles.ModeOf(created).HasFlag(UnixFileMode.SetUser));

        var overwritten = workspace.Combine("c.sh");
        OkUnit(Call("filesystem.copy_file_overwrite", PathOf(source), PathOf(overwritten)));
        Assert.Equal((UnixFileMode)Convert.ToInt32("755", 8), SystemRawFiles.ModeOf(overwritten));
        Assert.False(SystemRawFiles.ModeOf(overwritten).HasFlag(UnixFileMode.SetUser));

        // An existing destination (filesystem.copy_file_overwrite) keeps its own permissions, with no setuid added.
        var existing = workspace.Write("d.sh", "old");
        File.SetUnixFileMode(existing, (UnixFileMode)Convert.ToInt32("644", 8));
        OkUnit(Call("filesystem.copy_file_overwrite", PathOf(source), PathOf(existing)));
        Assert.Equal((UnixFileMode)Convert.ToInt32("644", 8), SystemRawFiles.ModeOf(existing));
    }

    [Fact]
    public void CopyDirectoryTree_does_not_propagate_setuid_setgid_or_sticky()
    {
        // S15: the tree copy follows filesystem.copy_file, not `cp -a`: only the 0o777 bits are propagated.
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Combine("source");
        Directory.CreateDirectory(source);

        var setuidFile = workspace.Write("source/setuid.sh", "#!/bin/sh\n");
        File.SetUnixFileMode(setuidFile, (UnixFileMode)Convert.ToInt32("4755", 8));
        var setgidDirectory = workspace.Combine("source", "group");
        Directory.CreateDirectory(setgidDirectory);
        File.SetUnixFileMode(setgidDirectory, (UnixFileMode)Convert.ToInt32("2775", 8));
        var stickyFile = workspace.Write("source/sticky.sh", "#!/bin/sh\n");
        File.SetUnixFileMode(stickyFile, (UnixFileMode)Convert.ToInt32("1755", 8));
        var ordinaryDirectory = workspace.Combine("source", "ordinary");
        Directory.CreateDirectory(ordinaryDirectory);
        File.SetUnixFileMode(ordinaryDirectory, (UnixFileMode)Convert.ToInt32("750", 8));
        var ordinaryFile = workspace.Write("source/plain.txt", "data");
        File.SetUnixFileMode(ordinaryFile, (UnixFileMode)Convert.ToInt32("640", 8));
        var stickyDirectory = workspace.Combine("source", "shared");
        Directory.CreateDirectory(stickyDirectory);
        File.SetUnixFileMode(stickyDirectory, (UnixFileMode)Convert.ToInt32("1777", 8));
        var privateFile = workspace.Write("source/private.txt", "secret");
        File.SetUnixFileMode(privateFile, (UnixFileMode)Convert.ToInt32("600", 8));
        var readOnlyDirectory = workspace.Combine("source", "locked");
        workspace.Write("source/locked/child.txt", "locked");
        File.SetUnixFileMode(readOnlyDirectory, (UnixFileMode)Convert.ToInt32("555", 8));

        // The fixture really carries the special bits before the copy.
        Assert.Equal((UnixFileMode)Convert.ToInt32("4755", 8), SystemRawFiles.ModeOf(setuidFile));
        Assert.Equal((UnixFileMode)Convert.ToInt32("2775", 8), SystemRawFiles.ModeOf(setgidDirectory));
        Assert.Equal((UnixFileMode)Convert.ToInt32("1755", 8), SystemRawFiles.ModeOf(stickyFile));
        Assert.Equal((UnixFileMode)Convert.ToInt32("1777", 8), SystemRawFiles.ModeOf(stickyDirectory));
        Assert.Equal((UnixFileMode)Convert.ToInt32("555", 8), SystemRawFiles.ModeOf(readOnlyDirectory));

        var destination = workspace.Combine("copy");
        OkUnit(Call("filesystem.copy_directory_tree", PathOf(source), PathOf(destination)));

        Assert.Equal((UnixFileMode)Convert.ToInt32("755", 8), SystemRawFiles.ModeOf(Path.Combine(destination, "setuid.sh")));
        Assert.False(SystemRawFiles.ModeOf(Path.Combine(destination, "setuid.sh")).HasFlag(UnixFileMode.SetUser));
        Assert.Equal((UnixFileMode)Convert.ToInt32("775", 8), SystemRawFiles.ModeOf(Path.Combine(destination, "group")));
        Assert.False(SystemRawFiles.ModeOf(Path.Combine(destination, "group")).HasFlag(UnixFileMode.SetGroup));
        Assert.Equal((UnixFileMode)Convert.ToInt32("755", 8), SystemRawFiles.ModeOf(Path.Combine(destination, "sticky.sh")));
        Assert.False(SystemRawFiles.ModeOf(Path.Combine(destination, "sticky.sh")).HasFlag(UnixFileMode.StickyBit));
        Assert.Equal((UnixFileMode)Convert.ToInt32("777", 8), SystemRawFiles.ModeOf(Path.Combine(destination, "shared")));
        Assert.False(SystemRawFiles.ModeOf(Path.Combine(destination, "shared")).HasFlag(UnixFileMode.StickyBit));

        // The ordinary permission bits are preserved and the tree still holds the same entries and contents.
        Assert.Equal((UnixFileMode)Convert.ToInt32("750", 8), SystemRawFiles.ModeOf(Path.Combine(destination, "ordinary")));
        Assert.Equal((UnixFileMode)Convert.ToInt32("640", 8), SystemRawFiles.ModeOf(Path.Combine(destination, "plain.txt")));
        Assert.Equal((UnixFileMode)Convert.ToInt32("600", 8), SystemRawFiles.ModeOf(Path.Combine(destination, "private.txt")));
        Assert.Equal((UnixFileMode)Convert.ToInt32("555", 8), SystemRawFiles.ModeOf(Path.Combine(destination, "locked")));
        Assert.Equal("data", File.ReadAllText(Path.Combine(destination, "plain.txt")));
        Assert.Equal("secret", File.ReadAllText(Path.Combine(destination, "private.txt")));
        Assert.Equal("locked", File.ReadAllText(Path.Combine(destination, "locked", "child.txt")));
        Assert.Equal("#!/bin/sh\n", File.ReadAllText(Path.Combine(destination, "sticky.sh")));
        Assert.Equal(
            [.. Directory.GetFileSystemEntries(source).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal)],
            [.. Directory.GetFileSystemEntries(destination).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void CopyDirectoryTree_masks_the_destination_root_mode()
    {
        // S15: the destination root receives sourceMode & 0o777, never setuid/setgid/sticky.
        using var workspace = new SystemTestWorkspace();

        var setgidSource = workspace.Combine("setgid-source");
        Directory.CreateDirectory(setgidSource);
        workspace.Write("setgid-source/a.txt", "one");
        File.SetUnixFileMode(setgidSource, (UnixFileMode)Convert.ToInt32("2775", 8));
        Assert.Equal((UnixFileMode)Convert.ToInt32("2775", 8), SystemRawFiles.ModeOf(setgidSource));

        var setgidDestination = workspace.Combine("setgid-copy");
        OkUnit(Call("filesystem.copy_directory_tree", PathOf(setgidSource), PathOf(setgidDestination)));
        Assert.Equal((UnixFileMode)Convert.ToInt32("775", 8), SystemRawFiles.ModeOf(setgidDestination));
        Assert.False(SystemRawFiles.ModeOf(setgidDestination).HasFlag(UnixFileMode.SetGroup));
        Assert.Equal("one", File.ReadAllText(Path.Combine(setgidDestination, "a.txt")));

        var stickySource = workspace.Combine("sticky-source");
        Directory.CreateDirectory(stickySource);
        workspace.Write("sticky-source/b.txt", "two");
        File.SetUnixFileMode(stickySource, (UnixFileMode)Convert.ToInt32("1777", 8));
        Assert.Equal((UnixFileMode)Convert.ToInt32("1777", 8), SystemRawFiles.ModeOf(stickySource));

        var stickyDestination = workspace.Combine("sticky-copy");
        OkUnit(Call("filesystem.copy_directory_tree", PathOf(stickySource), PathOf(stickyDestination)));
        Assert.Equal((UnixFileMode)Convert.ToInt32("777", 8), SystemRawFiles.ModeOf(stickyDestination));
        Assert.False(SystemRawFiles.ModeOf(stickyDestination).HasFlag(UnixFileMode.StickyBit));
        Assert.Equal("two", File.ReadAllText(Path.Combine(stickyDestination, "b.txt")));
    }

    [Fact]
    public void Setgid_and_sticky_are_not_propagated_either()
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write("s", "x");
        File.SetUnixFileMode(source, (UnixFileMode)Convert.ToInt32("2750", 8));
        var destination = workspace.Combine("t");

        OkUnit(Call("filesystem.copy_file", PathOf(source), PathOf(destination)));

        Assert.Equal((UnixFileMode)Convert.ToInt32("750", 8), SystemRawFiles.ModeOf(destination));
        Assert.False(SystemRawFiles.ModeOf(destination).HasFlag(UnixFileMode.SetGroup));
    }

    [Fact]
    public void Write_and_append_to_a_fifo_are_other_without_blocking()
    {
        // S16: no reader is connected to the FIFO; without a refusal, opening for writing would block forever.
        using var workspace = new SystemTestWorkspace();
        var fifo = workspace.Combine("pipe");
        SystemRawFiles.MakeFifo(fifo);

        AssertIoErrorWithin(5_000, () => Call("filesystem.write_file_overwrite", Text("x"), PathOf(fifo)), "other", fifo);
        AssertIoErrorWithin(5_000, () => Call("filesystem.append_file", Text("x"), PathOf(fifo)), "other", fifo);

        // The FIFO remains a special file: nothing was written and it still exists.
        Assert.True(File.Exists(fifo));
    }

    [Fact]
    public void Copy_file_overwrite_to_a_fifo_is_other_without_blocking()
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write("source", "content");
        var fifo = workspace.Combine("pipe");
        SystemRawFiles.MakeFifo(fifo);

        AssertIoErrorWithin(5_000, () => Call("filesystem.copy_file_overwrite", PathOf(source), PathOf(fifo)), "other", fifo);
        Assert.Equal("content", File.ReadAllText(source));
    }

    [Fact]
    public void Fifo_destination_of_copy_file_without_overwrite_is_already_exists()
    {
        // The documented evaluation order wins: an existing destination (even a FIFO) yields `already_exists` for `filesystem.copy_file`.
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Write("source", "content");
        var fifo = workspace.Combine("pipe");
        SystemRawFiles.MakeFifo(fifo);

        AssertIoErrorWithin(5_000, () => Call("filesystem.copy_file", PathOf(source), PathOf(fifo)), "already_exists", fifo);
        Assert.Equal("content", File.ReadAllText(source));
    }

    [Fact]
    public void Character_devices_remain_writable()
    {
        // S16: only FIFOs and sockets are refused; character devices remain allowed.
        OkUnit(Call("filesystem.write_file_overwrite", Text("discarded"), PathOf("/dev/null")));
        OkUnit(Call("filesystem.append_file", Text("discarded"), PathOf("/dev/null")));

        // `/dev/full` accepts the open but fails on write: `other`, with the destination as subject.
        AssertIoError(Call("filesystem.write_file_overwrite", Text("x"), PathOf("/dev/full")), "other", "/dev/full");
    }

    /// <summary>
    /// Runs <paramref name="call"/> on a background thread and fails if it does not finish within
    /// <paramref name="timeoutMilliseconds"/>, so a regression that blocks forever fails as a timeout instead of
    /// hanging the whole test run.
    /// </summary>
    private static void AssertIoErrorWithin(int timeoutMilliseconds, Func<Value> call, string kind, string? subject = null)
    {
        Value? result = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                result = call();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
        };
        worker.Start();

        Assert.True(worker.Join(timeoutMilliseconds), $"the call blocked for more than {timeoutMilliseconds} ms");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException("the call failed: " + failure);
        }

        AssertIoError(Assert.IsAssignableFrom<Value>(result), kind, subject);
    }
}
