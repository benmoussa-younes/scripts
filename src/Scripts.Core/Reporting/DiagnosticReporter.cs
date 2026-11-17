using System.Text;
using Scripts.Core;
using Scripts.Core.Json;

namespace Scripts.Core.Reporting;

/// <summary>Renders diagnostics as human text (DESIGN 8.2) or as machine JSON (DESIGN 8.3, 8.4).</summary>
public static class DiagnosticReporter
{
    private const char Newline = '\n';

    /// <summary>One diagnostic, every line terminated by a newline.</summary>
    public static string RenderHuman(Diagnostic diagnostic)
    {
        var builder = new StringBuilder();
        AppendHuman(builder, diagnostic);
        return builder.ToString();
    }

    /// <summary>Errors then warnings, one blank line between diagnostics, every line terminated by a newline.</summary>
    public static string RenderHuman(IReadOnlyList<Diagnostic> errors, IReadOnlyList<Diagnostic> warnings)
    {
        var builder = new StringBuilder();
        foreach (var diagnostic in errors.Concat(warnings))
        {
            if (builder.Length > 0)
            {
                builder.Append(Newline);
            }

            AppendHuman(builder, diagnostic);
        }

        return builder.ToString();
    }

    /// <summary>One diagnostic object with the fixed key order of DESIGN 8.3.</summary>
    public static string RenderDiagnosticJson(Diagnostic diagnostic)
    {
        var writer = new JsonWriter();
        WriteDiagnosticJson(writer, diagnostic);
        return writer.ToString();
    }

    public static void WriteDiagnosticJson(JsonWriter writer, Diagnostic diagnostic)
    {
        var hasPosition = diagnostic.File is not null;
        var (startLine, startColumn) = diagnostic.Start;
        var (endLine, endColumn) = diagnostic.End;

        writer.BeginObject();
        writer.WriteProperty("code", diagnostic.Code);
        writer.WriteProperty("severity", diagnostic.Severity == Severity.Error ? "error" : "warning");
        writer.WriteProperty("message", diagnostic.Message);
        writer.WriteProperty("file", hasPosition ? diagnostic.FilePath : null);
        writer.WriteProperty("line", hasPosition ? startLine : (long?)null);
        writer.WriteProperty("column", hasPosition ? startColumn : (long?)null);
        writer.WriteProperty("end_line", hasPosition ? endLine : (long?)null);
        writer.WriteProperty("end_column", hasPosition ? endColumn : (long?)null);
        writer.WriteProperty("source_line", hasPosition ? diagnostic.SourceLine : null);
        writer.WriteProperty("cause", diagnostic.Cause);
        writer.WriteProperty("suggestion", diagnostic.Suggestion);
        if (diagnostic.Fix is { } fix && diagnostic.File is not null)
        {
            // Optional key (DESIGN §8.3 stability policy): a machine can apply the edit without parsing prose.
            var (fixLine, fixColumn) = diagnostic.File.GetPosition(fix.Span.Start);
            var (fixEndLine, fixEndColumn) = diagnostic.File.GetPosition(fix.Span.End);
            writer.WriteName("fix");
            writer.BeginObject();
            writer.WriteProperty("description", fix.Description);
            writer.WriteName("edits");
            writer.BeginArray();
            writer.BeginObject();
            writer.WriteProperty("line", fixLine);
            writer.WriteProperty("column", fixColumn);
            writer.WriteProperty("end_line", fixEndLine);
            writer.WriteProperty("end_column", fixEndColumn);
            writer.WriteProperty("replacement", fix.Replacement);
            writer.EndObject();
            writer.EndArray();
            writer.EndObject();
        }

        writer.EndObject();
    }

    /// <summary>
    /// The <c>--errors-json</c> document on one line, without trailing newline; <c>success</c> is
    /// <c>errors.Count == 0</c>, or <c>false</c> when <paramref name="denyWarnings"/> and a warning exists
    /// (the <c>check --deny-warnings</c> switch, DESIGN 8.5).
    /// </summary>
    public static string RenderErrorsJsonDocument(IReadOnlyList<Diagnostic> errors, IReadOnlyList<Diagnostic> warnings, bool denyWarnings = false)
    {
        var writer = new JsonWriter();
        writer.BeginObject();
        writer.WriteProperty("schema_version", LanguageReference.SchemaVersion);
        writer.WriteProperty("success", errors.Count == 0 && !(denyWarnings && warnings.Count > 0));
        WriteDiagnosticArrays(writer, errors, warnings);
        writer.EndObject();
        return writer.ToString();
    }

