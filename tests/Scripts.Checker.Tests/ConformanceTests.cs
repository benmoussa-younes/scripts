using System.Text.RegularExpressions;
using Scripts.Core;
using Scripts.Syntax;

namespace Scripts.Checker.Tests;

/// <summary>One trigger example of (tests/Corpus/errors) run through loader and checker.</summary>
internal sealed record CheckerCase(
    string Name,
    string ExpectedCode,
    bool IsWarning,
    int Line,
    int Column,
    string ExpectedFileName,
    string MainText,
    IReadOnlyDictionary<string, string> AuxiliaryFiles)
{
    public const string MainFileName = "main.scs";

    public InMemorySourceProvider ToProvider()
    {
        var provider = new InMemorySourceProvider().AddText("/work/" + MainFileName, MainText);
        foreach (var (fileName, text) in AuxiliaryFiles)
        {
            provider.AddText("/work/" + fileName, text);
        }

        return provider;
    }

    public CheckResult Run()
    {
        var loaded = ProgramLoader.Load("/work/" + MainFileName, ToProvider());
        Assert.True(loaded.Error is null, Name + ": load failed: " + Checking.Describe(loaded.Error));
        return ProgramChecker.Check(loaded.Program!);
    }
}

internal static partial class CheckerCases
{
    private static readonly HashSet<string> LoadPhaseModuleCodes =
        [ErrorCodes.Module0001, ErrorCodes.Module0002, ErrorCodes.Module0003, ErrorCodes.Module0005, ErrorCodes.Module0007];

    /// <summary>
    /// Trigger examples whose first line is `# expect-skip: CODE (reason)` have no executable case:
    /// DocGen skips them, so the semantic corpus cannot contain them.
    /// </summary>
    public static readonly HashSet<string> SkippedCodes = File
        .ReadLines(Path.Combine(Repository.ConformanceErrorsDirectory, "SKIPPED.txt"))
        .Select(line => line.Split('\t')[0])
        .Where(code => code.StartsWith("SCRIPTS-", StringComparison.Ordinal))
        .Where(code => !File.Exists(Path.Combine(Repository.ConformanceErrorsDirectory, code + ".scs"))
            && !File.Exists(Path.Combine(Repository.ConformanceErrorsDirectory, code + ".cmd")))
        .ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyList<string> SemanticCodes() =>
        [.. ErrorCatalog.All
            .Select(info => info.Code)
            .Where(code => code.StartsWith("SCRIPTS-TYPE-", StringComparison.Ordinal)
                || code.StartsWith("SCRIPTS-LINT-", StringComparison.Ordinal)
                || (code.StartsWith("SCRIPTS-MODULE-", StringComparison.Ordinal) && code is not (ErrorCodes.Module0001 or ErrorCodes.Module0002 or ErrorCodes.Module0003 or ErrorCodes.Module0005 or ErrorCodes.Module0007)))
            .Where(code => !SkippedCodes.Contains(code))
            .Order(StringComparer.Ordinal)];

    public static IReadOnlyList<string> AllNames() =>
        [.. Directory.GetFiles(Repository.ConformanceErrorsDirectory, "*.scs")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(IsSemanticCase)
            .Order(StringComparer.Ordinal)];

    public static IEnumerable<object[]> AllNamesAsData() => AllNames().Select(name => new object[] { name });

