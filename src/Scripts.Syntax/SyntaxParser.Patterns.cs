using Scripts.Core;

namespace Scripts.Syntax;

internal sealed partial class SyntaxParser
{
    /// <param name="isStatement">True for a statement-position match; in both positions `=&gt; {` opens a block arm.</param>
    private MatchExpression ParseMatch(bool isStatement)
    {
        var matchToken = Advance();
        var scrutinee = ParseExpression();
        var open = RequireOpeningBrace("match");
        Advance();
        PushContext(ContextKind.Match, open);
        SkipNewLines();
        var arms = new List<MatchArm>();
        while (Current.Kind != TokenKind.RightBrace)
        {
            arms.Add(ParseMatchArm(isStatement));
            SkipNewLines();
        }

        var close = Current;
        if (arms.Count == 0)
        {
            throw ErrorAt(ErrorCodes.Parse0047, close);
        }

        Advance();
        PopContext();
        return new MatchExpression(SpanFrom(matchToken.Start), scrutinee, arms, isStatement);
    }

    private MatchArm ParseMatchArm(bool isStatementMatch)
    {
        var start = Current.Start;
        EnterNesting(start);
        var pattern = ParsePattern();
        Expect(TokenKind.FatArrow, "`=>`");
        var rawNext = Fetch(position);
        if (rawNext.Kind == TokenKind.NewLine)
        {
            throw ErrorAt(ErrorCodes.Parse0054, rawNext);
        }

        Expression? bodyExpression = null;
        BlockStatement? bodyBlock = null;
        if (Current.Kind == TokenKind.LeftBrace)
        {
            RejectMapLiteralArm();
            // Inside the block arm of a `match` expression, a nested `match` may turn out to be its value line (see ParseMatchStatement).
            bodyBlock = ParseBlock("=>", isValueBlock: !isStatementMatch || inValueBlock);
        }
        else
        {
            bodyExpression = ParseExpression();
        }

        var span = SpanFrom(start);
        FinishArm();
        LeaveNesting();
        return new MatchArm(span, pattern, bodyExpression, bodyBlock);
    }

    /// <summary>After `=>`, `{` opens a block (L1): `{name: …` or `{"key": …` is a map literal, which must be parenthesized.</summary>
    private void RejectMapLiteralArm()
    {
        var index = position + 1;
        while (Fetch(index).Kind == TokenKind.NewLine)
        {
            index++;
        }

        if (Fetch(index).Kind is TokenKind.Identifier or TokenKind.String && Fetch(index + 1).Kind == TokenKind.Colon)
        {
            throw Unexpected(Fetch(index + 1), "a statement: after `=>`, `{` opens a block — write a map literal as `({…})`");
        }
    }

