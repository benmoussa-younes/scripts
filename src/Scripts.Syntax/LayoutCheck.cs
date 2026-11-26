using System.Text;
using Scripts.Core;

namespace Scripts.Syntax;

/// <summary>One canonical-layout violation in a source file (P9c).</summary>
public sealed record LayoutIssue(int Line, int Column, string Message);

/// <summary>
/// <c>scripts format [--check]</c> (DESIGN §9): the canonical **layout** of a scripts source. <see cref="Check"/>
/// reports the violations without touching a token; <see cref="Canonicalize"/> rewrites exactly those violations.
/// The rules are deliberately local so the formatter never changes meaning: UTF-8 without BOM, LF only, a final
/// newline, no tab in layout (a tab in string data — a normal, raw or path literal, including a nested string inside an
/// interpolation — is data; a tab in an interpolation expression or a comment is layout), no trailing whitespace
/// outside raw-string content (the opening line and the interior lines), empty blank lines (at most one in a row), and
/// indentation of exactly four spaces per open brace.
/// A line whose first token is inside an open <c>(</c> or <c>[</c> is a continuation and keeps its author indentation.
/// Token spacing and line breaks are preserved: this is a layout formatter, not a re-printer.
/// </summary>
public static class LayoutCheck
{
    private const int IndentWidth = 4;

    /// <summary>Columns count Unicode code points (DESIGN §8.2), never UTF-16 code units.</summary>
    private static int CodePointCount(string line) => line.EnumerateRunes().Count();