    public static CheckerCase Load(string name)
    {
        var text = File.ReadAllText(Path.Combine(Repository.ConformanceErrorsDirectory, name + ".scs"), new System.Text.UTF8Encoding(false));
        var expectation = ExpectationPattern().Match(text);
        Assert.True(expectation.Success, "Missing expectation line in " + name);
        var auxiliaryFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        var auxiliaryDirectory = Path.Combine(Repository.ConformanceErrorsDirectory, name + ".files");
        if (Directory.Exists(auxiliaryDirectory))
        {
            foreach (var path in Directory.GetFiles(auxiliaryDirectory))
            {
                auxiliaryFiles[Path.GetFileName(path)] = File.ReadAllText(path, new System.Text.UTF8Encoding(false));
            }
        }

        return new CheckerCase(
            name,
            expectation.Groups["code"].Value,
            expectation.Groups["warning"].Success,
            int.Parse(expectation.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(expectation.Groups["column"].Value, System.Globalization.CultureInfo.InvariantCulture),
            expectation.Groups["file"].Success ? expectation.Groups["file"].Value : CheckerCase.MainFileName,
            text,
            auxiliaryFiles);
    }

    private static bool IsSemanticCase(string name) =>
        name.StartsWith("SCRIPTS-TYPE-", StringComparison.Ordinal)
        || name.StartsWith("SCRIPTS-LINT-", StringComparison.Ordinal)
        || (name.StartsWith("SCRIPTS-MODULE-", StringComparison.Ordinal) && !LoadPhaseModuleCodes.Contains(name));

    [GeneratedRegex(@"^#\s*expect-skip\s*:\s*(?<code>SCRIPTS-[A-Z]+-\d{4})")]
    private static partial Regex SkippedCodePattern();

    [GeneratedRegex(@"^# expect(?<warning>-warning)?: (?<code>SCRIPTS-[A-Z]+-\d{4}) (?<line>\d+):(?<column>\d+)(?: file=(?<file>\S+))?")]
    private static partial Regex ExpectationPattern();
}

public sealed class ConformanceTests
{
    [Fact]
    public void The_corpus_has_one_case_per_semantic_code()
    {
        var expected = CheckerCases.SemanticCodes();
        // Active semantic codes: TYPE 68 (70 minus the reserved SCRIPTS-TYPE-0060/0061),
        // LINT 11 (13 minus the retired SCRIPTS-LINT-0008/0009), MODULE 5.
        Assert.Equal(68 + 11 + 5, expected.Count);
        Assert.Equal(expected, CheckerCases.AllNames());
    }

    [Theory]
    [MemberData(nameof(CheckerCases.AllNamesAsData), MemberType = typeof(CheckerCases))]
    public void Each_case_reports_exactly_the_documented_diagnostic(string name)
    {
        var testCase = CheckerCases.Load(name);
        var result = testCase.Run();
        var actual = testCase.IsWarning ? result.Warnings : result.Errors;
        Assert.True(actual.Count == 1, $"{name}: expected exactly one {(testCase.IsWarning ? "warning" : "error")} but got: {Checking.Describe(result)}");
        var diagnostic = actual[0];
        Assert.Equal(
            (testCase.ExpectedCode, testCase.Line, testCase.Column, testCase.ExpectedFileName),
            (diagnostic.Code, diagnostic.Start.Line, diagnostic.Start.Column, Path.GetFileName(diagnostic.FilePath)));
        if (testCase.IsWarning)
        {
            Assert.Empty(result.Errors);
        }
        else
        {
            Assert.Empty(result.Warnings);
        }
    }

    [Fact]
    public void Every_semantic_code_of_the_catalog_is_produced_by_the_corpus()
    {
        var produced = new HashSet<string>(StringComparer.Ordinal);
        var unfilled = new List<string>();
        foreach (var name in CheckerCases.AllNames())
        {
            var result = CheckerCases.Load(name).Run();
            foreach (var diagnostic in result.Errors.Concat(result.Warnings))
            {
                produced.Add(diagnostic.Code);
                if (Checking.HasUnfilledPlaceholder(diagnostic))
                {
                    unfilled.Add($"{name}: {diagnostic.Message} / {diagnostic.Cause} / {diagnostic.Suggestion}");
                }
            }
        }

        var missing = CheckerCases.AllNames().Where(name => !produced.Contains(name)).ToList();
        Assert.True(missing.Count == 0, "Codes never produced: " + string.Join(", ", missing));
        Assert.True(unfilled.Count == 0, "Unfilled placeholders: " + string.Join(" | ", unfilled));
    }
}