    private void FinishArm()
    {
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.NewLine:
            case TokenKind.Comma:
                Advance();
                return;
            case TokenKind.RightBrace:
                return;
            default:
                throw Unexpected(token, "a newline, `,` or `}`");
        }
    }

    private Pattern ParsePattern()
    {
        var first = ParseSinglePattern();
        if (Current.Kind != TokenKind.Pipe)
        {
            return first;
        }

        var alternatives = new List<Pattern> { first };
        while (Current.Kind == TokenKind.Pipe)
        {
            var pipe = Advance();
            if (!IsAlternativeCandidate(alternatives[^1]))
            {
                throw ErrorAt(ErrorCodes.Parse0046, pipe);
            }

            var next = ParseSinglePattern();
            if (!IsAlternativeCandidate(next))
            {
                throw ErrorAt(ErrorCodes.Parse0046, pipe);
            }

            alternatives.Add(next);
        }

        return new AlternativePattern(SpanFrom(first.Span.Start), alternatives);
    }

    private static bool IsAlternativeCandidate(Pattern pattern) => pattern is LiteralPattern or WildcardPattern;

    private Pattern ParseSinglePattern()
    {
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.Identifier:
                return ParseNamePattern(token);
            case TokenKind.Integer:
                Advance();
                return new LiteralPattern(SpanOf(token), new IntegerLiteral(SpanOf(token), ((IntegerToken)token).Value));
            case TokenKind.Minus:
                return ParseNegativeIntegerPattern(token);
            case TokenKind.True:
            case TokenKind.False:
                Advance();
                return new LiteralPattern(SpanOf(token), new BooleanLiteral(SpanOf(token), token.Kind == TokenKind.True));
            case TokenKind.String:
                return ParseStringPattern((StringToken)token);
            case TokenKind.Float:
                throw ErrorAt(ErrorCodes.Parse0045, token, ("detail", "a floating literal is not allowed"));
            default:
                throw TokenKinds.IsKeyword(token.Kind)
                    ? ErrorAt(ErrorCodes.Parse0025, token, ("keyword", token.Text))
                    : Unexpected(token, "a pattern");
        }
    }

    private Pattern ParseNamePattern(Token token)
    {
        if (token.Text == "_")
        {
            Advance();
            return new WildcardPattern(SpanOf(token));
        }

        if (token.Text is "ok" or "error" && Fetch(position + 1).Kind == TokenKind.LeftParenthesis)
        {
            return ParseResultPattern(token);
        }

        RejectFutureReserved(token);
        Advance();
        return new BindingPattern(SpanOf(token), token.Text);
    }

    private ResultPattern ParseResultPattern(Token name)
    {
        Advance();
        var open = Advance();
        PushContext(ContextKind.Parenthesis, open);
        var inner = ParseResultPatternContent();
        Expect(TokenKind.RightParenthesis, "`)`");
        PopContext();
        return new ResultPattern(SpanFrom(name.Start), name.Text == "ok", inner);
    }

    private Pattern ParseResultPatternContent()
    {
        var token = Current;
        if (token.Kind == TokenKind.Identifier)
        {
            Advance();
            if (token.Text == "_")
            {
                return new WildcardPattern(SpanOf(token));
            }

            RejectFutureReserved(token);
            return new BindingPattern(SpanOf(token), token.Text);
        }

        if (token.Kind is TokenKind.Integer or TokenKind.Float or TokenKind.String or TokenKind.True or TokenKind.False or TokenKind.Minus)
        {
            throw ErrorAt(ErrorCodes.Parse0045, token, ("detail", "`ok(…)` and `error(…)` accept only a name or `_`"));
        }

        throw TokenKinds.IsKeyword(token.Kind)
            ? ErrorAt(ErrorCodes.Parse0025, token, ("keyword", token.Text))
            : Unexpected(token, "a name or `_`");
    }

    private LiteralPattern ParseNegativeIntegerPattern(Token minus)
    {
        Advance();
        var number = Current;
        if (number is IntegerToken integer)
        {
            Advance();
            var span = SpanFrom(minus.Start);
            return new LiteralPattern(span, new IntegerLiteral(span, -integer.Value));
        }

        throw number.Kind == TokenKind.Float
            ? ErrorAt(ErrorCodes.Parse0045, minus, ("detail", "a floating literal is not allowed"))
            : Unexpected(number, "an integer");
    }

    private LiteralPattern ParseStringPattern(StringToken token)
    {
        if (token.Style != StringStyle.Normal || token.HasInterpolation)
        {
            var detail = token.Style switch
            {
                StringStyle.Raw => "a raw string is not allowed",
                StringStyle.Path => "a `p\"…\"` path is not allowed",
                _ => "an interpolated string is not allowed",
            };
            throw ErrorAt(ErrorCodes.Parse0045, token, ("detail", detail));
        }

        Advance();
        if (token.Failure is { } failure)
        {
            throw new SyntaxErrorException(failure);
        }

        var value = string.Concat(token.Segments.OfType<TextSegment>().Select(segment => segment.Text));
        return new LiteralPattern(SpanOf(token), new StringLiteral(SpanOf(token), value));
    }
}
