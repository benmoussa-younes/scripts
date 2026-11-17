using Scripts.Core.Reporting;
using Scripts.Runtime.Tests.SystemFunctions;

namespace Scripts.Runtime.Tests.InterpreterRuns;

/// <summary>Data-driven conformance: each tests/Corpus/errors/SCRIPTS-RUNTIME-NNNN.scs must fail with the code, position and exit code of its .expect file.</summary>
[Collection(SystemTestCollection.Name)]
public sealed class InterpreterConformanceTests
{
    /// <summary>Cases whose memory or time cost is too high for a routine run; set SCRIPTS_HEAVY_TESTS=1 to include them.</summary>
    private static readonly HashSet<string> HeavyCases = ["SCRIPTS-RUNTIME-0013", "SCRIPTS-RUNTIME-0014"];

    public static TheoryData<string> CaseNames
    {
        get
        {
            var directory = InterpreterFiles.Combine("tests", "Corpus", "errors");
            var skipped = File.ReadAllLines(Path.Combine(directory, "SKIPPED.txt"))
                .Select(line => line.Split([' ', '\t'], 2)[0])
                .ToHashSet(StringComparer.Ordinal);
            var cases = new TheoryData<string>();
            foreach (var path in Directory.EnumerateFiles(directory, "SCRIPTS-RUNTIME-*.scs").Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!skipped.Contains(name) && !HeavyCases.Contains(name))
                {
                    cases.Add(name);
                }
            }

            return cases;
        }
    }

    [Fact]
    public void CorpusCoversEveryRuntimeCodeExceptTheSkippedOnes()
    {
        Assert.True(CaseNames.Count >= 14, $"only {CaseNames.Count} runtime conformance cases found");
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void FailureMatchesExpectation(string caseName) => AssertCase(caseName);

    [Theory]
    [InlineData("SCRIPTS-RUNTIME-0013")]
    [InlineData("SCRIPTS-RUNTIME-0014")]
    public void HeavyFailureMatchesExpectation(string caseName)
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("SCRIPTS_HEAVY_TESTS") == "1",
            "set SCRIPTS_HEAVY_TESTS=1 to run (needs about 2.5 GiB of memory)");
        AssertCase(caseName);
    }

    private static void AssertCase(string caseName)
    {
        var directory = InterpreterFiles.Combine("tests", "Corpus", "errors");
        var expectation = File.ReadAllText(Path.Combine(directory, caseName + ".expect")).Trim().Split(' ');
        Assert.Equal("expect:", expectation[0]);
        var expectedCode = expectation[1];
        var expectedPosition = expectation[2];
        var expectedExitCode = expectation.Length > 3 && expectation[3].StartsWith("exit=", StringComparison.Ordinal)
            ? int.Parse(expectation[3]["exit=".Length..], System.Globalization.CultureInfo.InvariantCulture)
            : 253;

        using var workingDirectory = new TemporaryWorkingDirectory();
        var scriptPath = Path.Combine(workingDirectory.Path, "script.scs");
        File.Copy(Path.Combine(directory, caseName + ".scs"), scriptPath);
        var auxiliaryDirectory = Path.Combine(directory, caseName + ".files");
        if (Directory.Exists(auxiliaryDirectory))
        {
            foreach (var auxiliary in Directory.EnumerateFiles(auxiliaryDirectory))
            {
                File.Copy(auxiliary, Path.Combine(workingDirectory.Path, Path.GetFileName(auxiliary)));
            }
        }

        var run = InterpreterFiles.RunFile(scriptPath);

        Assert.Equal(expectedCode, run.FailureCode);
        Assert.Equal(expectedPosition, run.FailurePosition);
        Assert.Equal(expectedExitCode, run.ExitCode);
        Assert.Equal(RunTermination.RuntimeError, run.Outcome.Termination);
    }
}
