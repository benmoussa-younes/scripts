using System.Text;
using System.Text.RegularExpressions;
using Scripts.Core;

namespace Scripts.Syntax.Tests;

/// <summary>One trigger example extracted from (tests/Corpus/errors), with generated inputs expanded.</summary>
internal sealed record ConformanceCase(
    string Name,
    string ExpectedCode,
    int Line,
    int Column,
    string? ExpectedFileName,
    byte[] MainScript,
    IReadOnlyDictionary<string, byte[]> AuxiliaryFiles)
{
    public const string MainFileName = "main.scs";

    public static IReadOnlySet<string> LoadPhaseModuleCodes { get; } =
        new HashSet<string> { ErrorCodes.Module0001, ErrorCodes.Module0002, ErrorCodes.Module0003, ErrorCodes.Module0005, ErrorCodes.Module0007 };

    public bool IsLoadPhase => ExpectedCode.StartsWith("SCRIPTS-PARSE-", StringComparison.Ordinal) || LoadPhaseModuleCodes.Contains(ExpectedCode);

    public InMemorySourceProvider ToInMemoryProvider()
    {
        var provider = new InMemorySourceProvider().AddBytes("/work/" + MainFileName, MainScript);
        foreach (var (fileName, bytes) in AuxiliaryFiles)
        {
            provider.AddBytes("/work/" + fileName, bytes);
        }

        return provider;
    }

    public string WriteToDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, MainFileName), MainScript);
        foreach (var (fileName, bytes) in AuxiliaryFiles)
        {
            File.WriteAllBytes(Path.Combine(directory, fileName), bytes);
        }

        return Path.Combine(directory, MainFileName);
    }
}

internal static partial class ConformanceCases
{
    private const string FillerLine = "# remplissage\n";

    public static IReadOnlyList<string> AllNames() =>
        Directory.GetFiles(Repository.ConformanceErrorsDirectory, "*.scs")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(name => name.StartsWith("SCRIPTS-PARSE-", StringComparison.Ordinal) || name.StartsWith("SCRIPTS-MODULE-", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

    public static ConformanceCase Load(string name)
    {
        var scriptPath = Path.Combine(Repository.ConformanceErrorsDirectory, name + ".scs");
        var text = File.ReadAllText(scriptPath, new UTF8Encoding(false));
        var expectation = ExpectationPattern().Match(text);
        Assert.True(expectation.Success, "Missing `# expect:` line in " + name);
        var mainScript = ExpandMarkers(text);
        var auxiliaryFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var auxiliaryDirectory = Path.Combine(Repository.ConformanceErrorsDirectory, name + ".files");
        if (Directory.Exists(auxiliaryDirectory))
        {
            foreach (var path in Directory.GetFiles(auxiliaryDirectory))
            {
                auxiliaryFiles[Path.GetFileName(path)] = File.ReadAllBytes(path);
            }
        }

        if (name == "SCRIPTS-PARSE-0004")
        {
            mainScript = PadBeyondSizeLimit(mainScript);
        }

        if (name == "SCRIPTS-MODULE-0003")
        {
            foreach (var (fileName, bytes) in GenerateImportChain(33))
            {
                auxiliaryFiles[fileName] = bytes;
            }
        }

        return new ConformanceCase(
            name,
            expectation.Groups["code"].Value,
            int.Parse(expectation.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(expectation.Groups["column"].Value, System.Globalization.CultureInfo.InvariantCulture),
            expectation.Groups["file"].Success ? expectation.Groups["file"].Value : null,
            mainScript,
            auxiliaryFiles);
    }

    /// <summary>m1.scs … m{count}.scs as specified for SCRIPTS-MODULE-0003: each imports the next one on line 2; the last only declares the function.</summary>
    public static IReadOnlyDictionary<string, byte[]> GenerateImportChain(int count)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var index = 1; index <= count; index++)
        {
            var importLine = index < count ? $"import \"m{index + 1}.scs\" as chain\n" : string.Empty;
            files[$"m{index}.scs"] = Encoding.UTF8.GetBytes($"# file: m{index}.scs\n{importLine}public function value() -> integer {{ return {index} }}\n");
        }

        return files;
    }

    public static byte[] PadBeyondSizeLimit(byte[] script)
    {
        var builder = new StringBuilder();
        while (script.Length + builder.Length <= Limits.MaximumSourceBytes)
        {
            builder.Append(FillerLine);
        }

        return [.. script, .. Encoding.ASCII.GetBytes(builder.ToString())];
    }

    /// <summary>Replaces the markers of: &lt;0xFF&gt;, &lt;NUL&gt;, &lt;CR&gt; and &lt;repeat N "s"&gt;.</summary>
    public static byte[] ExpandMarkers(string text)
    {
        using var output = new MemoryStream();
        var cursor = 0;
        foreach (Match marker in MarkerPattern().Matches(text))
        {
            output.Write(Encoding.UTF8.GetBytes(text.Substring(cursor, marker.Index - cursor)));
            if (marker.Groups["count"].Success)
            {
                var repeated = Encoding.UTF8.GetBytes(marker.Groups["unit"].Value);
                for (var repetition = 0; repetition < int.Parse(marker.Groups["count"].Value, System.Globalization.CultureInfo.InvariantCulture); repetition++)
                {
                    output.Write(repeated);
                }
            }
            else
            {
                output.WriteByte(marker.Value switch { "<0xFF>" => 0xFF, "<NUL>" => 0x00, _ => 0x0D });
            }

            cursor = marker.Index + marker.Length;
        }

        output.Write(Encoding.UTF8.GetBytes(text.Substring(cursor)));
        return output.ToArray();
    }

    [GeneratedRegex(@"^# expect: (?<code>SCRIPTS-[A-Z]+-\d{4}) (?<line>\d+):(?<column>\d+)(?: file=(?<file>\S+))?", RegexOptions.None)]
    private static partial Regex ExpectationPattern();

    [GeneratedRegex(@"<0xFF>|<NUL>|<CR>|<repeat (?<count>\d+) ""(?<unit>[^""]*)"">")]
    private static partial Regex MarkerPattern();
}

public sealed class ConformanceTests
{
    /// <summary>Codes whose trigger example is `# expect-skip` have no executable case (DocGen skips them).</summary>
    private static readonly HashSet<string> SkippedCodes = File
        .ReadLines(Path.Combine(Repository.ConformanceErrorsDirectory, "SKIPPED.txt"))
        .Select(line => line.Split('\t')[0])
        .Where(code => code.StartsWith("SCRIPTS-", StringComparison.Ordinal))
        .Where(code => !File.Exists(Path.Combine(Repository.ConformanceErrorsDirectory, code + ".scs"))
            && !File.Exists(Path.Combine(Repository.ConformanceErrorsDirectory, code + ".cmd")))
        .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<string> RequiredParseCodes() =>
        ErrorCatalog.All
            .Select(info => info.Code)
            .Where(code => code.StartsWith("SCRIPTS-PARSE-", StringComparison.Ordinal) && !SkippedCodes.Contains(code))
            .Order(StringComparer.Ordinal)
            .ToList();

