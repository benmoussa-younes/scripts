using Scripts.Core;

namespace Scripts.Syntax.Tests;

public sealed class MigrationCorpusTests
{
    public static IEnumerable<object[]> ScriptNames() =>
        Directory.GetFiles(Repository.MigrationDirectory, "*.scs").Order(StringComparer.Ordinal).Select(path => new object[] { Path.GetFileName(path) });

    [Fact]
    public void The_migration_corpus_has_twenty_one_scripts()
    {
        Assert.Equal(21, ScriptNames().Count());
    }

    [Theory]
    [MemberData(nameof(ScriptNames))]
    public void Every_migration_script_parses_as_a_main_script(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Repository.MigrationDirectory, name));
        var source = SourceReading.Read(name, bytes);
        Assert.True(source.Error is null, Parsing.Describe(source.Error));
        var result = Parser.Parse(source.Source!, isModule: false);
        Assert.True(result.Error is null, name + ": " + Parsing.Describe(result.Error));
        Assert.NotEmpty(result.Program!.Statements);
    }

    [Theory]
    [MemberData(nameof(ScriptNames))]
    public void Every_migration_script_loads_from_the_file_system(string name)
    {
        var result = ProgramLoader.Load(Path.Combine(Repository.MigrationDirectory, name), new FileSystemSourceProvider());
        Assert.True(result.Error is null, name + ": " + Parsing.Describe(result.Error));
        Assert.Empty(result.Program!.Modules);
    }

    [Theory]
    [MemberData(nameof(ScriptNames))]
    public void Migration_scripts_parse_identically_with_crlf_line_endings(string name)
    {
        var text = File.ReadAllText(Path.Combine(Repository.MigrationDirectory, name));
        var crlf = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.Equal(Parsing.Print(text), Parsing.Print(crlf));
    }
}
