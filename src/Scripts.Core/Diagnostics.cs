using System.Text;

namespace Scripts.Core;

public enum Severity
{
    Error,
    Warning,
}

/// <summary>Immutable catalog entry parsed from (see tools/Scripts.DocGen).</summary>
public sealed record ErrorInfo(
    string Code,
    string Category,
    Severity Severity,
    string Message,
    string Cause,
    string Suggestion,
    string Position,
    string Status = "active");

/// <summary>
/// A machine-applicable correction: replace <see cref="Span"/> with <see cref="Replacement"/> in the same file.
/// Only a safe fix (an unambiguous rename or literal) is applied by <c>scripts check --fix</c>.
/// </summary>
public sealed record Fix(string Description, SourceFile? File, Span Span, string Replacement);

/// <summary>One reported problem; messages are English, codes are stable.</summary>
public sealed record Diagnostic(
    string Code,
    Severity Severity,
    string Message,
    string Cause,
    string Suggestion,
    SourceFile? File,
    Span Span,
    Fix? Fix = null)
{
    public string FilePath => File?.Path ?? string.Empty;

    public (int Line, int Column) Start => File is null ? (0, 0) : File.GetPosition(Span.Start);

    public (int Line, int Column) End => File is null ? (0, 0) : File.GetPosition(Span.End);

    public string SourceLine => File is null ? string.Empty : File.GetLineText(Start.Line);

    public string Category => Code.Split('-')[1].ToLowerInvariant();
}

/// <summary>Builds diagnostics from catalog templates (named placeholders such as {expected}).</summary>
public static class Diagnostics
{
    /// <summary>Upper bound of a rendered placeholder value (UTF-16 units), before the trailing U+2026.</summary>
    private const int MaximumDisplayedValueLength = 256;

    /// <summary>True when a character must not appear raw in a one-line diagnostic (control or line/paragraph separator).</summary>
    internal static bool IsUnprintableLineCharacter(char value) =>
        char.IsControl(value) || value is '\u2028' or '\u2029';

    public static Diagnostic Create(string code, SourceFile? file, Span span, params (string Name, string Value)[] arguments) =>
        Create(code, file, span, null, arguments);

    public static Diagnostic Create(string code, SourceFile? file, Span span, Fix? fix, params (string Name, string Value)[] arguments)
    {
        var info = ErrorCatalog.Get(code);
        return new Diagnostic(
            info.Code,
            info.Severity,
            Render(info.Message, arguments),
            Render(info.Cause, arguments),
            Render(info.Suggestion, arguments),
            file,
            span,
            fix);
    }

    public static string Render(string template, (string Name, string Value)[] arguments)
    {
        if (arguments.Length == 0 || template.IndexOf('{', StringComparison.Ordinal) < 0)
        {
            return template;
        }

        var result = template;
        foreach (var (name, value) in arguments)
        {
            result = result.Replace("{" + name + "}", value, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>
    /// Printable value for a diagnostic placeholder: the empty string renders as <c>""</c>; control
    /// characters, line/paragraph separators and unpaired surrogates become U+FFFD, and a value longer
    /// than <see cref="MaximumDisplayedValueLength"/> is truncated with a trailing U+2026. The rendered
    /// value stays one line and valid UTF-8, and its allocation is bounded.
    /// </summary>
    public static string PrintableValue(string text)
    {
        if (text.Length == 0)
        {
            return "\"\"";
        }

        var truncated = text.Length > MaximumDisplayedValueLength;
        var limit = Math.Min(text.Length, MaximumDisplayedValueLength);
        var printable = new StringBuilder(limit + 1);
        for (var index = 0; index < text.Length && printable.Length < limit; index++)
        {
            var current = text[index];
            if (char.IsHighSurrogate(current) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                if (printable.Length + 2 > limit)
                {
                    truncated = true;
                    break;
                }

                printable.Append(current).Append(text[++index]);
            }
            else if (char.IsSurrogate(current) || IsUnprintableLineCharacter(current))
            {
                printable.Append('\uFFFD');
            }
            else
            {
                printable.Append(current);
            }
        }

        if (truncated)
        {
            printable.Append('…');
        }

        return printable.ToString();
    }

    /// <summary>Stable ordering used for all static diagnostics: file load order, then line, then column, then code.</summary>
    public static int Compare(Diagnostic left, Diagnostic right, Func<SourceFile?, int> fileOrder)
    {
        var byFile = fileOrder(left.File).CompareTo(fileOrder(right.File));
        if (byFile != 0)
        {
            return byFile;
        }

        var byStart = left.Span.Start.CompareTo(right.Span.Start);
        if (byStart != 0)
        {
            return byStart;
        }

        return string.CompareOrdinal(left.Code, right.Code);
    }
}

/// <summary>Lookup over the error catalogue (Catalogue/ErrorCatalogData.cs).</summary>
public static class ErrorCatalog
{
    private static readonly Lazy<Dictionary<string, ErrorInfo>> Index = new(() =>
    {
        var dictionary = new Dictionary<string, ErrorInfo>(StringComparer.Ordinal);
        foreach (var info in ErrorCatalogData.All)
        {
            dictionary[info.Code] = info;
        }

        return dictionary;
    });

    public static IReadOnlyList<ErrorInfo> All => ErrorCatalogData.All;

    public static bool TryGet(string code, out ErrorInfo info) => Index.Value.TryGetValue(code, out info!);

    public static ErrorInfo Get(string code) =>
        Index.Value.TryGetValue(code, out var info)
            ? info
            : throw new InvalidOperationException($"unknown error code: {code}");
}