    /// <summary>
    /// The <c>run --json-out</c> document on one line, without trailing newline. <c>success</c> is
    /// <c>exitCode == 0 &amp;&amp; errors.Count == 0</c>; <paramref name="resultJson"/> is an already rendered JSON value
    /// (null for an absent or <c>unit</c> result).
    /// </summary>
    public static string RenderRunJsonDocument(
        int exitCode,
        RunTermination termination,
        string standardOutput,
        string standardError,
        string? resultJson,
        IReadOnlyList<Diagnostic> errors,
        IReadOnlyList<Diagnostic> warnings)
    {
        var writer = new JsonWriter();
        writer.BeginObject();
        writer.WriteProperty("schema_version", LanguageReference.SchemaVersion);
        writer.WriteProperty("exit_code", exitCode);
        writer.WriteProperty("success", exitCode == 0 && errors.Count == 0);
        writer.WriteProperty("termination", termination.ToJsonName());
        writer.WriteProperty("stdout", standardOutput);
        writer.WriteProperty("stderr", standardError);
        writer.WriteName("result");
        if (resultJson is null)
        {
            writer.WriteNull();
        }
        else
        {
            writer.WriteRawJson(resultJson);
        }

        WriteDiagnosticArrays(writer, errors, warnings);
        writer.EndObject();
        return writer.ToString();
    }

    private static void WriteDiagnosticArrays(JsonWriter writer, IReadOnlyList<Diagnostic> errors, IReadOnlyList<Diagnostic> warnings)
    {
        writer.WriteName("errors");
        writer.BeginArray();
        foreach (var error in errors)
        {
            WriteDiagnosticJson(writer, error);
        }

        writer.EndArray();
        writer.WriteName("warnings");
        writer.BeginArray();
        foreach (var warning in warnings)
        {
            WriteDiagnosticJson(writer, warning);
        }

        writer.EndArray();
    }

    private static void AppendHuman(StringBuilder builder, Diagnostic diagnostic)
    {
        var label = diagnostic.Severity == Severity.Error ? "error" : "warning";
        // The human format is one line per field: reuse the source-excerpt sanitizer for the three text fields.
        builder.Append(label).Append('[').Append(diagnostic.Code).Append("]: ").Append(MakePrintable(diagnostic.Message)).Append(Newline);

        var lineNumberWidth = 1;
        if (diagnostic.File is not null)
        {
            var (startLine, startColumn) = diagnostic.Start;
            var (endLine, endColumn) = diagnostic.End;
            var lineNumberText = startLine.ToString(System.Globalization.CultureInfo.InvariantCulture);
            lineNumberWidth = lineNumberText.Length;
            var gutter = new string(' ', lineNumberWidth);
            // DESIGN §8.2: the quoted line is bounded. A generated or minified script can hold one line of tens of
            // thousands of code points; echoing it in full for every diagnostic made the rendering quadratic and ended
            // in an internal error (254) instead of the diagnostic. The window keeps the underlined region and marks
            // each cut side with `…`.
            var excerpt = Excerpt(diagnostic, startLine, startColumn, endLine, endColumn);
            var sourceLine = excerpt.Text;

            builder.Append(' ').Append(gutter).Append("--> ").Append(MakePrintable(diagnostic.FilePath))
                .Append(':').Append(startLine).Append(':').Append(startColumn).Append(Newline);
            builder.Append(' ').Append(gutter).Append(" |").Append(Newline);
            builder.Append(' ').Append(lineNumberText).Append(" |");
            // The caret row is computed before the line is appended: a StringBuilder append evaluates its argument
            // in place, so building it after the text is already in the builder corrupted the indentation. The caret
            // starts at the underline's column WITHIN the quoted line (the excerpt may begin far into the real line).
            var carets = Math.Max(1, Math.Min(excerpt.UnderlineLength, CountCodePoints(sourceLine) - excerpt.CaretColumn + 1));
            var caretRow = BuildUnderlineIndent(sourceLine, excerpt.CaretColumn).Append('^', carets);
            if (sourceLine.Length > 0)
            {
                builder.Append(' ').Append(MakePrintable(sourceLine));
            }

            builder.Append(Newline);

            builder.Append(' ').Append(gutter).Append(" | ").Append(caretRow.ToString()).Append(Newline);
        }

        var margin = new string(' ', lineNumberWidth);
        if (diagnostic.Cause.Length > 0)
        {
            builder.Append(' ').Append(margin).Append(" = cause: ").Append(MakePrintable(diagnostic.Cause)).Append(Newline);
        }

        if (diagnostic.Suggestion.Length > 0)
        {
            builder.Append(' ').Append(margin).Append(" = suggestion: ").Append(MakePrintable(diagnostic.Suggestion)).Append(Newline);
        }
    }

