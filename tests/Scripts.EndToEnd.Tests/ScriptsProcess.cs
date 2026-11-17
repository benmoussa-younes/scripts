using System.Diagnostics;
using System.Text;

namespace Scripts.EndToEnd.Tests;

/// <summary>Runs the real scripts process (apphost produced next to the test assembly's dependency) and captures its outcome.</summary>
public sealed record ScriptsProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Locates the scripts apphost built from src/Scripts.App and starts it as a child process, exactly as a user would.</summary>
public static class ScriptsProcess
{
    private static readonly Lazy<string> BinaryPath = new(FindBinary);

    public static string Binary => BinaryPath.Value;

    public static ScriptsProcessResult Run(params string[] arguments) => RunWithStandardInput(string.Empty, arguments);

    /// <summary>
    /// Runs <c>scripts run scriptPath</c> with its stdout redirected (via a shell) to <paramref name="outputPath"/>
    /// (e.g. /dev/full), capturing stderr and the exit code. Used to test write-failure handling.
    /// </summary>
    public static ScriptsProcessResult RunWithStandardOutputRedirect(string scriptPath, string outputPath)
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        DisableDiagnostics(startInfo);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exec \"$0\" run \"$1\" > \"$2\"");
        startInfo.ArgumentList.Add(Binary);
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo)!;
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ScriptsProcessResult(process.ExitCode, standardOutput, standardError);
    }

    /// <summary>
    /// Runs <c>scripts run scriptPath</c> with stdout connected to a pipe whose read end this process closes before the
    /// child flushes: a buffered write is then only discovered by the final shutdown flush (EPIPE, code 141).
    /// </summary>
    public static ScriptsProcessResult RunWithClosedStandardOutput(string scriptPath)
    {
        var startInfo = new ProcessStartInfo(Binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add(scriptPath);

        using var process = Process.Start(startInfo)!;
        process.StandardOutput.Close();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ScriptsProcessResult(process.ExitCode, string.Empty, standardError);
    }

    /// <summary>
    /// Runs <c>scripts run scriptPath</c> with fd 0 closed by the shell (<c>&lt;&amp;-</c>) before the exec, so the loader may
    /// hand a low descriptor to the runtime: used to prove that a file standard input is not passed as fd 0/1/2.
    /// </summary>
    public static ScriptsProcessResult RunWithClosedStandardInput(string workingDirectory, string scriptPath)
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        DisableDiagnostics(startInfo);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exec \"$0\" run \"$1\" <&-");
        startInfo.ArgumentList.Add(Binary);
        startInfo.ArgumentList.Add(scriptPath);

        using var process = Process.Start(startInfo)!;
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ScriptsProcessResult(process.ExitCode, standardOutput, standardError);
    }

    public static ScriptsProcessResult RunWithStandardInput(string standardInput, params string[] arguments) =>
        RunWithStandardInput(standardInput, beforeStandardInput: null, arguments);

    /// <summary>
    /// Same as <see cref="RunWithStandardInput(string, string[])"/>, but runs <paramref name="beforeStandardInput"/>
    /// after the child starts and before its standard input is written. The tests use it to prove that a child which
    /// legitimately exits before reading its input is still reported through its exit code and output.
    /// </summary>
    internal static ScriptsProcessResult RunWithStandardInput(
        string standardInput, Action<Process>? beforeStandardInput, params string[] arguments)
    {
        var startInfo = CreateStartInfo(arguments);
        using var process = Process.Start(startInfo)!;
        return Complete(process, standardInput, beforeStandardInput);
    }

    /// <summary>
    /// Runs the helper script <paramref name="helperScriptPath"/> as <c>sh helper.sh &lt;arguments…&gt;</c> and captures
    /// its outcome (<c>$1</c>… are the arguments; the script can re-execute one of them with its own redirections): the
    /// test process never changes its own descriptor table.
    /// </summary>
    public static ScriptsProcessResult RunThroughShellScript(string helperScriptPath, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        DisableDiagnostics(startInfo);
        startInfo.ArgumentList.Add(helperScriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        return Complete(process, standardInput: null);
    }

    /// <summary>Runs <c>scripts arguments</c> in <paramref name="workingDirectory"/>, feeding <paramref name="standardInput"/> on stdin.</summary>
    public static ScriptsProcessResult RunInDirectoryWithStandardInput(
        string workingDirectory, string standardInput, params string[] arguments)
    {
        var startInfo = CreateStartInfo(arguments);
        startInfo.WorkingDirectory = workingDirectory;
        using var process = Process.Start(startInfo)!;
        return Complete(process, standardInput);
    }

    private static ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(Binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        DisableDiagnostics(startInfo);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static ScriptsProcessResult Complete(
        Process process, string? standardInput, Action<Process>? beforeStandardInput = null)
    {
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) => Append(standardOutput, eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => Append(standardError, eventArgs.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (standardInput is not null)
        {
            beforeStandardInput?.Invoke(process);
            try
            {
                process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The child may legitimately exit before it reads its standard input (the REPL rejects an empty
                // --record path at startup, for example), so the pipe's read end is already gone and the write or
                // the close sees a broken pipe (EPIPE). That is not a harness failure: the exit code and the
                // captured output below are what the test asserts, and they still decide the outcome.
            }
            catch (ObjectDisposedException)
            {
                // The same early exit observed through a stream the process has already disposed.
            }
        }

        process.WaitForExit();
        // Flush the asynchronous readers before returning.
        process.WaitForExit();
        return new ScriptsProcessResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
    }

    public static string WriteScript(string directory, string name, string contents)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, contents, new UTF8Encoding(false));
        return path;
    }

    public static string NewTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "scripts-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Turns the .NET diagnostics servers off in the child: they add startup threads and an out-of-process handshake
    /// that have nothing to do with what these tests check, and stderr then carries only the script's diagnostics.
    /// </summary>
    private static void DisableDiagnostics(ProcessStartInfo startInfo) =>
        startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is not null)
        {
            builder.Append(line).Append('\n');
        }
    }

    private static string FindBinary()
    {
        // Tests run only against the published Native AOT deliverable (no JIT apphost).
        var configured = Environment.GetEnvironmentVariable("SCRIPTS_E2E_BINARY");
        if (!string.IsNullOrEmpty(configured))
        {
            var fullPath = Path.GetFullPath(configured);
            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException($"SCRIPTS_E2E_BINARY does not exist: {fullPath}");
            }

            return fullPath;
        }

        // Fallback: walk up to the repository and use the published Native AOT binary.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Scripts.slnx")))
            {
                var published = Path.Combine(directory.FullName, "src", "Scripts.App", "bin", "Release", "net10.0", "linux-x64", "publish", "scripts");
                if (File.Exists(published))
                {
                    return published;
                }
            }
        }

        throw new InvalidOperationException(
            "scripts AOT binary not found starting from " + AppContext.BaseDirectory
            + " (publish it first: dotnet publish src/Scripts.App/Scripts.App.csproj -c Release -r linux-x64)");
    }
}
