using System.Text;
using Scripts.Runtime.Platform;

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>
/// P8d: the native launch is a direct <c>posix_spawn</c> with an explicit <c>envp</c>, a real process group and
/// <c>waitpid</c>. These tests exercise the primitive without the interpreter, so a regression is localized.
/// </summary>
[Collection(SystemTestCollection.Name)]
public sealed class PosixSpawnTests
{
    private static readonly byte[] MinimalEnvironment = EnvironmentBlock(
        ("PATH", "/usr/bin:/bin"),
        ("SCRIPTS_SPAWN_TEST", "yes"));

    [Fact]
    public void A_child_receives_its_arguments_verbatim_and_its_exit_code()
    {
        var child = Launch("/usr/bin/printf", ["%s|%s", "a b", "é😀"], redirectInput: false);

        Assert.Equal("a b|é😀", ReadToEnd(child.StandardOutput!));
        Assert.Equal(0, Wait(child));
    }

    [Fact]
    public void A_child_is_placed_in_its_own_process_group()
    {
        var child = Launch("/bin/sleep", ["2"]);

        Assert.Equal(child.ProcessIdentifier, Libc.GetProcessGroup(child.ProcessIdentifier));
        Assert.Equal(0, Libc.Kill(child.ProcessIdentifier, Libc.SignalKill));
        Assert.Equal(137, Wait(child));
    }

    [Fact]
    public void A_child_without_the_group_flag_keeps_the_group_of_bcs()
    {
        var child = Launch("/bin/sleep", ["2"], ownProcessGroup: false);

        Assert.Equal(Libc.GetProcessGroup(Environment.ProcessId), Libc.GetProcessGroup(child.ProcessIdentifier));
        Assert.Equal(0, Libc.Kill(child.ProcessIdentifier, Libc.SignalKill));
        Assert.Equal(137, Wait(child));
    }

    [Fact]
    public void The_working_directory_is_applied()
    {
        var child = Launch("/bin/pwd", [], workingDirectory: "/tmp");

        Assert.Equal("/tmp\n", ReadToEnd(child.StandardOutput!));
        Assert.Equal(0, Wait(child));
    }

    [Fact]
    public void The_environment_block_reaches_the_child_byte_for_byte()
    {
        var child = Launch("/usr/bin/env", []);

        var output = ReadToEnd(child.StandardOutput!);
        Assert.Contains("SCRIPTS_SPAWN_TEST=yes", output, StringComparison.Ordinal);
        Assert.Contains("PATH=/usr/bin:/bin", output, StringComparison.Ordinal);
        Assert.Equal(0, Wait(child));
    }

    [Fact]
    public void Standard_input_is_a_pipe_the_parent_can_feed()
    {
        var child = Launch("/bin/cat", []);

        child.StandardInput!.Write("abc");
        child.StandardInput.Close();
        Assert.Equal("abc", ReadToEnd(child.StandardOutput!));
        Assert.Equal(0, Wait(child));
    }

    [Fact]
    public void A_child_killed_by_a_signal_reports_128_plus_the_signal()
    {
        var child = Launch("/bin/sh", ["-c", "kill -TERM $$"]);

        Assert.Equal(143, Wait(child));
    }

    [Fact]
    public void The_child_starts_with_SIGPIPE_at_its_default_disposition()
    {
        var child = Launch("/bin/grep", ["SigIgn", "/proc/self/status"]);

        var output = ReadToEnd(child.StandardOutput!);
        Assert.DoesNotContain("0000000000001000", output, StringComparison.Ordinal);
        Assert.Equal(0, Wait(child));
    }

    [Fact]
    public void The_parent_ends_keep_close_on_exec()
    {
        var child = Launch("/bin/sleep", ["1"]);
        try
        {
            AssertCloseOnExec(((FileStream)child.StandardInput!.BaseStream).SafeFileHandle);
            AssertCloseOnExec(((FileStream)child.StandardOutput!).SafeFileHandle);
            AssertCloseOnExec(((FileStream)child.StandardError!).SafeFileHandle);
        }
        finally
        {
            Assert.Equal(0, Libc.Kill(child.ProcessIdentifier, Libc.SignalKill));
            Assert.Equal(137, Wait(child));
        }
    }