    /// <summary>Code points of the quoted line kept around the underlined region (DESIGN §8.2).</summary>
    private const int QuotedLineCodePoints = 120;

    /// <summary>
    /// The part of the source line shown under the header and the extent of the underline in it. A line at most
    /// <see cref="QuotedLineCodePoints"/> long is shown whole; a longer one is cut to a window centred on the
    /// underlined region, with a single `…` on each cut side. <see cref="StartColumn"/> is the 1-based column of the
    /// first shown character, so <see cref="BuildUnderlineIndent"/> puts the carets on the right character.
    /// </summary>
    private static (int StartColumn, string Text, int UnderlineLength, int CaretColumn) Excerpt(
        Diagnostic diagnostic, int startLine, int startColumn, int endLine, int endColumn)
    {
        var line = diagnostic.SourceLine;
        var length = CountCodePoints(line);
        var underlineLength = startLine == endLine
            ? Math.Max(1, endColumn - startColumn)
            : Math.Max(1, length - startColumn + 1);
        if (length <= QuotedLineCodePoints)
        {
            // The whole line is shown: the caret starts at the diagnostic's own column within it.
            return (1, line, underlineLength, startColumn);
        }

        // 40 code points of context before the underline, then as much of the line as fits.
        const int ContextBefore = 40;
        var textStart = Math.Max(0, startColumn - 1 - ContextBefore);
        var textEnd = Math.Min(length, textStart + QuotedLineCodePoints);
        var hasPrefix = textStart > 0;
        var hasSuffix = textEnd < length;
        var text = (hasPrefix ? "…" : string.Empty)
            + SliceByCodePoints(line, textStart, textEnd)
            + (hasSuffix ? "…" : string.Empty);
        // Column of the caret WITHIN the quoted text: one-based offset of the underlined character, shifted by the
        // `…` prefix when one was added.
        var caretColumn = startColumn - textStart + (hasPrefix ? 1 : 0);
        return (1, text, underlineLength, caretColumn);
    }

    /// <summary>Substring between two 0-based code-point indices; a surrogate pair is never split.</summary>
    private static string SliceByCodePoints(string text, int startCodePoint, int endCodePoint)
    {
        var start = 0;
        for (var index = 0; index < startCodePoint && start < text.Length; index++)
        {
            start += CodePointLength(text, start);
        }

        var end = start;
        for (var index = startCodePoint; index < endCodePoint && end < text.Length; index++)
        {
            end += CodePointLength(text, end);
        }

        return text[start..end];
    }

    // One space per code point, but tabs stay tabs so the carets line up under the printed source line.
    private static StringBuilder BuildUnderlineIndent(string sourceLine, int startColumn)
    {
        var indent = new StringBuilder();
        var charIndex = 0;
        for (var currentColumn = 1; currentColumn < startColumn; currentColumn++)
        {
            if (charIndex < sourceLine.Length)
            {
                indent.Append(sourceLine[charIndex] == '\t' ? '\t' : ' ');
                charIndex += CodePointLength(sourceLine, charIndex);
            }
            else
            {
                indent.Append(' ');
            }
        }

        return indent;
    }

    private static string MakePrintable(string text)
    {
        var printable = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (char.IsHighSurrogate(current) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                // A valid astral character is kept as its surrogate pair.
                printable.Append(current).Append(text[++index]);
            }
            else if (char.IsSurrogate(current) || (Diagnostics.IsUnprintableLineCharacter(current) && current != '\t'))
            {
                // Tabs stay tabs so the carets line up under the printed source line.
                printable.Append('\uFFFD');
            }
            else
            {
                printable.Append(current);
            }
        }

        return printable.ToString();
    }

    private static int CountCodePoints(string text)
    {
        var count = 0;
        for (var charIndex = 0; charIndex < text.Length; charIndex += CodePointLength(text, charIndex))
        {
            count++;
        }

        return count;
    }

    private static int CodePointLength(string text, int charIndex) =>
        char.IsHighSurrogate(text[charIndex]) && charIndex + 1 < text.Length && char.IsLowSurrogate(text[charIndex + 1]) ? 2 : 1;
}
