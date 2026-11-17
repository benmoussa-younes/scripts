using System.Diagnostics;
using System.Text;

namespace Scripts.Corpus.Tests;

/// <summary>xUnit port of the standard-input corpus (phase 2). The command line
/// comes from the case's <c>.command</c> file; a directory stdio redirect is kept
/// through <c>/bin/bash</c>, the only shell use in this suite.</summary>
[Collection("corpus")]
public sealed class StdinCorpusTests
{
    public static TheoryData<string, string> Cases()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "tests", "Corpus", "stdin");
        var data = new TheoryData<string, string>();
        foreach (var expect in Directory.EnumerateFiles(directory, "*.expect").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(expect), directory);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Case(string name, string directory)
    {
        var expectation = Expectation.Read(Path.Combine(directory, name + ".expect"));
        var work = Directory.CreateTempSubdirectory("scripts-stdin-");
        try
        {
            CopyTree(Path.Combine(directory, name + ".files"), work.FullName);
            var command = File.Exists(Path.Combine(directory, name + ".command"))
                ? File.ReadAllText(Path.Combine(directory, name + ".command")).Trim()
                : "scripts run -";
            command = command.Replace("scripts", $"\"{BinaryPath()}\"", StringComparison.Ordinal);

            var startInfo = new ProcessStartInfo("/bin/bash")
            {
                WorkingDirectory = work.FullName,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);

            using var process = Process.Start(startInfo)!;
            var input = Path.Combine(directory, name + ".stdin");
            if (File.Exists(input))
            {
                process.StandardInput.Write(File.ReadAllText(input));
            }

            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (expectation.ExitCode is { } expectedExit)
            {
                Assert.True(process.ExitCode == expectedExit, $"{name}: exit {process.ExitCode}, expected {expectedExit}\n{await error}");
            }

            if (expectation.StandardOutput is { } expectedOutput)
            {
                Assert.True(await output == expectedOutput, $"{name}: stdout differs\n--- expected ---\n{expectedOutput}\n--- actual ---\n{await output}");
            }

            if (expectation.StandardError is { } expectedError)
            {
                Assert.True(await error == expectedError, $"{name}: stderr differs\n--- expected ---\n{expectedError}\n--- actual ---\n{await error}");
            }
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    private static void CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(entry));
            var link = new FileInfo(entry).LinkTarget;
            if (link is not null)
            {
                File.CreateSymbolicLink(target, link);
            }
            else if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(target);
                CopyTree(entry, target);
            }
            else
            {
                File.Copy(entry, target);
            }
        }
    }

    private static string BinaryPath()
    {
        var configured = Environment.GetEnvironmentVariable("SCRIPTS_CORPUS_BINARY");
        var binary = string.IsNullOrEmpty(configured)
            ? Path.Combine(FindRepositoryRoot(), "src", "Scripts.App", "bin", "Release", "net10.0", "scripts")
            : configured;
        Assert.True(File.Exists(binary), $"scripts binary not found: {binary} (build the solution first)");
        return binary;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Scripts.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Scripts.slnx not found above " + AppContext.BaseDirectory);
    }
}
