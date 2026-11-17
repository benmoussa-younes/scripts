using System.Diagnostics;
using System.Text;

namespace Scripts.Corpus.Tests;

[CollectionDefinition("corpus", DisableParallelization = true)]
public sealed class CorpusCollection;

/// <summary>
/// xUnit runner for the data-driven corpus: a case is an <c>.scs</c> entry (or an
/// <c>entry:</c> target), an optional <c>.files/</c> tree, an optional <c>.dirs</c>
/// list (empty directories) and a <c>.setup.sh</c> hook, run by the built
/// <c>scripts</c> binary and compared with the <c>.expect</c> directives.
/// Phase 1 covers the base language categories and the standard library.
/// </summary>
[Collection("corpus")]
public sealed class CorpusTests
{
    private static readonly string[] BaseCategories =
    [
        "types", "mutability", "expressions", "strings", "control", "functions",
        "modules", "process", "environment", "signals", "limits",
    ];

    public static TheoryData<string, string, string> Cases()
    {
        var root = FindRepositoryRoot();
        var data = new TheoryData<string, string, string>();
        foreach (var category in BaseCategories)
        {
            var directory = Path.Combine(root, "tests", "Corpus", "base", category);
            AddCases(data, directory, category, recurse: false);
        }

        AddCases(data, Path.Combine(root, "tests", "Corpus", "stdlib"), "stdlib", recurse: true);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case(string label, string name, string directory) => RunCase(label, name, directory);

    private static void AddCases(TheoryData<string, string, string> data, string directory, string prefix, bool recurse)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var option = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var expect in Directory.EnumerateFiles(directory, "*.expect", option).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(expect);
            var caseDirectory = Path.GetDirectoryName(expect)!;
            var relative = Path.GetRelativePath(Path.Combine(FindRepositoryRoot(), "tests", "Corpus"), caseDirectory)
                .Replace(Path.DirectorySeparatorChar, '/');
            data.Add(relative, name, caseDirectory);
        }
    }

    private static void RunCase(string label, string name, string directory)
    {
        var expectation = Expectation.Read(Path.Combine(directory, name + ".expect"));
        var entry = expectation.Entry ?? name + ".scs";
        var source = Path.Combine(directory, entry);
        Assert.True(File.Exists(source), $"{label}: no entry `{entry}`");

        var work = Directory.CreateTempSubdirectory("scripts-corpus-");
        try
        {
            CopyTree(Path.Combine(directory, name + ".files"), work.FullName);
            CreateEmptyDirectories(Path.Combine(directory, name + ".dirs"), work.FullName);
            RunSetup(Path.Combine(directory, name + ".setup.sh"), work.FullName);
            File.Copy(source, Path.Combine(work.FullName, entry));

            var (exitCode, standardOutput, standardError) = Run(work.FullName, entry);
            if (expectation.ExitCode is { } expectedExit)
            {
                Assert.True(
                    exitCode == expectedExit,
                    $"{label}: exit {exitCode}, expected {expectedExit}\nstderr: {standardError}");
            }

            if (expectation.StandardOutput is { } expectedOutput)
            {
                Assert.True(
                    standardOutput == expectedOutput,
                    $"{label}: stdout differs\n--- expected ---\n{expectedOutput}\n--- actual ---\n{standardOutput}");
            }

            if (expectation.StandardError is { } expectedError)
            {
                Assert.True(
                    standardError == expectedError,
                    $"{label}: stderr differs\n--- expected ---\n{expectedError}\n--- actual ---\n{standardError}");
            }
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    private static (int ExitCode, string StandardOutput, string StandardError) Run(string workingDirectory, string entry)
    {
        var binary = Environment.GetEnvironmentVariable("SCRIPTS_CORPUS_BINARY");
        if (string.IsNullOrEmpty(binary))
        {
            binary = Path.Combine(FindRepositoryRoot(), "src", "Scripts.App", "bin", "Release", "net10.0", "scripts");
        }

        Assert.True(File.Exists(binary), $"scripts binary not found: {binary} (build the solution first)");

        var startInfo = new ProcessStartInfo(binary)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add(entry);

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"case timed out: {entry}");
        }

        return (process.ExitCode, output.Result, error.Result);
    }

    private static void CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            var target = Path.Combine(destination, name);
            var link = new FileInfo(entry).LinkTarget;
            if (link is not null)
            {
                File.CreateSymbolicLink(target, link);
                continue;
            }

            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(target);
                CopyTree(entry, target);
                continue;
            }

            File.Copy(entry, target);
        }
    }

    private static void CreateEmptyDirectories(string listPath, string destination)
    {
        if (!File.Exists(listPath))
        {
            return;
        }

        foreach (var line in File.ReadAllLines(listPath))
        {
            if (line.Length > 0)
            {
                Directory.CreateDirectory(Path.Combine(destination, line));
            }
        }
    }

    private static void RunSetup(string setupPath, string destination)
    {
        if (!File.Exists(setupPath))
        {
            return;
        }

        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            WorkingDirectory = destination,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(setupPath);
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"setup failed: {setupPath}\n{output}{error}");
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
