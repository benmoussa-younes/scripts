namespace Scripts.Core;

public sealed record OperatorInfo(string Symbol, string Category, string Description);

/// <summary>One row of the precedence table (DESIGN 2.1); associativity is left, right, none or prefix.</summary>
public sealed record PrecedenceLevel(int Level, IReadOnlyList<string> Symbols, string Associativity);

public sealed record TypeDescription(string Name, string Syntax, string Category, string Description);

/// <summary>Exit code or inclusive range of exit codes (LastCode equals Code for a single code).</summary>
public sealed record ExitCodeInfo(int Code, int LastCode, string Name, string Meaning);

public sealed record LimitInfo(string Name, long Value);

/// <summary>
/// Static reference tables of the language (DESIGN 1.7, 2.1, 3.1, 5.8, 6) shared by describe and the help text.
/// <see cref="Version"/> is the one member not declared here: it is generated at build time from git
/// (see <c>Directory.Build.targets</c> and <c>obj/Generated/BuildVersion.g.cs</c>).
/// </summary>
public static partial class LanguageReference
{
    /// <summary>Canonical language name printed by <c>--version</c>, <c>describe</c> and the help text.</summary>
    public const string LanguageName = "scripts";

    /// <summary>Product name shown next to the language name in the help text.</summary>
    public const string ProductName = "Scripts";

    public const int SchemaVersion = 1;

    public static readonly IReadOnlyList<OperatorInfo> Operators =
    [
        new("+", "arithmetic", "addition; string or list concatenation"),
        new("-", "arithmetic", "subtraction; unary negation"),
        new("*", "arithmetic", "multiplication"),
        new("/", "arithmetic", "division (integers: truncated toward zero); path joining"),
        new("%", "arithmetic", "remainder (sign of the dividend)"),
        new("==", "comparison", "structural equality"),
        new("!=", "comparison", "difference"),
        new("<", "comparison", "strictly less than"),
        new("<=", "comparison", "less than or equal"),
        new(">", "comparison", "strictly greater than"),
        new(">=", "comparison", "greater than or equal"),
        new("and", "logical", "short-circuit logical and"),
        new("or", "logical", "short-circuit logical or"),
        new("not", "logical", "logical negation"),
        new("=", "assignment", "assignment"),
        new("+=", "assignment", "compound assignment by addition"),
        new("-=", "assignment", "compound assignment by subtraction"),
        new("*=", "assignment", "compound assignment by multiplication"),
        new("/=", "assignment", "compound assignment by division"),
        new("%=", "assignment", "compound assignment by remainder"),
        new("->", "type", "function return type and function type"),
        new("=>", "value", "lambda body; separates a pattern from its result in a match arm"),
        new("?", "result", "propagates the error of a result to the current function"),
        new(".", "member", "access to a field, a method or a module member"),
        new(",", "punctuation", "separator for elements or arguments"),
        new(":", "punctuation", "separator for a type, a map key or a parameter"),
        new("|", "punctuation", "alternative of patterns in a match"),
        new("(", "punctuation", "opens a call, a parameter list or a grouped expression"),
        new(")", "punctuation", "closes a parenthesis"),
        new("[", "punctuation", "opens a list literal or an index"),
        new("]", "punctuation", "closes a bracket"),
        new("{", "punctuation", "opens a block, a map literal or an interpolation"),
        new("}", "punctuation", "closes a brace"),
    ];

    public static readonly IReadOnlyList<PrecedenceLevel> Precedence =
    [
        new(1, ["=>"], "right"),
        new(2, ["or"], "left"),
        new(3, ["and"], "left"),
        new(4, ["not"], "prefix"),
        new(5, ["==", "!=", "<", "<=", ">", ">="], "none"),
        new(6, ["+", "-"], "left"),
        new(7, ["*", "/", "%"], "left"),
        new(8, ["-"], "prefix"),
        new(9, ["()", "[]", ".", "?"], "left"),
    ];

    public static readonly IReadOnlyList<TypeDescription> Types =
    [
        new("string", "string", "primitive", "opaque Unicode text"),
        new("integer", "integer", "primitive", "64-bit signed integer, checked arithmetic"),
        new("float", "float", "primitive", "IEEE-754 binary64 float, always finite"),
        new("boolean", "boolean", "primitive", "true or false; only type accepted in conditions"),
        new("path", "path", "primitive", "file path, a type distinct from string"),
        new("unit", "unit", "primitive", "absence of a value"),
        new("never", "never", "primitive", "type of process.exit_with_code, compatible with any expected type, not writable"),
        new("list", "list<T>", "generic", "ordered homogeneous list, value semantics"),
        new("map", "map<K, V>", "generic", "insertion-ordered table, keys string, integer, path or boolean"),
        new("result", "result<T, E>", "generic", "ok(T) or error(E)"),
        new("function", "function(A, B) -> R", "function", "function or closure"),
    ];

    public static readonly IReadOnlyList<ExitCodeInfo> ExitCodeTable =
    [
        new(ExitCodes.Success, ExitCodes.Success, "success", "success (check: valid script; process.exit_with_code(0))"),
        new(1, ExitCodes.MaximumScriptExitCode, "exit_script", "code chosen by the script with process.exit_with_code(n)"),
        new(ExitCodes.Usage, ExitCodes.Usage, "usage", "command line usage error"),
        new(ExitCodes.StaticError, ExitCodes.StaticError, "static_error", "static error (syntax, types, modules; or a warning under --deny-warnings)"),
        new(ExitCodes.ScriptUnreadable, ExitCodes.ScriptUnreadable, "script_unreadable", "unreadable or missing script"),
        new(ExitCodes.RuntimeFailure, ExitCodes.RuntimeFailure, "runtime_failure", "fatal runtime error or unhandled error at top level"),
        new(ExitCodes.InternalError, ExitCodes.InternalError, "internal_error", "internal error"),
        new(ExitCodes.Hangup, ExitCodes.Hangup, "hangup", "interrupted by SIGHUP"),
        new(ExitCodes.Interrupted, ExitCodes.Interrupted, "interrupted", "interrupted by SIGINT"),
        new(ExitCodes.BrokenPipe, ExitCodes.BrokenPipe, "broken_pipe", "write to an output closed by the reader (EPIPE), silent exit"),
        new(ExitCodes.Terminated, ExitCodes.Terminated, "terminated", "interrupted by SIGTERM"),
    ];

    public static readonly IReadOnlyList<LimitInfo> LimitTable =
    [
        new("maximum_source_bytes", Limits.MaximumSourceBytes),
        new("maximum_syntax_depth", Limits.MaximumSyntaxDepth),
        new("maximum_import_depth", Limits.MaximumImportDepth),
        new("maximum_call_depth", Limits.MaximumCallDepth),
        new("maximum_value_depth", Limits.MaximumValueDepth),
        new("maximum_string_length_utf16_units", Limits.MaximumCollectionLength),
        new("maximum_collection_length", Limits.MaximumCollectionLength),
        new("maximum_range_length", Limits.MaximumRangeLength),
        new("maximum_read_file_bytes", Limits.MaximumReadFileBytes),
        new("maximum_script_exit_code", ExitCodes.MaximumScriptExitCode),
    ];
}
