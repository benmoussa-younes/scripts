using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scripts.Corpus.Tests;

/// <summary>xUnit port of the <c>explain</c> corpus (phase 2).</summary>
[Collection("corpus")]
public sealed class ExplainCorpusTests
{
    public static TheoryData<string, string> Cases()
    {
        var root = FindRepositoryRoot();
        var directory = Path.Combine(root, "tests", "Corpus", "explain");
        var data = new TheoryData<string, string>();
        foreach (var expect in Directory.EnumerateFiles(directory, "*.expect").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(expect), directory);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case(string name, string directory)
    {
        var expectation = Expectation.Read(Path.Combine(directory, name + ".expect"));
        var entry = expectation.Entry ?? name + ".scs";
        var work = Directory.CreateTempSubdirectory("scripts-explain-");
        try
        {
            CopyTree(Path.Combine(directory, name + ".files"), work.FullName);
            File.Copy(Path.Combine(directory, entry), Path.Combine(work.FullName, entry));

            var first = Run(work.FullName, "explain", entry);
            if (expectation.ExitCode is { } expectedExit)
            {
                Assert.True(first.ExitCode == expectedExit, $"explain {name}: exit {first.ExitCode}, expected {expectedExit}\n{first.StandardError}");
            }

            if (expectation.StandardOutput is { } expectedOutput)
            {
                var normalizedExpected = NormalizeHeader(expectedOutput);
                var normalizedActual = NormalizeHeader(first.StandardOutput);
                Assert.True(
                    normalizedActual == normalizedExpected,
                    $"explain {name}: stdout differs\n--- expected ---\n{normalizedExpected}\n--- actual ---\n{normalizedActual}");
            }

            var second = Run(work.FullName, "explain", entry);
            Assert.True(first.StandardOutput == second.StandardOutput, $"explain {name}: non-deterministic");

            var json = Run(work.FullName, "explain", "--json-out", entry);
            Assert.True(json.ExitCode == first.ExitCode, $"explain --json-out {name}: exit {json.ExitCode}, human {first.ExitCode}");
            using var document = JsonDocument.Parse(json.StandardOutput);
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
            Assert.Equal("explain", root.GetProperty("command").GetString());
            if (first.ExitCode == 0)
            {
                Assert.Equal(0, root.GetProperty("errors").GetArrayLength());
                Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
                var body = first.StandardOutput
                    .Split('\n')
                    .Where(line => !line.StartsWith("Script `", StringComparison.Ordinal) && line.Length > 0)
                    .ToList();
                var steps = root.GetProperty("steps");
                Assert.Equal(body.Count, steps.GetArrayLength());
                for (var index = 0; index < body.Count; index++)
                {
                    var line = body[index];
                    var step = steps[index];
                    var depth = (line.Length - line.TrimStart(' ').Length) / 2;
                    Assert.Equal(depth, step.GetProperty("depth").GetInt32());
                    Assert.Equal(line.Trim(), step.GetProperty("text").GetString());
                    Assert.True(step.GetProperty("destructive").ValueKind is JsonValueKind.True or JsonValueKind.False);
                    Assert.True(step.GetProperty("dangerous").ValueKind is JsonValueKind.True or JsonValueKind.False);
                    Assert.False(string.IsNullOrEmpty(step.GetProperty("kind").GetString()));
                    Assert.True(step.GetProperty("line").GetInt32() >= 1);
                    Assert.True(step.GetProperty("column").GetInt32() >= 1);
                }
            }
            else
            {
                Assert.False(root.GetProperty("success").GetBoolean());
                Assert.True(root.GetProperty("errors").GetArrayLength() > 0);
            }
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    private static (int ExitCode, string StandardOutput, string StandardError) Run(string workingDirectory, params string[] arguments)
    {
        var binary = BinaryPath();
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
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.Result, error.Result);
    }

    private static string NormalizeHeader(string text) =>
        Regex.Replace(text, "^Script `.*`:$", "Script `ENTREE`:", RegexOptions.Multiline);

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
