using System.Text;
using Scripts.Runtime.Tests.SystemFunctions;

namespace Scripts.Runtime.Tests.InterpreterRuns;

/// <summary>End-to-end acceptance: every migration script produces exactly the bytes of its .expected file (parser + interpreter, no checker).</summary>
[Collection(SystemTestCollection.Name)]
public sealed class InterpreterMigrationTests
{
    public static TheoryData<string> CaseNames
    {
        get
        {
            var cases = new TheoryData<string>();
            var names = Directory.EnumerateFiles(InterpreterFiles.Combine("tests", "Corpus", "migration"), "*.scs")
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .Order(StringComparer.Ordinal);
            foreach (var name in names)
            {
                cases.Add(name);
            }

            return cases;
        }
    }

    [Fact]
    public void CorpusHasTwentyOneScripts()
    {
        Assert.Equal(21, CaseNames.Count);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ScriptOutputIsByteIdenticalToExpected(string caseName)
    {
        using var workingDirectory = new TemporaryWorkingDirectory();
        var scriptPath = InterpreterFiles.Combine("tests", "Corpus", "migration", caseName + ".scs");
        var expectedBytes = File.ReadAllBytes(InterpreterFiles.Combine("tests", "Corpus", "migration", caseName + ".expected"));

        var run = InterpreterFiles.RunFile(scriptPath);

        Assert.True(
            run.ExitCode == 0,
            $"exit {run.ExitCode}: {run.Outcome.Failure?.Code} {run.Outcome.Failure?.Message} at {run.FailurePosition}");
        Assert.Equal(Encoding.UTF8.GetString(expectedBytes), run.StandardOutput);
        Assert.Equal(expectedBytes, new UTF8Encoding(false).GetBytes(run.StandardOutput));
    }
}
