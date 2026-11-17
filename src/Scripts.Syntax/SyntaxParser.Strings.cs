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
                InterpolationSegment interpolation => new InterpolationPart(new Span(interpolation.Start, interpolation.End), ParseInterpolation(interpolation)),
                _ => throw new InvalidOperationException("Unknown string segment."),
            });
        }

        if (token.Failure is { } failure)
        {
            throw new SyntaxErrorException(failure);
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

    /// <summary>Parses the tokens collected by the lexer for one `{…}`; the surrounding token stream is restored afterwards.</summary>
    private Expression ParseInterpolation(InterpolationSegment segment)
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
