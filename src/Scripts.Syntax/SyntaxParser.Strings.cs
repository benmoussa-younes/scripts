using Scripts.Core;

namespace Scripts.Syntax;

internal sealed partial class SyntaxParser
{
    private Expression ParseStringExpression()
    {
        var token = (StringToken)Advance();
        var span = SpanOf(token);
        var parts = new List<StringPart>();
        foreach (var segment in token.Segments)
        {
            parts.Add(segment switch
            {
                TextSegment textSegment => new TextPart(new Span(textSegment.Start, textSegment.End), textSegment.Text),
                InterpolationSegment interpolation => new InterpolationPart(new Span(interpolation.Start, interpolation.End), ParseInterpolation(token, interpolation)),
                _ => throw new InvalidOperationException("Unknown string segment."),
            });
        }

        if (token.Failure is { } failure)
        {
            throw failure.Code == ErrorCodes.Parse0020 && StartsWithDoubleBrace(failure.Span.Start)
                && IsClosedSingleLineString(token)
                ? DoubleBrace(token, failure.Span.Start)
                : new SyntaxErrorException(failure);
        }

        if (token.Style == StringStyle.Path)
        {
            var contentStart = token.Start + 2;
            return new PathLiteral(span, parts.Count > 0 ? parts : [new TextPart(new Span(contentStart, contentStart), string.Empty)]);
        }

        if (parts.Exists(part => part is InterpolationPart))
        {
            return new InterpolatedString(span, parts);
        }

        return new StringLiteral(span, string.Concat(parts.Cast<TextPart>().Select(part => part.Text)));
    }

    /// <summary>
    /// L6: `{{` opens an interpolation whose expression starts with a map literal. When that interpolation does not parse,
    /// the author almost surely meant a literal brace (the Python/C#/Rust habit): PARSE-0059 replaces the first error of
    /// the segment. A valid `{{"a": 1}.length()}` is untouched, so no accepted program changes meaning.
    /// </summary>
    private Expression ParseInterpolation(StringToken token, InterpolationSegment segment)
    {
        if (!StartsWithDoubleBrace(segment.Start))
        {
            return ParseInterpolationTokens(segment);
        }

        try
        {
            return ParseInterpolationTokens(segment);
        }
        catch (SyntaxErrorException failure) when (failure.Diagnostic.Code != ErrorCodes.Parse0059
            && IsClosedSingleLineString(token))
        {
            throw DoubleBrace(token, segment.Start);
        }
    }

    private bool StartsWithDoubleBrace(int openBrace) =>
        openBrace + 1 < text.Length && text[openBrace] == '{' && text[openBrace + 1] == '{';

    /// <summary>
    /// True when the token is a normal string closed on its line: outside that shape a `{{` diagnostic would hide the
    /// real error (an unterminated interpolation) and no brace rewrite is safe.
    /// </summary>
    private bool IsClosedSingleLineString(StringToken token) =>
        token.Style == StringStyle.Normal
        && token.End - token.Start >= 2
        && text[token.Start] == '"'
        && text[token.End - 1] == '"'
        && text.AsSpan(token.Start + 1, token.End - token.Start - 2).IndexOfAny('\n', '\r') < 0;

    private SyntaxErrorException DoubleBrace(StringToken token, int openBrace)
    {
        var span = new Span(openBrace, openBrace + 2);
        var fix = EscapedBraces(token, openBrace) is { } replacement
            ? new Fix("write literal braces as `\\{` and `\\}`", source, new Span(token.Start, token.End), replacement)
            : null;
        return new SyntaxErrorException(Diagnostics.Create(ErrorCodes.Parse0059, source, span, fix));
    }

