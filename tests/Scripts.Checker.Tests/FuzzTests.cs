using System.Text;
using Scripts.Core;
using Scripts.Syntax;

namespace Scripts.Checker.Tests;

public sealed class FuzzTests
{
    private const int Iterations = 2000;
    private const int Seed = 20261013;
    private const string KnownBadStatement = "\nio.write_standard_output_line(scripts_fuzz_missing_name)\n";

    private static readonly string[] Fragments =
    [
        "let ", "mutable ", "function ", "if ", "else ", "match ", "return", "not ", "and ", "or ", "for ", "in ", "while ", "break", "continue",
        "{", "}", "(", ")", "[", "]", ",", ".", "?", ":", "|>", "->", "=>", "-", "=", "==", "+=", "<", "\"x\"", "1", "1.5", "true", "[]", "{}",
        "ok(1)", "error(\"e\")", "ok()", "process.request(\"sh\", [])", "process.run_shell(\"x\")", "x => x", "_", "path", "io.write_standard_output_line", "xs", "value", "result<integer, string>",
        "unit", "never", "public ", "import \"lib.scs\" as lib\n", "lib.f(1)", "\n",
    ];

    private static readonly HashSet<string> CatalogCodes = ErrorCatalog.All.Select(info => info.Code).ToHashSet(StringComparer.Ordinal);

    private static List<string> LoadCorpus()
    {
        var corpus = new List<string>();
        foreach (var path in Directory.GetFiles(Repository.MigrationDirectory, "*.scs"))
        {
            corpus.Add(File.ReadAllText(path));
        }

        foreach (var path in Directory.GetFiles(Repository.ConformanceErrorsDirectory, "*.scs", SearchOption.AllDirectories))
        {
            if (!path.Contains("PARSE-0001", StringComparison.Ordinal) && !path.Contains("PARSE-0002", StringComparison.Ordinal)
                && !path.Contains("PARSE-0003", StringComparison.Ordinal) && !path.Contains("PARSE-0004", StringComparison.Ordinal)
                && !path.Contains("PARSE-0005", StringComparison.Ordinal))
            {
                corpus.Add(File.ReadAllText(path));
            }
        }

        return corpus;
    }

    private static string Mutate(string original, Random random)
    {
        var text = new StringBuilder(original);
        var mutations = random.Next(1, 4);
        for (var mutation = 0; mutation < mutations && text.Length > 0; mutation++)
        {
            var position = random.Next(text.Length);
            switch (random.Next(5))
            {
                case 0:
                    text.Remove(position, Math.Min(random.Next(1, 12), text.Length - position));
                    break;
                case 1:
                    text.Insert(position, Fragments[random.Next(Fragments.Length)]);
                    break;
                case 2:
                    text[position] = Fragments[random.Next(Fragments.Length)][0];
                    break;
                case 3:
                    var length = Math.Min(random.Next(1, 30), text.Length - position);
                    text.Insert(random.Next(text.Length), text.ToString(position, length));
                    break;
                default:
                    text.Insert(position, Fragments[random.Next(Fragments.Length)] + Fragments[random.Next(Fragments.Length)]);
                    break;
            }
        }

        return text.ToString();
    }

    private static InMemorySourceProvider Provider(string main, string library) =>
        new InMemorySourceProvider()
            .AddText("/work/main.scs", main)
            .AddText("/work/lib.scs", library)
            .AddText("/work/other.scs", "public let value = 1\npublic function f(n: integer) -> integer {\n    return n\n}\n");

    [Fact]
    public void Mutated_programs_never_throw_and_report_consistent_catalogued_diagnostics()
    {
        var corpus = LoadCorpus();
        Assert.True(corpus.Count > 100);
        var random = new Random(Seed);
        var checkedPrograms = 0;
        var withErrors = 0;
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var main = Mutate(corpus[random.Next(corpus.Count)], random);
            var library = random.Next(3) == 0 ? Mutate(corpus[random.Next(corpus.Count)], random) : "public function f(n: integer) -> integer {\n    return n\n}\n";
            var loaded = ProgramLoader.Load("/work/main.scs", Provider(main, library));
            if (loaded.Program is null)
            {
                continue;
            }

            checkedPrograms++;
            var result = ProgramChecker.Check(loaded.Program);
            AssertConsistent(result, loaded.Program, iteration);
            if (result.Errors.Count > 0)
            {
                withErrors++;
            }

            var again = ProgramChecker.Check(loaded.Program);
            Assert.True(
                result.Errors.Select(error => (error.Code, error.Span)).SequenceEqual(again.Errors.Select(error => (error.Code, error.Span))),
                $"iteration {iteration}: checking twice gave different errors");
        }

