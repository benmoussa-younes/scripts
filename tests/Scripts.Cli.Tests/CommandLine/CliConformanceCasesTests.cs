using Scripts.Core;

namespace Scripts.Cli.Tests.CommandLine;

/// <summary>Replays the tests/Corpus/errors/SCRIPTS-CLI-*.cmd cases against the parser.</summary>
public sealed class CliConformanceCasesTests
{
    private const string ScriptUnreadableCode = "SCRIPTS-CLI-0009";
    private const string StandardInputUnreadableCode = "SCRIPTS-CLI-0010";

    public static TheoryData<string> CaseNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var caseName in ListCaseNames())
            {
                data.Add(caseName);
            }

            return data;
        }
    }

    private static List<string> ListCaseNames() =>
        Directory.GetFiles(ConformanceDirectory(), "SCRIPTS-CLI-*.cmd")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    private static string ConformanceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Scripts.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "tests", "Corpus", "errors");
    }

    [Fact]
    public void Every_cli_code_from_one_to_ten_has_a_conformance_case()
    {
        var names = ListCaseNames();

        // CLI-0001..0010: the .cmd corpus. CLI-0011 lives in the json corpus; CLI-0012..0016 are command
        // behaviours covered by the EndToEnd CLI tests, not by this parser corpus.
        Assert.Equal(Enumerable.Range(1, 10).Select(number => $"SCRIPTS-CLI-{number:D4}"), names);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Command_line_parses_to_the_expected_code(string caseName)
    {
        var commandLine = File.ReadAllText(Path.Combine(ConformanceDirectory(), caseName + ".cmd")).Trim();
        var expectation = File.ReadAllText(Path.Combine(ConformanceDirectory(), caseName + ".expect")).Trim();
        var tokens = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("scripts", tokens[0]);
        var expectedParts = expectation.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("expect:", expectedParts[0]);
        var expectedCode = expectedParts[1];
        Assert.Equal(caseName, expectedCode);
        var expectedExitCode = int.Parse(expectedParts[2].Replace("exit=", string.Empty, StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture);

        // The case file names the shell redirection (`< /`) after the command; the parser only sees the arguments.
        var standardInputRedirect = Array.IndexOf(tokens, "<");
        var commandLineArguments = tokens.Skip(1).Take(standardInputRedirect < 0 ? tokens.Length : standardInputRedirect - 1).ToArray();
        var result = CommandLineParser.Parse(commandLineArguments);

        if (expectedCode is ScriptUnreadableCode or StandardInputUnreadableCode)
        {
            // CLI-0009/CLI-0010 are detected after a successful parse (missing file, unreadable standard input): code 252.
            Assert.Equal(ExitCodes.ScriptUnreadable, expectedExitCode);
            var accepted = Assert.IsType<CommandLineAccepted>(result);
            Assert.True(
                expectedCode == StandardInputUnreadableCode
                    ? IsStandardInputScript(accepted.Command)
                    : !IsStandardInputScript(accepted.Command),
                $"{caseName}: unexpected command {accepted.Command}");
            return;
        }

        var rejected = Assert.IsType<CommandLineRejected>(result);
        Assert.Equal(expectedCode, rejected.Diagnostic.Code);
        Assert.Equal(ExitCodes.Usage, expectedExitCode);
    }

    /// <summary>True when the accepted command reads its script from the standard input (CLI-0010 cases).</summary>
    private static bool IsStandardInputScript(ParsedCommand command) => command switch
    {
        RunCommand run => run.StandardInput,
        CheckCommand check => check.StandardInput,
        _ => false,
    };
}
