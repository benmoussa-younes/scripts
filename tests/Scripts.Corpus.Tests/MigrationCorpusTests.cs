using System.Diagnostics;
using System.Text;

namespace Scripts.Corpus.Tests;

/// <summary>xUnit port of the bash → scripts migration corpus: both sides are run
/// and their stdout must equal the recorded <c>.expected</c>.</summary>
[Collection("corpus")]
public sealed class MigrationCorpusTests
{
    public static TheoryData<string, string> Cases()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "tests", "Corpus", "migration");
        var data = new TheoryData<string, string>();
        foreach (var expected in Directory.EnumerateFiles(directory, "*.expected").Where(path => Path.GetFileName(path).Length > 2 && char.IsAsciiDigit(Path.GetFileName(path)[0]) && char.IsAsciiDigit(Path.GetFileName(path)[1])).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(expected), directory);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case(string name, string directory)
    {
        var expected = File.ReadAllText(Path.Combine(directory, name + ".expected"));
        Assert.Equal(expected, Run("bash", [Path.Combine(directory, name + ".sh")]));
        Assert.Equal(expected, Run(BinaryPath(), ["run", Path.Combine(directory, name + ".scs")]));
    }

    private static string Run(string program, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(program)
        {
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
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{program} {string.Join(' ', arguments)}: exit {process.ExitCode}\n{error}");
        return output;
    }

    private static string BinaryPath() => CorpusPaths.BinaryPath();
    private static string FindRepositoryRoot() => CorpusPaths.RepositoryRoot;
}
