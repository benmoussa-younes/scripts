using Scripts.Core;
using Scripts.Syntax;

namespace Scripts.Checker.Tests;

public sealed partial class CorpusTests
{
    public static IEnumerable<object[]> MigrationFiles() =>
        Directory.GetFiles(Repository.MigrationDirectory, "*.scs").Order(StringComparer.Ordinal).Select(path => new object[] { Path.GetFileName(path) });

    public static IEnumerable<object[]> StdlibEntryIndexes() =>
        StdlibCatalog.All.Select((entry, position) => new object[] { position, entry.Signature });

    [Fact]
    public void The_migration_corpus_has_twenty_one_scripts()
    {
        Assert.Equal(21, Directory.GetFiles(Repository.MigrationDirectory, "*.scs").Length);
    }

    /// <summary>
    /// The normative count in DESIGN §7.10 must agree with the catalog and sum its own parts
    /// (global + module + methods = total) — the drift the B3 adversarial audit found (F1).
    /// </summary>

    /// <summary>
    /// Every catalog `list` method of DESIGN §7.7 must have its verbatim signature in DESIGN (normative signatures);
    /// guards the drift the B1 adversarial audit found (F1: §7.7 omitted `collect` and the eight class-1 methods).
    /// </summary>

    [Theory]
    [MemberData(nameof(MigrationFiles))]
    public void Migration_scripts_check_without_errors(string fileName)
    {
        var path = Path.Combine(Repository.MigrationDirectory, fileName);
        var loaded = ProgramLoader.Load(path, new FileSystemSourceProvider());
        Assert.True(loaded.Error is null, Checking.Describe(loaded.Error));
        var result = ProgramChecker.Check(loaded.Program!);
        Assert.True(result.Errors.Count == 0, fileName + ": " + Checking.Describe(result));
    }

    [Fact]
    public void Migration_scripts_produce_only_the_expected_warnings()
    {
        var warnings = new List<string>();
        foreach (var path in Directory.GetFiles(Repository.MigrationDirectory, "*.scs").Order(StringComparer.Ordinal))
        {
            var loaded = ProgramLoader.Load(path, new FileSystemSourceProvider());
            var result = ProgramChecker.Check(loaded.Program!);
            warnings.AddRange(result.Warnings.Select(warning => $"{Path.GetFileName(path)}: {warning.Code} {warning.Start.Line}:{warning.Start.Column}"));
        }

        Assert.Equal(ExpectedMigrationWarnings, warnings);
    }

    [Fact]
    public void Every_catalogue_entry_has_an_example()
    {
        Assert.All(StdlibCatalog.All, entry => Assert.False(string.IsNullOrWhiteSpace(entry.ExampleCode), entry.QualifiedName));
    }

    [Theory]
    [MemberData(nameof(StdlibEntryIndexes))]
    public void Stdlib_examples_check_without_errors_and_only_the_documented_warning(int entryIndex, string signature)
    {
        var entry = StdlibCatalog.All[entryIndex];
        var result = Checking.Check(entry.ExampleCode);
        var expectedWarnings = entry is { ModuleName: "process", Name: "run_shell" } ? new[] { "SCRIPTS-LINT-0002" } : [];
        Assert.True(
            result.Errors.Count == 0 && result.Warnings.Select(warning => warning.Code).SequenceEqual(expectedWarnings),
            $"{signature}: {Checking.Describe(result)}");
    }

    // `03-exit-status.scs` passes the program as a parameter (`process.request(program, …)`), the computed-program
    // case of SCRIPTS-LINT-0004.
    private static readonly string[] ExpectedMigrationWarnings = ["03-exit-status.scs: SCRIPTS-LINT-0004 5:47"];
}
