using Scripts.Core;

namespace Scripts.Checker;

/// <summary>Lint rules of DESIGN 8.5 (SCRIPTS-LINT-0001 to SCRIPTS-LINT-0012).</summary>
internal static class LintRules
{
    private static readonly HashSet<string> LauncherPrograms = new(StringComparer.Ordinal)
    {
        "sh", "bash", "dash", "zsh", "csh", "scripts", "ksh", "tcsh", "env", "xargs", "busybox", "python", "python3", "perl", "ruby", "node",
    };

    private static readonly HashSet<string> ProgramLaunchingModuleFunctions = new(StringComparer.Ordinal)
    {
        "process.request", "process.spawn",
    };

    /// <summary>The two module functions whose lambda runs on several threads (DESIGN 7.10.1).</summary>
    private static readonly HashSet<string> ParallelLambdaHosts = new(StringComparer.Ordinal)
    {
        "parallel.transform", "parallel.walk_directory",
    };

    public static bool LaunchesProgram(StdlibEntry entry) =>
        entry.Kind == StdlibKind.ModuleFunction && ProgramLaunchingModuleFunctions.Contains(entry.QualifiedName);

    /// <summary>The string methods that cannot split on an empty separator (SCRIPTS-TYPE-0065, DESIGN 7.6).</summary>
    private static readonly HashSet<string> EmptySeparatorHosts = new(StringComparer.Ordinal)
    {
        "split", "split_regex",
    };

    /// <summary>
    /// The literal empty separator of a <c>split</c>/<c>split_regex</c> call: an empty string literal, normal or raw
    /// (<c>""</c>, <c>r""</c>, <c>r""""""</c>). A non-literal string is not flagged (SCRIPTS-TYPE-0065).
    /// </summary>
    public static StringLiteral? FindEmptySeparator(StdlibEntry entry, IReadOnlyList<Expression> arguments) =>
        entry.Kind == StdlibKind.Method
        && EmptySeparatorHosts.Contains(entry.Name)
        && arguments.Count == 1
        && arguments[0] is StringLiteral { Value.Length: 0 } literal
            ? literal
            : null;

    /// <summary>Only <c>process.request</c> takes a <c>standard_input</c> then a <c>working_directory</c> (SCRIPTS-LINT-0005).</summary>
    public static bool IsRequestContext(StdlibEntry entry) => entry.Kind == StdlibKind.ModuleFunction && entry.QualifiedName == "process.request";

    /// <summary>True for a literal string starting with <c>/</c>, <c>./</c> or <c>../</c>, hence reading like a directory.</summary>
    public static bool LooksLikeDirectory(StringLiteral literal) =>
        literal.Value.StartsWith('/') || literal.Value.StartsWith("./", StringComparison.Ordinal) || literal.Value.StartsWith("../", StringComparison.Ordinal);

    /// <summary>True for <c>parallel.transform</c> and <c>parallel.walk_directory</c>, whose lambdas run on several workers.</summary>
    public static bool IsParallelLambdaHost(StdlibEntry entry) =>
        entry.Kind == StdlibKind.ModuleFunction && ParallelLambdaHosts.Contains(entry.QualifiedName);

    /// <summary>The literal program name when its basename is an interpreter or launcher.</summary>
    public static string? FindLauncherProgram(Expression programArgument)
    {
        if (programArgument is not StringLiteral literal)
        {
            return null;
        }

        var lastSeparator = literal.Value.LastIndexOf('/');
        var baseName = lastSeparator < 0 ? literal.Value : literal.Value[(lastSeparator + 1)..];
        return LauncherPrograms.Contains(baseName) ? literal.Value : null;
    }

    /// <summary>True when a program argument is not a literal string (SCRIPTS-LINT-0004).</summary>
    public static bool IsComputedProgram(Expression programArgument) => programArgument is not StringLiteral;
}
