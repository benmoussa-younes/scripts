namespace Scripts.Core;

/// <summary>Process exit codes of the scripts binary (DESIGN 6).</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Usage = 250;
    public const int StaticError = 251;
    public const int ScriptUnreadable = 252;
    public const int RuntimeFailure = 253;
    public const int InternalError = 254;
    public const int Hangup = 129;
    public const int Interrupted = 130;
    public const int BrokenPipe = 141;
    public const int Terminated = 143;
    public const int MaximumScriptExitCode = 255;
}

/// <summary>Language limits (DESIGN 1.1, 4.3, 5.1, 5.8).</summary>
public static class Limits
{
    public const int MaximumSourceBytes = 4 * 1024 * 1024;
    public const int MaximumSyntaxDepth = 200;
    public const int MaximumImportDepth = 32;
    public const int MaximumCallDepth = 10_000;
    public const int MaximumValueDepth = 1_000;
    public const int MaximumCollectionLength = 1 << 28;
    public const int MaximumRangeLength = 10_000_000;
    public const long MaximumReadFileBytes = 256L * 1024 * 1024;
}

/// <summary>Static language tables shared by the lexer, checker and describe.</summary>
public static class LanguageTables
{
    public static readonly string[] Keywords =
    [
        "let", "mutable", "function", "return", "if", "else", "while", "for", "in", "match", "break", "continue",
        "import", "as", "public", "private", "true", "false", "and", "or", "not",
    ];

    public static readonly string[] ReservedIdentifiers = [.. StdlibCatalog.IntrinsicModuleNames, "ok", "error"];

    public static readonly string[] FutureReserved =
    [
        "async", "await", "struct", "enum", "type", "try", "catch",
    ];

    public static bool IsKeyword(string text) => Array.IndexOf(Keywords, text) >= 0;

    public static bool IsReservedIdentifier(string text) => Array.IndexOf(ReservedIdentifiers, text) >= 0;

    public static bool IsFutureReserved(string text) => Array.IndexOf(FutureReserved, text) >= 0;
}