    /// <summary>The token with every brace of the failing segment escaped (`{{` collapses to `\{`), or null when the rewrite is unsafe.</summary>
    private string? EscapedBraces(StringToken token, int openBrace)
    {
        if (token.Style != StringStyle.Normal || token.End - token.Start < 2 || text[token.Start] != '"' || text[token.End - 1] != '"')
        {
            return null;
        }

        var content = text.AsSpan(token.Start + 1, token.End - token.Start - 2);
        if (content.IndexOfAny('"', '\n', '\r') >= 0)
        {
            return null;
        }

        var failing = token.Segments.FirstOrDefault(segment => segment is InterpolationSegment && segment.Start == openBrace);
        if (failing is null)
        {
            return null;
        }

        var builder = new System.Text.StringBuilder(content.Length + 2);
        builder.Append('"');
        foreach (var segment in token.Segments)
        {
            var end = Math.Min(segment.End, token.End - 1);
            var raw = text.AsSpan(segment.Start, Math.Max(0, end - segment.Start));
            if (segment == failing)
            {
                AppendEscapedBraces(builder, raw);
            }
            else
            {
                builder.Append(raw);
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static void AppendEscapedBraces(System.Text.StringBuilder builder, ReadOnlySpan<char> content)
    {
        for (var index = 0; index < content.Length; index++)
        {
            var current = content[index];
            if (current is '{' or '}')
            {
                // WHY: a single brace left in the segment would open an interpolation after the rewrite.
                builder.Append('\\').Append(current);
                if (index + 1 < content.Length && content[index + 1] == current)
                {
                    index++;
                }
            }
            else
            {
                builder.Append(current);
            }
        }
    }

    /// <summary>Parses the tokens collected by the lexer for one `{…}`; the surrounding token stream is restored afterwards.</summary>
    private Expression ParseInterpolationTokens(InterpolationSegment segment)
    {
        var interpolationTokens = segment.Tokens;
        if (interpolationTokens.Count == 0)
        {
            throw ErrorAt(ErrorCodes.Parse0019, segment.Start, segment.Start + 1);
        }

        if (IsBareLiteral(interpolationTokens))
        {
            var literal = text.Substring(interpolationTokens[0].Start, interpolationTokens[^1].End - interpolationTokens[0].Start);
            throw ErrorAt(
                ErrorCodes.Parse0022,
                segment.Start,
                segment.Start + 1,
                ("literal", literal),
                ("template", text.Substring(segment.Start, segment.End - segment.Start)));
        }

        var savedTokens = tokens;
        var savedPosition = position;
        var savedLexer = lexer;
        var savedPreviousEnd = previousEnd;
        var savedCloseMap = closeIndexByOpen;
        closeIndexByOpen = null;
        var closingBrace = segment.End - 1;
        tokens = [.. interpolationTokens, new Token(TokenKind.EndOfFile, closingBrace, closingBrace + 1, "}")];
        position = 0;
        lexer = null;
        PushContext(ContextKind.Interpolation, null);
        EnterNesting(segment.Start);
        var expression = ParseExpression();
        var trailing = Current;
        if (trailing.Kind != TokenKind.EndOfFile)
        {
            throw Unexpected(trailing, "`}`");
        }

        LeaveNesting();
        PopContext();
        tokens = savedTokens;
        position = savedPosition;
        lexer = savedLexer;
        previousEnd = savedPreviousEnd;
        closeIndexByOpen = savedCloseMap;
        return expression;
    }

    /// <summary>§1.6: a single literal token, optionally preceded by `-`, is almost certainly a mistyped `format` template.</summary>
    private static bool IsBareLiteral(IReadOnlyList<Token> interpolationTokens)
    {
        var literalIndex = interpolationTokens.Count == 2 && interpolationTokens[0].Kind == TokenKind.Minus ? 1 : 0;
        if (interpolationTokens.Count != literalIndex + 1)
        {
            return false;
        }

        var token = interpolationTokens[literalIndex];
        return token.Kind is TokenKind.Integer or TokenKind.Float or TokenKind.True or TokenKind.False
            || token is StringToken { IsFaulty: false, HasInterpolation: false };
    }
}
