using System.Text;
using Scripts.Core;
using Scripts.Syntax;

namespace Scripts.Checker.Tests;

internal static class Repository
{
    public static string Root { get; } = FindRoot();

    public static string ConformanceErrorsDirectory => Path.Combine(Root, "tests", "Corpus", "errors");

    public static string MigrationDirectory => Path.Combine(Root, "tests", "Corpus", "migration");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Scripts.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Scripts.slnx not found above " + AppContext.BaseDirectory);
    }
}

internal static class Checking
{
    public const string MainPath = "/work/main.scs";

    public static LoadedProgram Load(string mainText, params (string Path, string Text)[] modules)
    {
        var provider = new InMemorySourceProvider().AddText(MainPath, mainText);
        foreach (var (path, text) in modules)
        {
            provider.AddText("/work/" + path, text);
        }

        var result = ProgramLoader.Load(MainPath, provider);
        Assert.True(result.Error is null, "load failed: " + Describe(result.Error));
        return result.Program!;
    }

    public static CheckResult Check(string mainText, params (string Path, string Text)[] modules) =>
        ProgramChecker.Check(Load(mainText, modules));

    public static string Describe(Diagnostic? diagnostic) =>
        diagnostic is null ? string.Empty : $"{diagnostic.Code} {diagnostic.Start.Line}:{diagnostic.Start.Column} {diagnostic.Message}";

    public static string Describe(CheckResult result) =>
        string.Join(" | ", result.Errors.Concat(result.Warnings).Select(Describe));

    public static IReadOnlyList<string> ErrorPositions(CheckResult result) =>
        [.. result.Errors.Select(error => $"{error.Code} {error.Start.Line}:{error.Start.Column}")];

    public static IReadOnlyList<string> WarningPositions(CheckResult result) =>
        [.. result.Warnings.Select(warning => $"{warning.Code} {warning.Start.Line}:{warning.Start.Column}")];

    /// <summary>Asserts the exact ordered list of "CODE line:column" errors (line 1 is the first line of <paramref name="mainText"/>).</summary>
    public static CheckResult AssertErrors(string mainText, params string[] expected) => AssertErrorsInModules(mainText, [], expected);

    public static CheckResult AssertErrorsInModules(string mainText, (string Path, string Text)[] modules, params string[] expected)
    {
        var result = Check(mainText, modules);
        Assert.True(
            expected.SequenceEqual(ErrorPositions(result)),
            $"expected [{string.Join(", ", expected)}] but got [{string.Join(", ", ErrorPositions(result))}]: {Describe(result)}");
        return result;
    }

    public static CheckResult AssertClean(string mainText, params (string Path, string Text)[] modules)
    {
        var result = Check(mainText, modules);
        Assert.True(result.Errors.Count == 0, "unexpected errors: " + Describe(result));
        return result;
    }

    /// <summary>The marker character precedes the start of each expected diagnostic; marks are removed before checking.</summary>
    public const char Mark = '\u2038';

    public static (string Text, List<(int Line, int Column)> Positions) StripMarks(string markedText)
    {
        var builder = new StringBuilder();
        var positions = new List<(int Line, int Column)>();
        var line = 1;
        var column = 1;
        foreach (var rune in markedText.EnumerateRunes())
        {
            if (rune.Value == Mark)
            {
                positions.Add((line, column));
                continue;
            }

            builder.Append(rune.ToString());
            if (rune.Value == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return (builder.ToString(), positions);
    }

    /// <summary>Asserts the ordered error codes at the marked positions.</summary>
    public static CheckResult AssertMarked(string markedText, params string[] codes) => AssertMarkedInModules(markedText, [], codes);

    public static CheckResult AssertMarkedInModules(string markedText, (string Path, string Text)[] modules, params string[] codes)
    {
        var (text, positions) = StripMarks(markedText);
        Assert.True(codes.Length == positions.Count, $"test bug: {codes.Length} codes for {positions.Count} marks");
        var expected = codes.Select((code, position) => $"{code} {positions[position].Line}:{positions[position].Column}").ToArray();
        return AssertErrorsInModules(text, modules, expected);
    }

    public static CheckResult AssertMarkedWarnings(string markedText, params string[] codes)
    {
        var (text, positions) = StripMarks(markedText);
        Assert.True(codes.Length == positions.Count, $"test bug: {codes.Length} codes for {positions.Count} marks");
        var result = Check(text);
        var expected = codes.Select((code, position) => $"{code} {positions[position].Line}:{positions[position].Column}").ToArray();
        Assert.True(result.Errors.Count == 0, "unexpected errors: " + Describe(result));
        Assert.True(expected.SequenceEqual(WarningPositions(result)), $"expected [{string.Join(", ", expected)}] but got [{string.Join(", ", WarningPositions(result))}]");
        return result;
    }

    public static ScriptType TypeOfLastLet(string mainText)
    {
        var program = Load(mainText);
        var result = ProgramChecker.Check(program);
        Assert.True(result.Errors.Count == 0, "unexpected errors: " + Describe(result));
        var lastLet = program.Entry.Program.Statements.OfType<LetStatement>().Last();
        return lastLet.Initializer.ResolvedType!;
    }

    public static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    /// <summary>
    /// True when a catalog placeholder such as {name} survives in the rendered texts (codes quoting source text are
    /// exempt). SCRIPTS-TYPE-0064 quotes `{…}`, `{N}` and `{name}` as literal examples, SCRIPTS-PARSE-0022 quotes the
    /// interpolated `{name}`/`{0}` forms, and SCRIPTS-PARSE-0023 quotes `{ … }` as the arm syntax, so their texts are not
    /// inspected.
    /// </summary>
    public static bool HasUnfilledPlaceholder(Diagnostic diagnostic) =>
        diagnostic.Code is not (ErrorCodes.Type0029 or ErrorCodes.Type0042 or ErrorCodes.Type0048 or ErrorCodes.Type0055 or ErrorCodes.Type0064 or ErrorCodes.Parse0022 or ErrorCodes.Parse0023)
        && System.Text.RegularExpressions.Regex.IsMatch(diagnostic.Message + diagnostic.Cause + diagnostic.Suggestion, @"\{[a-z_]+\}");
}
