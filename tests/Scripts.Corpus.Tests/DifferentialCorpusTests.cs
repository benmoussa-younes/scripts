using System.Diagnostics;
using System.Text;

namespace Scripts.Corpus.Tests;

/// <summary>xUnit port of the differential corpus: scripts must match its
/// <c>.expected</c> output and must not behave like bash.</summary>
[Collection("corpus")]
public sealed class DifferentialCorpusTests
{
    public static TheoryData<string, string> Cases()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "tests", "Corpus", "differential");
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
        var arguments = File.Exists(Path.Combine(directory, name + ".args"))
            ? File.ReadAllLines(Path.Combine(directory, name + ".args"))
            : [];
        var expected = File.ReadAllText(Path.Combine(directory, name + ".expected"));
        var bash = Run("bash", [Path.Combine(directory, name + ".sh"), .. arguments], requireSuccess: false);
        var scripts = Run(BinaryPath(), ["run", Path.Combine(directory, name + ".scs"), .. arguments], requireSuccess: true);

        Assert.True(scripts == expected, $"{name}: scripts output differs\n--- expected ---\n{expected}\n--- actual ---\n{scripts}");
        Assert.True(scripts != bash, $"{name}: scripts behaved like bash");
    }

    private static string Run(string program, IReadOnlyList<string> arguments, bool requireSuccess)
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
        if (requireSuccess)
        {
            Assert.True(process.ExitCode == 0, $"{program}: exit {process.ExitCode}\n{error}");
        }

        return output;
    }

    private static string BinaryPath() => CorpusPaths.BinaryPath();
    private static string FindRepositoryRoot() => CorpusPaths.RepositoryRoot;
}