    private static void AssertCloseOnExec(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var descriptor = (int)handle.DangerousGetHandle();
        var flags = Libc.FileControl(descriptor, 1, 0); // F_GETFD
        Assert.True(flags >= 0, $"fcntl(F_GETFD) failed for descriptor {descriptor}");
        Assert.NotEqual(0, flags & 1); // FD_CLOEXEC
    }

    [Fact]
    public void The_child_sees_only_its_stdio_descriptors()
    {
        // The AOT binary has few descriptors, so the F_DUPFD path is taken there; a plain duplicate would survive
        // exec as 10/11/12 (audit F1). The child must see only 0/1/2 plus the directory fd opened by ls itself.
        var child = Launch("/bin/ls", ["/proc/self/fd"]);

        var descriptors = ReadToEnd(child.StandardOutput!)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse)
            .Order()
            .ToList();
        Assert.Equal(new[] { 0, 1, 2, 3 }, descriptors);
        Assert.Equal(0, Wait(child));
    }

    [Fact]
    public void A_descriptor_open_in_bcs_is_not_inherited_by_the_child()
    {
        var fileDescriptor = Libc.Open(NativeText.ToNullTerminated("/dev/null"), Libc.OpenReadOnly, 0);
        Assert.True(fileDescriptor >= 3, "the probe needs a descriptor above stdio");
        try
        {
            var child = Launch("/bin/ls", ["/proc/self/fd"]);

            var entries = ReadToEnd(child.StandardOutput!)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            Assert.DoesNotContain(fileDescriptor.ToString(System.Globalization.CultureInfo.InvariantCulture), entries);
            Assert.Equal(0, Wait(child));
        }
        finally
        {
            _ = Libc.Close(fileDescriptor);
        }
    }

    [Fact]
    public void The_child_starts_with_an_empty_signal_mask()
    {
        var child = Launch("/bin/grep", ["SigBlk", "/proc/self/status"]);

        Assert.Contains("0000000000000000", ReadToEnd(child.StandardOutput!), StringComparison.Ordinal);
        Assert.Equal(0, Wait(child));
    }

    [Fact]
    public void Waiting_for_a_process_that_is_not_a_child_returns_the_errno()
    {
        var errno = PosixSpawn.TryWait(int.MaxValue, out var exitCode);

        Assert.NotEqual(0, errno);
        Assert.Equal(-1, exitCode);
    }

    [Fact]
    public void A_missing_program_returns_the_exec_errno()
    {
        var spec = new SpawnSpec(
            "scripts-posix-spawn-probe",
            "/nonexistent/scripts-posix-spawn-probe",
            [],
            MinimalEnvironment,
            null,
            RedirectInput: false,
            RedirectOutputs: true,
            OwnProcessGroup: true);

        var errno = PosixSpawn.TryLaunch(spec, out var child);

        Assert.Equal(Errno.NoSuchEntry, errno);
        Assert.Null(child);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1 << 8, 1)]
    [InlineData(255 << 8, 255)]
    [InlineData(15, 143)]
    [InlineData(9, 137)]
    public void The_wait_status_decodes_exit_codes_and_signals(int status, int expected)
    {
        Assert.Equal(expected, PosixSpawn.DecodeWaitStatus(status));
    }

    private static SpawnedChild Launch(
        string program,
        string[] arguments,
        bool redirectInput = true,
        bool ownProcessGroup = true,
        string? workingDirectory = null)
    {
        var spec = new SpawnSpec(
            program,
            program,
            arguments,
            MinimalEnvironment,
            workingDirectory,
            redirectInput,
            RedirectOutputs: true,
            ownProcessGroup);

        var errno = PosixSpawn.TryLaunch(spec, out var child);
        Assert.True(errno == 0, $"posix_spawn failed with errno {errno}");
        return child!;
    }

    private static string ReadToEnd(Stream stream)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        return reader.ReadToEnd();
    }

    private static int Wait(SpawnedChild child)
    {
        child.StandardInput?.Dispose();
        var errno = PosixSpawn.TryWait(child.ProcessIdentifier, out var exitCode);
        Assert.True(errno == 0, $"waitpid failed with errno {errno}");
        return exitCode;
    }

    private static byte[] EnvironmentBlock(params (string Name, string Value)[] entries)
    {
        var block = new MemoryStream();
        foreach (var (name, value) in entries)
        {
            block.Write(Encoding.UTF8.GetBytes(name + "=" + value));
            block.WriteByte(0);
        }

        return block.ToArray();
    }
}
