using System.Diagnostics;
using System.Net.Sockets;
using Xunit.Sdk;

namespace Scripts.EndToEnd.Tests;

/// <summary>
/// BUG-020: the entry script path is pre-checked before the loader opens it, so a FIFO or a socket fails fast
/// with SCRIPTS-CLI-0009 instead of blocking in open(2). Every child is bounded by <see cref="ProcessTimeout"/>:
/// a regression fails the test instead of hanging the suite.
/// </summary>
public sealed class EntryScriptPathTests : IDisposable
{
    // Anti-regression guard, not a performance budget: a pre-check refusal takes milliseconds, a blocking
    // open(2) never returns without a writer, so a wide bound tells them apart without being flaky under load.
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);

    private readonly string directory = ScriptsProcess.NewTemporaryDirectory();

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Theory]
    [InlineData("check")]
    [InlineData("run")]
    [InlineData("explain")]
    public void A_fifo_entry_is_refused_without_a_writer(string command)
    {
        var fifo = MakeFifo("entry.fifo");

        var result = RunBounded(command, fifo);

        Assert.Equal(252, result.ExitCode);
        Assert.Contains("SCRIPTS-CLI-0009", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("not a regular file", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void Fmt_check_refuses_a_fifo_without_a_writer()
    {
        var fifo = MakeFifo("entry-fmt.fifo");

        var result = RunBounded("fmt", "--check", fifo);

        Assert.Equal(252, result.ExitCode);
        Assert.Contains("SCRIPTS-CLI-0009", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("not a regular file", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_entry_keeps_the_loader_reason()
    {
        var missing = Path.Combine(directory, "absent.scs");

        var result = RunBounded("check", missing);

        Assert.Equal(252, result.ExitCode);
        Assert.Contains("SCRIPTS-CLI-0009", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("(file not found)", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("(not a regular file)", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void A_socket_entry_is_refused()
    {
        var socketPath = Path.Combine(directory, "entry.sock");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));

        var result = RunBounded("check", socketPath);

        Assert.Equal(252, result.ExitCode);
        Assert.Contains("SCRIPTS-CLI-0009", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("not a regular file", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void A_directory_entry_is_refused_with_the_script_family_code()
    {
        var subdirectory = Path.Combine(directory, "a-directory");
        Directory.CreateDirectory(subdirectory);

        var result = RunBounded("check", subdirectory);

        Assert.Equal(252, result.ExitCode);
        Assert.Contains("SCRIPTS-CLI-0009", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void A_symlink_to_a_fifo_is_refused()
    {
        var fifo = MakeFifo("target.fifo");
        var link = Path.Combine(directory, "entry-link");
        File.CreateSymbolicLink(link, fifo);

        var result = RunBounded("check", link);

        Assert.Equal(252, result.ExitCode);
        Assert.Contains("SCRIPTS-CLI-0009", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("not a regular file", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void A_symlink_to_a_regular_file_is_accepted()
    {
        var target = ScriptsProcess.WriteScript(directory, "target.scs", "let value = 1\n");
        var link = Path.Combine(directory, "entry-link.scs");
        File.CreateSymbolicLink(link, target);

        var result = RunBounded("check", link);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public void A_character_device_is_accepted_as_an_empty_script()
    {
        if (!File.Exists("/dev/null"))
        {
            throw SkipException.ForSkip("/dev/null is required for the character-device test");
        }

        var result = RunBounded("check", "/dev/null");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public void A_regular_file_is_accepted()
    {
        var script = ScriptsProcess.WriteScript(directory, "entry.scs", "io.write_standard_output_line(\"run\")\n");

        var result = RunBounded("run", script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("run\n", result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public void A_standard_input_entry_still_reads_the_pipe()
    {
        var result = ScriptsProcess.RunWithStandardInput(
            "io.write_standard_output_line(\"hello\")\n", "run", "-");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello\n", result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    private string MakeFifo(string name)
    {
        var path = Path.Combine(directory, name);
        using var process = Process.Start(new ProcessStartInfo("mkfifo")
        {
            ArgumentList = { path },
            UseShellExecute = false,
        });
        process!.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return path;
    }

    /// <summary>
    /// Runs the real binary with the standard input closed and kills it after <see cref="ProcessTimeout"/>; a
    /// pre-check regression that blocks in open(2) therefore fails the test instead of hanging the suite.
    /// </summary>
    private static ScriptsProcessResult RunBounded(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(ScriptsProcess.Binary)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new XunitException(
                $"scripts {string.Join(' ', arguments)} did not finish within {ProcessTimeout.TotalSeconds} s");
        }

        process.WaitForExit();
        return new ScriptsProcessResult(process.ExitCode, standardOutput.Result, standardError.Result);
    }
}