    public static IEnumerable<object[]> LoadPhaseCaseNames() =>
        ConformanceCases.AllNames().Where(name => ConformanceCases.Load(name).IsLoadPhase).Select(name => new object[] { name });

    public static IEnumerable<object[]> SemanticModuleCaseNames() =>
        ConformanceCases.AllNames().Where(name => !ConformanceCases.Load(name).IsLoadPhase).Select(name => new object[] { name });

    [Fact]
    public void The_corpus_covers_every_parse_code_and_every_module_code()
    {
        var names = ConformanceCases.AllNames();
        var expectedParse = RequiredParseCodes();
        Assert.Equal(54, expectedParse.Count);
        Assert.Equal(
            ["SCRIPTS-PARSE-0036", "SCRIPTS-PARSE-0037", "SCRIPTS-PARSE-0038", "SCRIPTS-PARSE-0050"],
            SkippedCodes.Where(code => code.StartsWith("SCRIPTS-PARSE-", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(expectedParse, names.Where(name => name.StartsWith("SCRIPTS-PARSE-", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(10, names.Count(name => !name.StartsWith("SCRIPTS-PARSE-", StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(LoadPhaseCaseNames))]
    public void Load_phase_cases_report_the_exact_code_and_position_from_memory(string name)
    {
        var testCase = ConformanceCases.Load(name);
        AssertDiagnostic(testCase, ProgramLoader.Load("/work/" + ConformanceCase.MainFileName, testCase.ToInMemoryProvider()));
    }

    [Theory]
    [MemberData(nameof(LoadPhaseCaseNames))]
    public void Load_phase_cases_report_the_same_diagnostic_from_real_files(string name)
    {
        var testCase = ConformanceCases.Load(name);
        var directory = Path.Combine(Path.GetTempPath(), "scripts-syntax-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var scriptPath = testCase.WriteToDirectory(directory);
            AssertDiagnostic(testCase, ProgramLoader.Load(scriptPath, new FileSystemSourceProvider()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(SemanticModuleCaseNames))]
    public void Semantic_module_cases_load_successfully_and_are_left_to_the_checker(string name)
    {
        var testCase = ConformanceCases.Load(name);
        var result = ProgramLoader.Load("/work/" + ConformanceCase.MainFileName, testCase.ToInMemoryProvider());
        Assert.True(result.Error is null, Parsing.Describe(result.Error));
        Assert.NotNull(result.Program);
    }

    [Fact]
    public void Every_parse_code_and_every_load_phase_module_code_is_triggered_by_the_corpus()
    {
        var produced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in ConformanceCases.AllNames())
        {
            var testCase = ConformanceCases.Load(name);
            var result = ProgramLoader.Load("/work/" + ConformanceCase.MainFileName, testCase.ToInMemoryProvider());
            if (result.Error is not null)
            {
                produced.Add(result.Error.Code);
            }
        }

        var required = RequiredParseCodes()
            .Concat(ConformanceCase.LoadPhaseModuleCodes)
            .Order(StringComparer.Ordinal)
            .ToList();
        var missing = required.Where(code => !produced.Contains(code)).ToList();
        Assert.True(missing.Count == 0, "Codes never produced by the corpus: " + string.Join(", ", missing));
    }

    private static void AssertDiagnostic(ConformanceCase testCase, LoadResult result)
    {
        Assert.Null(result.Program);
        var error = Assert.IsType<Diagnostic>(result.Error);
        var expectedFile = testCase.ExpectedFileName ?? ConformanceCase.MainFileName;
        Assert.Equal(
            (testCase.ExpectedCode, testCase.Line, testCase.Column, expectedFile),
            (error.Code, error.Start.Line, error.Start.Column, Path.GetFileName(error.FilePath)));
    }
}
