using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Scripts.Corpus.Tests;

/// <summary>Result of one black-box child: bounded by the caller's timeout, with the
/// standard streams captured and the elapsed time kept for the startup assertions.</summary>
internal sealed record BlackboxProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    long ElapsedMilliseconds)
{
    public string CombinedOutput => StandardOutput + StandardError;
}

/// <summary>A private working directory for one case, removed when the case ends.</summary>
internal sealed class BlackboxWorkspace : IDisposable
{
    public BlackboxWorkspace(string prefix)
    {
        Path = Directory.CreateTempSubdirectory(prefix).FullName;
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A child that outlived its timeout must not mask the test result.
        }
    }
}

/// <summary>
/// The process, binary and trace helpers shared by the four black-box axes ported
/// from the former <c>tests/blackbox</c> shell corpus. Every child is bounded by
/// an explicit timeout; a launched process never inherits the test host's terminal.
/// </summary>
internal static class BlackboxRunner
{
    private static readonly Regex TracePattern = new(
        "Unhandled exception|at Scripts\\.|NullReference|Stack overflow|System\\.[A-Za-z]+Exception|scripts internal error",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Lazy<string> Snapshot = new(CreateSnapshot, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The configured binary (the AOT one under check-all step 5).</summary>
    public static string SourceBinary => CorpusPaths.BinaryPath();

    /// <summary>A private snapshot of <see cref="SourceBinary"/>; the build output may be
    /// rewritten by a concurrent publish, so the cases never execute that path.</summary>
    public static string Binary => Snapshot.Value;

    /// <summary>The stdin startup bound and the memory-floor check only hold for the published binary.</summary>
    public static bool SourceIsPublished => SourceBinary.Contains("/publish/", StringComparison.Ordinal);

    public static BlackboxProcessResult Run(
        string program,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        int timeoutMilliseconds,
        byte[]? standardInput = null)
    {
        var startInfo = new ProcessStartInfo(program)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"cannot start {program}");
        if (standardInput is not null)
        {
            process.StandardInput.BaseStream.Write(standardInput);
        }

        process.StandardInput.Close();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var timedOut = false;
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The child exited between the timeout and the kill.
            }

            process.WaitForExit();
        }

        var standardOutput = outputTask.GetAwaiter().GetResult();
        var standardError = errorTask.GetAwaiter().GetResult();
        stopwatch.Stop();
        return new BlackboxProcessResult(
            process.ExitCode,
            standardOutput,
            standardError,
            timedOut,
            stopwatch.ElapsedMilliseconds);
    }

    /// <summary>Runs <c>scripts run &lt;name&gt;.scs</c> with the source written under the work directory.</summary>
    public static BlackboxProcessResult RunScript(
        string workingDirectory,
        string name,
        string source,
        IReadOnlyList<string>? arguments = null,
        int timeoutMilliseconds = 30_000)
    {
        var entry = name + ".scs";
        File.WriteAllText(
            Path.Combine(workingDirectory, entry),
            source,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var command = new List<string> { "run", entry };
        if (arguments is not null)
        {
            command.AddRange(arguments);
        }

        return Run(Binary, command, workingDirectory, timeoutMilliseconds);
    }

    /// <summary>Runs a bounded shell command, used only where the case itself is a redirection,
    /// a pipe, a descriptor limit or a signal disposition.</summary>
    public static BlackboxProcessResult RunShell(
        string workingDirectory,
        string command,
        IReadOnlyList<string> arguments,
        int timeoutMilliseconds)
    {
        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add("bash");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("cannot start /bin/bash");
        process.StandardInput.Close();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var timedOut = false;
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The child exited between the timeout and the kill.
            }

            process.WaitForExit();
        }

        var standardOutput = outputTask.GetAwaiter().GetResult();
        var standardError = errorTask.GetAwaiter().GetResult();
        stopwatch.Stop();
        return new BlackboxProcessResult(
            process.ExitCode,
            standardOutput,
            standardError,
            timedOut,
            stopwatch.ElapsedMilliseconds);
    }

    /// <summary>The no-trace invariant: a crash must never print a runtime stack.</summary>
    public static void AssertNoTrace(BlackboxProcessResult result, string label)
    {
        Assert.False(
            TracePattern.IsMatch(result.CombinedOutput),
            $"{label}: a stack trace appeared\n{result.CombinedOutput}");
    }

    private static string CreateSnapshot()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException("blackbox: the scripts binary runs on Linux only");
        }

        var source = SourceBinary;
        var directory = Path.GetDirectoryName(source)
            ?? throw new InvalidOperationException($"binary has no directory: {source}");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var candidate = Path.Combine(
                directory,
                $".scs-under-test.{Environment.ProcessId}.{Guid.NewGuid():N}");
            try
            {
                File.Copy(source, candidate, overwrite: true);
                var mode = File.GetUnixFileMode(candidate)
                    | UnixFileMode.UserExecute
                    | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherExecute;
                File.SetUnixFileMode(candidate, mode);
                var check = Run(candidate, ["--version"], directory, 5_000);
                if (check.ExitCode == 0)
                {
                    var snapshot = candidate;
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(snapshot);
                    return candidate;
                }
            }
            catch (IOException)
            {
                // A concurrent publish can leave the copy half-written; retry.
            }
            catch (UnauthorizedAccessException)
            {
                // Same transient window, or a read-only directory: retry then fail.
            }

            TryDelete(candidate);
            Thread.Sleep(100);
        }

        throw new InvalidOperationException($"cannot copy a runnable scripts from {source}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The process is leaving; a leftover in bin/ is not tracked.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