        Assert.True(checkedPrograms > 300 && withErrors > 100, $"checked={checkedPrograms} withErrors={withErrors}");
    }

    [Fact]
    public void A_program_with_an_injected_error_never_reports_zero_errors()
    {
        var corpus = LoadCorpus();
        var random = new Random(Seed + 1);
        var injected = 0;
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var main = Mutate(corpus[random.Next(corpus.Count)], random) + KnownBadStatement;
            var loaded = ProgramLoader.Load("/work/main.scs", Provider(main, "public let value = 1\n"));
            if (loaded.Program is null)
            {
                continue;
            }

            injected++;
            var result = ProgramChecker.Check(loaded.Program);
            Assert.True(
                result.Errors.Any(error => error.Code == ErrorCodes.Type0001 && error.Span.Start >= main.Length - KnownBadStatement.Length),
                $"iteration {iteration}: the injected unknown name was not reported: {Checking.Describe(result)}");
        }

        Assert.True(injected > 300, $"injected={injected}");
    }

    [Fact]
    public void Random_token_soup_that_parses_never_throws()
    {
        var random = new Random(Seed + 2);
        var parsed = 0;
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var soup = string.Concat(Enumerable.Range(0, random.Next(1, 25)).Select(_ => Fragments[random.Next(Fragments.Length)] + (random.Next(2) == 0 ? " " : "\n")));
            var loaded = ProgramLoader.Load("/work/main.scs", Provider(soup, "public let value = 1\n"));
            if (loaded.Program is null)
            {
                continue;
            }

            parsed++;
            AssertConsistent(ProgramChecker.Check(loaded.Program), loaded.Program, iteration);
        }

        Assert.True(parsed > 5, $"parsed={parsed}");
    }

    private static void AssertConsistent(CheckResult result, LoadedProgram program, int iteration)
    {
        var fileOrder = program.AllFilesInLoadOrder().Select((file, position) => (file.Source, position)).ToDictionary(pair => pair.Source, pair => pair.position);
        foreach (var (diagnostics, severity) in new[] { (result.Errors, Severity.Error), (result.Warnings, Severity.Warning) })
        {
            for (var position = 0; position < diagnostics.Count; position++)
            {
                var diagnostic = diagnostics[position];
                Assert.True(CatalogCodes.Contains(diagnostic.Code), $"iteration {iteration}: unknown code {diagnostic.Code}");
                Assert.Equal(severity, diagnostic.Severity);
                Assert.True(
                    diagnostic.Code.StartsWith("SCRIPTS-TYPE-", StringComparison.Ordinal)
                    || diagnostic.Code.StartsWith("SCRIPTS-MODULE-", StringComparison.Ordinal)
                    || diagnostic.Code.StartsWith("SCRIPTS-LINT-", StringComparison.Ordinal),
                    $"iteration {iteration}: {diagnostic.Code} is not a semantic code");
                Assert.NotNull(diagnostic.File);
                Assert.True(diagnostic.Span.Start >= 0 && diagnostic.Span.End >= diagnostic.Span.Start && diagnostic.Span.End <= diagnostic.File.Text.Length, $"iteration {iteration}: bad span of {diagnostic.Code}");
                Assert.True(diagnostic.Start.Line >= 1 && diagnostic.Start.Column >= 1);
                Assert.False(Checking.HasUnfilledPlaceholder(diagnostic), $"iteration {iteration}: unfilled placeholder in {Checking.Describe(diagnostic)}");
                if (position > 0)
                {
                    var previous = diagnostics[position - 1];
                    Assert.True(
                        Diagnostics.Compare(previous, diagnostic, source => source is not null ? fileOrder[source] : -1) <= 0,
                        $"iteration {iteration}: diagnostics out of order: {Checking.Describe(previous)} then {Checking.Describe(diagnostic)}");
                }
            }
        }
    }
}