    /// <summary>Checks the layout of <paramref name="text"/>; a lexical error is returned as an issue and stops the scan.</summary>
    public static IReadOnlyList<LayoutIssue> Check(string text)
    {
        var issues = new List<LayoutIssue>();
        var source = new SourceFile("<layout>", text);
        var tokens = Tokenize(source, issues);
        if (tokens is null)
        {
            return issues;
        }

        var lines = text.Split('\n');
        var firstTokens = FirstTokensByLine(tokens, source);
        var stringLines = StringInteriorLines(tokens, source);
        var rawContentLineEnds = RawStringContentLineEnds(tokens, source);
        var rawClosingLines = RawStringClosingLines(tokens, source);
        var stringSpans = StringTextSpansByLine(tokens, source, lines);
        var endsWithNewline = text.Length == 0 || text.EndsWith('\n');
        var lastLine = endsWithNewline ? lines.Length - 1 : lines.Length;

        var consecutiveBlank = 0;
        for (var index = 0; index < lastLine; index++)
        {
            var line = lines[index];
            var lineNumber = index + 1;
            if (stringLines.Contains(lineNumber))
            {
                continue;
            }

            if (line.EndsWith('\r'))
            {
                if (!rawContentLineEnds.Contains(lineNumber))
                {
                    issues.Add(new LayoutIssue(lineNumber, CodePointCount(line), "CRLF line ending: use LF"));
                }

                line = line[..^1];
            }

            if (line.Length == 0)
            {
                consecutiveBlank++;
                if (consecutiveBlank > 1)
                {
                    issues.Add(new LayoutIssue(lineNumber, 1, "more than one consecutive blank line"));
                }

                if (index == 0)
                {
                    issues.Add(new LayoutIssue(lineNumber, 1, "blank line at the start of the file"));
                }

                continue;
            }

            consecutiveBlank = 0;
            var spans = stringSpans.TryGetValue(lineNumber, out var lineSpans) ? lineSpans : null;
            // The closing line of a multi-line raw string starts inside its content: its leading whitespace is data,
            // not layout, so neither the indentation rule nor the leading-tab rule applies there.
            var leadingIsContent = IsInsideString(spans, 0) || rawClosingLines.Contains(lineNumber);
            var indentEnd = 0;
            while (indentEnd < line.Length && line[indentEnd] is ' ' or '\t')
            {
                indentEnd++;
            }

            if (!leadingIsContent && line[..indentEnd].Contains('\t'))
            {
                issues.Add(new LayoutIssue(lineNumber, 1, "tab in indentation: use spaces"));
            }

            // Columns count code points (DESIGN §8.2). The leading whitespace is ASCII, so the scan starts at its
            // length plus one and advances one column per code point: a line with many tabs stays linear.
            var column = indentEnd + 1;
            var cursor = indentEnd;
            while (cursor < line.Length)
            {
                if (line[cursor] == '\t' && !IsInsideString(spans, cursor))
                {
                    issues.Add(new LayoutIssue(lineNumber, column, "tab: use spaces"));
                }

                cursor += char.IsHighSurrogate(line[cursor]) && cursor + 1 < line.Length && char.IsLowSurrogate(line[cursor + 1]) ? 2 : 1;
                column++;
            }

            if (line[^1] is ' ' or '\t' && !rawContentLineEnds.Contains(lineNumber))
            {
                issues.Add(new LayoutIssue(lineNumber, CodePointCount(line), "trailing whitespace"));
            }

            if (leadingIsContent)
            {
                continue;
            }

            if (!firstTokens.TryGetValue(index + 1, out var first)
                || first.ParenthesisDepth > 0
                || first.BracketDepth > 0
                || first.Kind == TokenKind.Dot)
            {
                // A continuation inside ( or [, and a method chain that starts a line with '.', keep the author
                // indentation: the language does not impose one there.
                continue;
            }

            var expectedDepth = first.Kind == TokenKind.RightBrace ? first.BraceDepth - 1 : first.BraceDepth;
            var expected = Math.Max(expectedDepth, 0) * IndentWidth;
            if (indentEnd != expected)
            {
                issues.Add(new LayoutIssue(
                    lineNumber,
                    1,
                    $"expected {expected.ToString(System.Globalization.CultureInfo.InvariantCulture)} spaces of indentation, found {indentEnd.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
            }
        }

        if (!endsWithNewline)
        {
            issues.Add(new LayoutIssue(lines.Length, CodePointCount(lines[^1]) + 1, "missing final newline"));
        }

        // `\n\n` ends with an empty content line before the final terminator: that is a trailing blank line.
        if (lines.Length >= 2 && lines[^1].Length == 0 && lines[^2].Length == 0)
        {
            issues.Add(new LayoutIssue(lines.Length - 1, 1, "blank line at the end of the file"));
        }

        return issues;
    }

    /// <summary>
    /// Returns <paramref name="text"/> rewritten to the canonical layout, or null when the source cannot be tokenized.
    /// The rewrite applies exactly the rules <see cref="Check"/> reports, and only to layout outside string data, so
    /// the meaning of the program is preserved.
    /// </summary>
    public static string? Canonicalize(string text)
    {
        var source = new SourceFile("<layout>", text);
        var ignoredIssues = new List<LayoutIssue>();
        var tokens = Tokenize(source, ignoredIssues);
        if (tokens is null)
        {
            return null;
        }

        var lines = text.Split('\n');
        var firstTokens = FirstTokensByLine(tokens, source);
        var stringLines = StringInteriorLines(tokens, source);
        var rawContentLineEnds = RawStringContentLineEnds(tokens, source);
        var rawClosingLines = RawStringClosingLines(tokens, source);
        var stringSpans = StringTextSpansByLine(tokens, source, lines);
        var endsWithNewline = text.Length == 0 || text.EndsWith('\n');
        var lastLine = endsWithNewline ? lines.Length - 1 : lines.Length;

        var canonicalLines = new List<string>(lastLine);
        var previousBlank = false;
        for (var index = 0; index < lastLine; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index];
            if (stringLines.Contains(lineNumber))
            {
                // A string's interior line is data: every layout rule is skipped.
                canonicalLines.Add(line);
                previousBlank = false;
                continue;
            }

            var lineEndIsContent = rawContentLineEnds.Contains(lineNumber);
            if (line.EndsWith('\r') && !lineEndIsContent)
            {
                line = line[..^1];
            }

            if (line.Length == 0)
            {
                if (canonicalLines.Count == 0 || previousBlank)
                {
                    continue;
                }

                canonicalLines.Add(line);
                previousBlank = true;
                continue;
            }

            var spans = stringSpans.TryGetValue(lineNumber, out var lineSpans) ? lineSpans : null;
            var leadingIsContent = IsInsideString(spans, 0) || rawClosingLines.Contains(lineNumber);
            var indentEnd = 0;
            while (indentEnd < line.Length && line[indentEnd] is ' ' or '\t')
            {
                indentEnd++;
            }

            if (!leadingIsContent
                && firstTokens.TryGetValue(lineNumber, out var first)
                && first.ParenthesisDepth == 0
                && first.BracketDepth == 0
                && first.Kind != TokenKind.Dot)
            {
                var expectedDepth = first.Kind == TokenKind.RightBrace ? first.BraceDepth - 1 : first.BraceDepth;
                line = new string(' ', Math.Max(expectedDepth, 0) * IndentWidth) + line[indentEnd..];
            }

            if (line.Contains('\t'))
            {
                line = ExpandLayoutTabs(line, spans);
            }

            if (!lineEndIsContent)
            {
                line = line.TrimEnd(' ', '\t');
            }

            canonicalLines.Add(line);
            previousBlank = false;
        }

        // A trailing blank line is layout whether or not the file ended with a newline.
        while (canonicalLines.Count > 0 && canonicalLines[^1].Length == 0)
        {
            canonicalLines.RemoveAt(canonicalLines.Count - 1);
        }

        return canonicalLines.Count == 0 ? string.Empty : string.Join('\n', canonicalLines) + '\n';
    }

    private static string ExpandLayoutTabs(string line, List<(int Start, int End)>? spans)
    {
        var builder = new StringBuilder(line.Length);
        for (var index = 0; index < line.Length; index++)
        {
            builder.Append(line[index] == '\t' && !IsInsideString(spans, index) ? ' ' : line[index]);
        }

        return builder.ToString();
    }

    private sealed record LayoutToken(
        TokenKind Kind,
        int Start,
        int End,
        int BraceDepth,
        int ParenthesisDepth,
        int BracketDepth,
        bool IsRawString,
        IReadOnlyList<StringSegment>? Segments);

    /// <summary>Tokenizes while recording the nesting depths **before** each token; null after a lexical error.</summary>
    private static List<LayoutToken>? Tokenize(SourceFile source, List<LayoutIssue> issues)
    {
        var lexer = new Lexer(source);
        var tokens = new List<LayoutToken>();
        var braces = 0;
        var parentheses = 0;
        var brackets = 0;
        while (true)
        {
            var token = lexer.ReadToken();
            if (token.Kind == TokenKind.EndOfFile)
            {
                return tokens;
            }

            if (token.Kind == TokenKind.Error || token.Failure is not null)
            {
                var position = source.GetPosition(token.Start);
                issues.Add(new LayoutIssue(position.Line, position.Column, "the source cannot be tokenized"));
                return null;
            }

            tokens.Add(new LayoutToken(
                token.Kind,
                token.Start,
                token.End,
                braces,
                parentheses,
                brackets,
                token is StringToken { Style: StringStyle.Raw },
                token is StringToken stringToken ? stringToken.Segments : null));
            switch (token.Kind)
            {
                case TokenKind.LeftBrace:
                    braces++;
                    break;
                case TokenKind.RightBrace:
                    braces = Math.Max(braces - 1, 0);
                    break;
                case TokenKind.LeftParenthesis:
                    parentheses++;
                    break;
                case TokenKind.RightParenthesis:
                    parentheses = Math.Max(parentheses - 1, 0);
                    break;
                case TokenKind.LeftBracket:
                    brackets++;
                    break;
                case TokenKind.RightBracket:
                    brackets = Math.Max(brackets - 1, 0);
                    break;
            }
        }
    }

    private static Dictionary<int, LayoutToken> FirstTokensByLine(List<LayoutToken> tokens, SourceFile source)
    {
        var first = new Dictionary<int, LayoutToken>();
        foreach (var token in tokens)
        {
            if (token.Kind == TokenKind.NewLine)
            {
                continue;
            }

            var line = source.GetPosition(token.Start).Line;
            first.TryAdd(line, token);
        }

        return first;
    }

    private sealed record StringTokenSpan(int Start, int End, bool IsRaw);

    /// <summary>
    /// Every string literal token of the file — normal, raw or path — including a string nested in an interpolation.
    /// The line-level rules (interior lines, raw content line ends, raw closing lines) need the nested multi-line raw
    /// string as much as the top-level one.
    /// </summary>
    private static IEnumerable<StringTokenSpan> StringTokenSpans(List<LayoutToken> tokens)
    {
        foreach (var token in tokens)
        {
            if (token.Segments is null)
            {
                continue;
            }

            if (token.Kind == TokenKind.String)
            {
                yield return new StringTokenSpan(token.Start, token.End, token.IsRawString);
            }

            foreach (var span in NestedStringTokenSpans(token.Segments))
            {
                yield return span;
            }
        }
    }

    private static IEnumerable<StringTokenSpan> NestedStringTokenSpans(IReadOnlyList<StringSegment> segments)
    {
        foreach (var segment in segments)
        {
            if (segment is not InterpolationSegment interpolation)
            {
                continue;
            }

            foreach (var token in interpolation.Tokens)
            {
                if (token is not StringToken stringToken)
                {
                    continue;
                }

                yield return new StringTokenSpan(stringToken.Start, stringToken.End, stringToken.Style == StringStyle.Raw);
                foreach (var span in NestedStringTokenSpans(stringToken.Segments))
                {
                    yield return span;
                }
            }
        }
    }

    private static HashSet<int> StringInteriorLines(List<LayoutToken> tokens, SourceFile source)
    {
        var lines = new HashSet<int>();
        foreach (var token in StringTokenSpans(tokens))
        {
            // Only the strictly interior lines are content. The opening and closing lines carry layout after the
            // token, so they stay under the scan.
            var start = source.GetPosition(token.Start).Line;
            var end = source.GetPosition(token.End).Line;
            for (var line = start + 1; line < end; line++)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    /// <summary>
    /// Lines of a multi-line raw string whose end-of-line is string content: the opening line from <c>r"""</c> to the
    /// end of the line, and every interior line. The closing line is excluded: after its last quote, trailing
    /// whitespace and CRLF are layout and must be reported.
    /// </summary>
    private static HashSet<int> RawStringContentLineEnds(List<LayoutToken> tokens, SourceFile source)
    {
        var lines = new HashSet<int>();
        foreach (var token in StringTokenSpans(tokens))
        {
            if (!token.IsRaw)
            {
                continue;
            }

            var start = source.GetPosition(token.Start).Line;
            var end = source.GetPosition(token.End).Line;
            if (start == end)
            {
                continue;
            }

            for (var line = start; line < end; line++)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    /// <summary>Closing lines of a multi-line raw string: their leading whitespace is content, not indentation.</summary>
    private static HashSet<int> RawStringClosingLines(List<LayoutToken> tokens, SourceFile source)
    {
        var lines = new HashSet<int>();
        foreach (var token in StringTokenSpans(tokens))
        {
            if (!token.IsRaw)
            {
                continue;
            }

            var start = source.GetPosition(token.Start).Line;
            var end = source.GetPosition(token.End).Line;
            if (start != end)
            {
                lines.Add(end);
            }
        }

        return lines;
    }

    /// <summary>
    /// The character ranges covered by the literal text of a string on each line. Only <see cref="TextSegment"/>s are
    /// masked: a tab in an interpolation expression is code whitespace and stays a layout violation, while a tab in
    /// the string data is not reported. A nested string token inside an interpolation is data too, so its text
    /// segments are added recursively; the interpolation's own tokens are never masked as a whole.
    /// </summary>
    private static Dictionary<int, List<(int Start, int End)>> StringTextSpansByLine(List<LayoutToken> tokens, SourceFile source, string[] lines)
    {
        var lineStarts = new int[lines.Length];
        var offset = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            lineStarts[index] = offset;
            offset += lines[index].Length + 1;
        }

        var spans = new Dictionary<int, List<(int Start, int End)>>();
        foreach (var token in tokens)
        {
            if (token.Segments is not null)
            {
                AddTextSpans(token.Segments, source, lines, lineStarts, spans);
            }
        }

        return spans;
    }

    private static void AddTextSpans(
        IReadOnlyList<StringSegment> segments,
        SourceFile source,
        string[] lines,
        int[] lineStarts,
        Dictionary<int, List<(int Start, int End)>> spans)
    {
        foreach (var segment in segments)
        {
            if (segment is TextSegment text)
            {
                if (text.End <= text.Start)
                {
                    continue;
                }

                var startLine = source.GetPosition(text.Start).Line;
                var endLine = source.GetPosition(text.End - 1).Line;
                for (var line = startLine; line <= endLine; line++)
                {
                    var lineStart = lineStarts[line - 1];
                    var localStart = Math.Max(text.Start, lineStart) - lineStart;
                    var localEnd = Math.Min(text.End, lineStart + lines[line - 1].Length) - lineStart;
                    if (localEnd > localStart)
                    {
                        AddSpan(spans, line, localStart, localEnd);
                    }
                }

                continue;
            }

            if (segment is InterpolationSegment interpolation)
            {
                foreach (var token in interpolation.Tokens)
                {
                    if (token is StringToken stringToken)
                    {
                        AddTextSpans(stringToken.Segments, source, lines, lineStarts, spans);
                    }
                }
            }
        }
    }

    private static void AddSpan(Dictionary<int, List<(int Start, int End)>> spans, int line, int start, int end)
    {
        if (!spans.TryGetValue(line, out var list))
        {
            list = [];
            spans[line] = list;
        }

        list.Add((start, end));
    }

    private static bool IsInsideString(List<(int Start, int End)>? spans, int offset)
    {
        if (spans is null)
        {
            return false;
        }

        foreach (var span in spans)
        {
            if (offset >= span.Start && offset < span.End)
            {
                return true;
            }
        }

        return false;
    }
}
