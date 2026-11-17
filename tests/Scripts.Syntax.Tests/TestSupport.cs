using System.Text;
using Scripts.Core;
using Scripts.Syntax;

namespace Scripts.Syntax.Tests;

internal static class Repository
{
    public static string Root { get; } = FindRoot();

    public static string ConformanceErrorsDirectory => Path.Combine(Root, "tests", "Corpus", "errors");

    public static string MigrationDirectory => Path.Combine(Root, "tests", "Corpus", "migration");

    public static string ErrorsSpecificationPath => Path.Combine(Root, "");

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

internal static class Parsing
{
    public static SourceFile Source(string text, string path = "test.scs") => new(path, text);

    public static ProgramNode Parse(string text, bool isModule = false)
    {
        var result = Parser.Parse(Source(text), isModule);
        Assert.True(result.Error is null, Describe(result.Error));
        return result.Program!;
    }

    public static Diagnostic Fail(string text, bool isModule = false)
    {
        var result = Parser.Parse(Source(text), isModule);
        Assert.Null(result.Program);
        Assert.NotNull(result.Error);
        return result.Error;
    }

    public static void AssertError(string text, string code, int line, int column, bool isModule = false)
    {
        var error = Fail(text, isModule);
        Assert.Equal((code, line, column), (error.Code, error.Start.Line, error.Start.Column));
    }

    /// <summary>Parses a program and renders its statements, one per line.</summary>
    public static string Print(string text, bool isModule = false) => AstPrinter.Print(Parse(text, isModule));

    /// <summary>Parses `let _ = expression` and renders only the expression.</summary>
    public static string PrintExpression(string expression)
    {
        var program = Parse("let _ = " + expression);
        return AstPrinter.Print(((LetStatement)program.Statements[0]).Initializer);
    }

    public static string Describe(Diagnostic? error) =>
        error is null ? string.Empty : $"{error.Code} {error.Start.Line}:{error.Start.Column} {error.Message}";

    public static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    public static T OnSmallStack<T>(Func<T> action, int stackBytes = 256 * 1024)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    result = action();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            },
            stackBytes);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("Action failed on the small stack", failure);
        }

        return result!;
    }
}
