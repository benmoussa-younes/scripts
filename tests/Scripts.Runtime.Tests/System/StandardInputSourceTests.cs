using System.Runtime.InteropServices;
using Scripts.Runtime.Platform;
using Scripts.Runtime.Tests.SystemFunctions;

namespace Scripts.Runtime.Tests.SystemPlatform;

/// <summary>
/// <see cref="StandardInputSource"/> on the real file descriptor 0: every test <c>dup2</c>-s a pipe, a regular file or a
/// directory onto descriptor 0 and restores the previous descriptor in a <c>finally</c>. The tests are serialized by
/// <see cref="SystemTestCollection"/> (they change process-wide descriptors, like the other system tests).
/// </summary>
[Collection(SystemTestCollection.Name)]
public sealed partial class StandardInputSourceTests
{
    private static readonly List<string> TemporaryFiles = [];

    [Fact]
    public void Reads_the_exact_bytes_of_a_pipe_then_end_of_stream()
    {
        WithStandardInput("abc"u8.ToArray(), () =>
        {
            var result = StandardInputSource.Read(64);

            Assert.True(result.Succeeded, result.FailureReason);
            Assert.Equal("abc", System.Text.Encoding.UTF8.GetString(result.Bytes));
        });
    }

    [Fact]
    public void An_open_stream_nobody_writes_is_an_empty_script_after_the_read_timeout()
    {
        // `scripts run - <&-`: the .NET host may have put one of its own streams on descriptor 0 (a pipe nobody writes).
        // The read waits for a script, then gives up (DESIGN §9: a stream that delivers nothing is an empty script).
        Assert.Equal(0, Pipe(out var readDescriptor, out var writeDescriptor));
        try
        {
            WithDescriptor(readDescriptor, () =>
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                var result = RunWithDeadline(() => StandardInputSource.Read(64));

                Assert.True(result.Succeeded, result.FailureReason);
                Assert.Empty(result.Bytes);
                Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
            });
        }
        finally
        {
            CloseIfOpen(readDescriptor);
            CloseIfOpen(writeDescriptor);
        }
    }

    [Fact]
    public void A_non_blocking_stream_with_nothing_available_is_an_empty_script_without_spinning()
    {
        // A non-blocking descriptor answers EAGAIN at once. The reader must end there (DESIGN §9: a stream that
        // delivers nothing is an empty script), never retry in a loop: the call returns immediately.
        Assert.Equal(0, Pipe(out var readDescriptor, out var writeDescriptor));
        try
        {
            WithDescriptor(readDescriptor, () =>
            {
                // dup2 drops the file status flags: the mode is set on descriptor 0 once it points to the pipe.
                var flags = FileControl(0, GetStatusFlags, 0);
                Assert.True(FileControl(0, SetStatusFlags, flags | NonBlockingFlag) >= 0);
                var started = System.Diagnostics.Stopwatch.StartNew();
                var result = StandardInputSource.Read(64);

                Assert.True(result.Succeeded, result.FailureReason);
                Assert.Empty(result.Bytes);
                Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
            });
        }
        finally
        {
            CloseIfOpen(readDescriptor);
            CloseIfOpen(writeDescriptor);
        }
    }

    [Fact]
    public void A_regular_file_is_read_to_end_of_file()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Write("script.scs", "let x = 1\n");
        WithStandardInputFromFile(path, () =>
        {
            var result = StandardInputSource.Read(64);

            Assert.True(result.Succeeded, result.FailureReason);
            Assert.Equal("let x = 1\n", System.Text.Encoding.UTF8.GetString(result.Bytes));
        });
    }

    [Fact]
    public void The_runtime_random_device_is_not_a_script()
    {
        Assert.True(StandardInputSource.UseStandardInputFrom("/dev/urandom"), "cannot open /dev/urandom");
        try
        {
            // The real `scripts` passes Limits.MaximumSourceBytes + 1 as the cap.
            const int cap = (4 * 1024 * 1024) + 1;

            var result = RunWithDeadline(() => StandardInputSource.Read(cap));

            // A random device is not a script: an absent stream, never an endless read (DESIGN §9).
            Assert.True(result.Succeeded, result.FailureReason);
            Assert.Empty(result.Bytes);
        }
        finally
        {
            Assert.True(StandardInputSource.UseStandardInputFrom("/dev/null"), "cannot restore the standard input");
        }
    }

    [Fact]
    public void A_directory_yields_a_failure_reason()
    {
        WithStandardInputFromDirectory(() =>
        {
            var result = RunWithDeadline(() => StandardInputSource.Read(64));

            Assert.False(result.Succeeded);
            Assert.Equal("the standard input is a directory", result.FailureReason);
            Assert.Empty(result.Bytes);
        });
    }

    [Fact]
    public void Truncates_at_the_maximum_byte_count()
    {
        const int maximum = 16;
        var payload = Enumerable.Range(0, maximum * 4).Select(index => (byte)('a' + (index % 26))).ToArray();
        WithStandardInput(payload, () =>
        {
            var result = StandardInputSource.Read(maximum);

            Assert.True(result.Succeeded, result.FailureReason);
            Assert.Equal(payload[..maximum], result.Bytes);
        });
    }

    [Fact]
    public void A_source_of_exactly_the_maximum_is_read_completely()
    {
        var payload = Enumerable.Repeat((byte)'x', 32).ToArray();
        WithStandardInput(payload, () =>
        {
            var result = StandardInputSource.Read(payload.Length);

            Assert.True(result.Succeeded, result.FailureReason);
            Assert.Equal(payload, result.Bytes);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void A_small_maximum_returns_exactly_that_many_bytes(int maximum)
    {
        WithStandardInput("abcdef"u8.ToArray(), () =>
        {
            var result = StandardInputSource.Read(maximum);

            Assert.True(result.Succeeded, result.FailureReason);
            Assert.Equal(maximum, result.Bytes.Length);
        });
    }

    [Fact]
    public void A_negative_maximum_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StandardInputSource.Read(-1));
    }

    // ----------------------------------------------------------------- descriptor plumbing

    /// <summary>Makes <paramref name="payload"/> the content of descriptor 0 (a pipe whose write end is closed) for <paramref name="body"/>.</summary>
    private static void WithStandardInput(byte[] payload, Action body) => WithStandardInputFromFile(PathFor(payload), body);

    /// <summary>Makes the regular file at <paramref name="path"/> descriptor 0 for <paramref name="body"/>.</summary>
    private static void WithStandardInputFromFile(string path, Action body)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        WithDescriptor((int)stream.SafeFileHandle.DangerousGetHandle(), body);
    }

    /// <summary>
    /// Opens a directory with <c>open(2)</c> and makes it descriptor 0: <c>read(2)</c> then answers EISDIR. No managed
    /// handle is used (libc/open is the only way to get a directory descriptor here; it is a test-only binding).
    /// </summary>
    private static void WithStandardInputFromDirectory(Action body)
    {
        var readDescriptor = OpenDirectory(Path.GetTempPath(), 0);
        Assert.True(readDescriptor >= 0, "open(2) on a directory failed");
        try
        {
            WithDescriptor(readDescriptor, body);
        }
        finally
        {
            Close(readDescriptor);
        }
    }

    /// <summary>Writes the payload to a fresh temporary file (a regular file is read like a pipe, to end of file).</summary>
    private static string PathFor(byte[] payload)
    {
        var path = Path.Combine(Path.GetTempPath(), "scripts-stdin-" + Guid.NewGuid().ToString("N"));
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            stream.Write(payload);
        }

        TemporaryFiles.Add(path);
        return path;
    }

    /// <summary>
    /// Replaces descriptor 0 by <paramref name="descriptor"/> for <paramref name="body"/>, then restores the previous
    /// one. The prepared descriptor is first moved above 0: when the test host started with a closed standard input,
    /// a pipe or an <c>open(2)</c> can itself return 0, and <c>dup2(descriptor, 0)</c> would then close the very
    /// descriptor it is copying.
    /// </summary>
    private static void WithDescriptor(int descriptor, Action body)
    {
        // The previous descriptor 0 is saved above 0 as well: saving it as a low number could give the duplicate the
        // number of the prepared descriptor itself, which would then be closed with it.
        var previous = Dup(0);
        var saved = previous < 0 ? -1 : DuplicateAbove(previous);
        if (previous >= 0)
        {
            Close(previous);
        }

        var source = descriptor == 0 ? DuplicateAbove(0) : descriptor;
        var moved = source == descriptor ? -1 : source;
        try
        {
            Assert.True(previous >= 0, "the test host has no standard input to save");
            Assert.Equal(0, DuplicateOnto(source, 0));
            body();
        }
        finally
        {
            if (moved >= 0)
            {
                Close(moved);
            }

            if (saved >= 0)
            {
                Assert.Equal(0, DuplicateOnto(saved, 0));
                Close(saved);
            }
        }
    }

    /// <summary>Runs <paramref name="work"/> on a worker thread so that a regression which blocks becomes a failure, not a hang.</summary>
    private static T RunWithDeadline<T>(Func<T> work)
    {
        var task = Task.Run(work);
        Assert.True(task.Wait(TimeSpan.FromSeconds(20)), "StandardInputSource.Read blocked");
        return task.Result;
    }

    /// <summary>A duplicate of <paramref name="descriptor"/> above the standard streams (10 or more).</summary>
    private static int DuplicateAbove(int descriptor)
    {
        var duplicate = FcntlDuplicateFrom(descriptor, FcntlDuplicate, MinimumMovedDescriptor);
        Assert.True(duplicate >= 0, $"dup({descriptor}) above {MinimumMovedDescriptor} failed");
        return duplicate;
    }

    private const int MinimumMovedDescriptor = 10;

    private static int Duplicate(int descriptor)
    {
        var duplicate = Dup(descriptor);
        Assert.True(duplicate >= 0, $"dup({descriptor}) failed");
        return duplicate;
    }

    private static void Close(int descriptor) => Assert.Equal(0, CloseDescriptor(descriptor));

    /// <summary>Closes a descriptor that may already be closed (an identical descriptor was closed twice).</summary>
    private static void CloseIfOpen(int descriptor)
    {
        if (descriptor >= 0)
        {
            CloseDescriptor(descriptor);
        }
    }

    // Test-only libc bindings: `dup`/`dup2` are not in the production P/Invoke list (DESIGN §11) and a test must not
    // grow it. They only replace this process's descriptor 0 with a prepared regular file.
    [LibraryImport("libc", EntryPoint = "pipe", SetLastError = true)]
    private static partial int Pipe(out int readDescriptor, out int writeDescriptor);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int FileControl(int fileDescriptor, int command, int argument);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static partial long Write(int descriptor, ReadOnlySpan<byte> buffer, nuint count);

    private const int GetStatusFlags = 3;
    private const int SetStatusFlags = 4;
    private const int NonBlockingFlag = 0x800;

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    private static partial int OpenDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [LibraryImport("libc", EntryPoint = "dup2", SetLastError = true)]
    private static partial int DuplicateOnto(int source, int destination);

    [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static partial int Dup(int descriptor);

    /// <summary><c>fcntl(descriptor, F_DUPFD, minimumDescriptor)</c>: a duplicate at or above the minimum.</summary>
    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int FcntlDuplicateFrom(int descriptor, int command, int minimumDescriptor);

    private const int FcntlDuplicate = 0;

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseDescriptor(int descriptor);

    [Fact]
    public void A_descriptor_opened_with_close_on_exec_is_runtime_owned()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Write("fd.txt", "x");
        var nativePath = NativeFileSystem.ToNativePath(path);

        var flagged = Libc.Open(nativePath, Libc.OpenReadOnly | Libc.OpenCloseOnExec, 0);
        Assert.True(flagged >= 0);
        try
        {
            Assert.True(StandardInputSource.IsRuntimeOwnedDescriptor(flagged));
        }
        finally
        {
            Libc.Close(flagged);
        }

        var plain = Libc.Open(nativePath, Libc.OpenReadOnly, 0);
        Assert.True(plain >= 0);
        try
        {
            Assert.False(StandardInputSource.IsRuntimeOwnedDescriptor(plain));
        }
        finally
        {
            Libc.Close(plain);
        }
    }
}
