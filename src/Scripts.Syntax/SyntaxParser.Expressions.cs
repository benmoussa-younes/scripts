using Scripts.Core;

namespace Scripts.Syntax;

internal sealed partial class SyntaxParser
{
    private const int OrPrecedence = 1;
    private const int AndPrecedence = 2;
    private const int NotPrecedence = 3;
    private const int ComparisonPrecedence = 4;
    private const int AdditivePrecedence = 5;
    private const int MultiplicativePrecedence = 6;

    /// <param name="isMapKey">A map key may be followed by `:`, so `name: type -> body` is not diagnosed there.</param>
    private Expression ParseExpression(bool isMapKey = false)
    {
        var start = Current.Start;
        EnterNesting(start);
        Expression expression;
        if (IsLambdaStart(isMapKey))
        {
            expression = ParseLambda();
        }
        else
        {
            expression = ParseBinary(OrPrecedence, forbidAnd: false);
        }

        LeaveNesting();
        return expression;
    }

    private bool IsLambdaStart(bool isMapKey)
    {
        var token = Current;
        if (token.Kind == TokenKind.LeftParenthesis)
        {
            return IsParenthesizedLambda();
        }

        if (token.Kind != TokenKind.Identifier)
        {
            return false;
        }

        if (token.Text == "_")
        {
            var wildcardNext = Fetch(position + 1);
            if (wildcardNext.Kind == TokenKind.NewLine && IgnoresNewLines)
            {
                wildcardNext = Fetch(position + 2);
            }

            if (wildcardNext.Kind == TokenKind.Arrow)
            {
                throw ErrorAt(ErrorCodes.Parse0056, wildcardNext);
            }
        }

        var next = Fetch(position + 1);
        if (next.Kind == TokenKind.NewLine && IgnoresNewLines)
        {
            next = Fetch(position + 2);
        }

        if (next.Kind == TokenKind.Colon && !isMapKey)
        {
            RejectAnnotatedBareLambdaParameter(token, next);
        }

        return next.Kind == TokenKind.FatArrow;
    }

    /// <summary>
    /// `(` … matching `)` followed by `=>` (§2.3). The matching pairs are computed once per parse
    /// (O(tokens)); rescanning from every `(` made the lookahead quadratic and let a 1.2 MB file of
    /// parentheses stall the parser for two minutes (the 2026-10-27 audit).
    /// </summary>
    private bool IsParenthesizedLambda()
    {
        EnsureDelimiterMap();
        if (closeIndexByOpen is null || !closeIndexByOpen.TryGetValue(position, out var close))
        {
            return false;
        }

        var nextAfterHead = Fetch(close + 1);
        if (nextAfterHead.Kind == TokenKind.NewLine && IgnoresNewLines)
        {
            nextAfterHead = Fetch(close + 2);
        }

        // The migration check looks for the old `->`; IsArrowAt answers for the lambda arrow (`=>`).
        if (nextAfterHead.Kind == TokenKind.Arrow)
        {
            throw ErrorAt(ErrorCodes.Parse0056, nextAfterHead);
        }

        return nextAfterHead.Kind == TokenKind.FatArrow;
    }

    private Dictionary<int, int>? closeIndexByOpen;

    /// <summary>One stack pass over the token stream: every matched delimiter pair, by opening index.</summary>
    private void EnsureDelimiterMap()
    {
        if (closeIndexByOpen is not null)
        {
            return;
        }

        closeIndexByOpen = [];
        var stack = new Stack<(TokenKind Kind, int Index)>();
        for (var index = 0; ; index++)
        {
            var token = Fetch(index);
            if (token.Kind == TokenKind.EndOfFile || token.IsFaulty)
            {
                return;
            }

            switch (token.Kind)
            {
                case TokenKind.LeftParenthesis or TokenKind.LeftBracket or TokenKind.LeftBrace:
                    stack.Push((token.Kind, index));
                    break;
                case TokenKind.RightParenthesis or TokenKind.RightBracket or TokenKind.RightBrace:
                    if (stack.Count == 0)
                    {
                        break;
                    }

                    var opening = stack.Pop();
                    var matches = opening.Kind switch
                    {
                        TokenKind.LeftParenthesis => token.Kind == TokenKind.RightParenthesis,
                        TokenKind.LeftBracket => token.Kind == TokenKind.RightBracket,
                        _ => token.Kind == TokenKind.RightBrace,
                    };
                    if (matches)
                    {
                        closeIndexByOpen[opening.Index] = index;
                    }

                    break;
            }
        }
    }

    private bool IsArrowAt(int index)
    {
        var token = Fetch(index);
        if (token.Kind == TokenKind.NewLine && IgnoresNewLines)
        {
            token = Fetch(index + 1);
        }

        return token.Kind == TokenKind.FatArrow;
    }

    /// <summary>`x: T -> e` is diagnosed once the type and the arrow have been seen; anything else is left to the caller's own error.</summary>
    private void RejectAnnotatedBareLambdaParameter(Token name, Token colon)
    {
        var savedPosition = position;
        var savedDepth = nestingDepth;
        var savedPreviousEnd = previousEnd;
        var savedContextCount = contexts.Count;
        var isAnnotatedLambda = false;
        try
        {
            position += 2;
            ParseType();
            isAnnotatedLambda = Current.Kind == TokenKind.FatArrow;
        }
        catch (SyntaxErrorException)
        {
            isAnnotatedLambda = false;
        }

        var annotationText = text.Substring(colon.End, Math.Max(0, previousEnd - colon.End)).Trim();
        position = savedPosition;
        nestingDepth = savedDepth;
        previousEnd = savedPreviousEnd;
        contexts.RemoveRange(savedContextCount, contexts.Count - savedContextCount);
        if (isAnnotatedLambda)
        {
            throw ErrorAt(ErrorCodes.Parse0044, colon, ("name", name.Text), ("type", annotationText));
        }
    }

    private LambdaExpression ParseLambda()
    {
        var start = Current.Start;
        var parameters = new List<LambdaParameter>();
        if (Current.Kind == TokenKind.Identifier)
        {
            parameters.Add(ParseLambdaParameter(allowAnnotation: false));
        }
        else
        {
            var open = Advance();
            PushContext(ContextKind.Parenthesis, open);
            if (Current.Kind != TokenKind.RightParenthesis)
            {
                parameters.Add(ParseLambdaParameter(allowAnnotation: true));
                while (Current.Kind == TokenKind.Comma)
                {
                    Advance();
                    parameters.Add(ParseLambdaParameter(allowAnnotation: true));
                }
            }

            Expect(TokenKind.RightParenthesis, "`)`");
            PopContext();
        }

        Expect(TokenKind.FatArrow, "`=>`");
        var body = ParseExpression();
        return new LambdaExpression(SpanFrom(start), parameters, body);
    }

    private LambdaParameter ParseLambdaParameter(bool allowAnnotation)
    {
        var (name, nameSpan) = ExpectName(allowWildcard: true);
        TypeSyntax? annotation = null;
        if (allowAnnotation && Current.Kind == TokenKind.Colon)
        {
            Advance();
            annotation = ParseType();
        }

        return new LambdaParameter(SpanFrom(nameSpan.Start), name, annotation);
    }

    private static int GetPrecedence(TokenKind kind) =>
        kind switch
        {
            TokenKind.Or => OrPrecedence,
            TokenKind.And => AndPrecedence,
            TokenKind.EqualEqual or TokenKind.NotEqual or TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual => ComparisonPrecedence,
            TokenKind.Plus or TokenKind.Minus => AdditivePrecedence,
            TokenKind.Star or TokenKind.Slash or TokenKind.Percent => MultiplicativePrecedence,
            _ => 0,
        };

    private static BinaryOperator ToBinaryOperator(TokenKind kind) =>
        kind switch
        {
            TokenKind.Or => BinaryOperator.Or,
            TokenKind.And => BinaryOperator.And,
            TokenKind.EqualEqual => BinaryOperator.Equal,
            TokenKind.NotEqual => BinaryOperator.NotEqual,
            TokenKind.Less => BinaryOperator.Less,
            TokenKind.LessEqual => BinaryOperator.LessOrEqual,
            TokenKind.Greater => BinaryOperator.Greater,
            TokenKind.GreaterEqual => BinaryOperator.GreaterOrEqual,
            TokenKind.Plus => BinaryOperator.Add,
            TokenKind.Minus => BinaryOperator.Subtract,
            TokenKind.Star => BinaryOperator.Multiply,
            TokenKind.Slash => BinaryOperator.Divide,
            _ => BinaryOperator.Modulo,
        };

    /// <summary>Precedence climbing over or / and / not / comparison / additive / multiplicative with the §2.1 rejections.</summary>
    /// <param name="forbidAnd">True for the right operand of `or`: an unparenthesized `and` there mixes the two operators.</param>
    private Expression ParseBinary(int minimumPrecedence, bool forbidAnd)
    {
        var start = Current.Start;
        var left = Current.Kind switch
        {
            TokenKind.Not when minimumPrecedence <= NotPrecedence => ParseNot(),
            TokenKind.Minus => ParseUnary(),
            _ => ParsePostfix(),
        };
        var containsAnd = false;
        var justCompared = false;
        while (true)
        {
            var token = Current;
            var precedence = GetPrecedence(token.Kind);
            if (precedence == 0 || precedence < minimumPrecedence)
            {
                return left;
            }

            RejectAmbiguousOperator(token, precedence, start, forbidAnd, containsAnd, justCompared);
            containsAnd |= precedence == AndPrecedence;
            Advance();
            var right = ParseBinary(precedence + 1, forbidAnd: precedence == OrPrecedence);
            left = new BinaryExpression(SpanFrom(start), ToBinaryOperator(token.Kind), left, right, SpanOf(token));
            justCompared = precedence == ComparisonPrecedence;
        }
    }

    private void RejectAmbiguousOperator(Token token, int precedence, int start, bool forbidAnd, bool containsAnd, bool justCompared)
    {
        if ((precedence == AndPrecedence && forbidAnd) || (precedence == OrPrecedence && containsAnd))
        {
            throw ErrorAt(ErrorCodes.Parse0035, token);
        }

        if (precedence == ComparisonPrecedence && justCompared)
        {
            throw ErrorAt(ErrorCodes.Parse0034, token, ("expression", text.Substring(start, token.End - start) + " …"));
        }

        if (token.Kind == TokenKind.Minus && token.IsPrecededByBlank && !IsFollowedByBlank(token))
        {
            throw ErrorAt(
                ErrorCodes.Parse0009,
                token,
                ("left", text.Substring(start, previousEnd - start)),
                ("right", Describe(Fetch(position + 1))));
        }
    }

    private bool IsFollowedByBlank(Token token) =>
        token.End >= text.Length || text[token.End] is ' ' or '\t' or '\n' or '\r';

    private Expression ParseNot()
    {
        var notToken = Advance();
        EnterNesting(notToken.Start);
        var operand = ParseBinary(NotPrecedence, forbidAnd: false);
        LeaveNesting();
        return new UnaryExpression(SpanFrom(notToken.Start), UnaryOperator.Not, operand);
    }

    private Expression ParseUnary()
    {
        var token = Advance();
        EnterNesting(token.Start);
        var operand = Current.Kind == TokenKind.Minus ? ParseUnary() : ParsePostfix();
        LeaveNesting();
        return new UnaryExpression(SpanFrom(token.Start), UnaryOperator.Negate, operand);
    }

    private Expression ParsePostfix()
    {
        var start = Current.Start;
        var expression = ParsePrimary();
        while (true)
        {
            switch (Current.Kind)
            {
                case TokenKind.LeftParenthesis:
                    expression = ParseCall(expression, start);
                    break;
                case TokenKind.LeftBracket:
                    expression = ParseIndex(expression, start);
                    break;
                case TokenKind.Dot:
                    expression = ParseMemberAccess(expression, start);
                    break;
                case TokenKind.Question:
                    Advance();
                    expression = new PropagateExpression(SpanFrom(start), expression);
                    break;
                default:
                    return expression;
            }
        }
    }

    private CallExpression ParseCall(Expression callee, int start)
    {
        var open = Advance();
        PushContext(ContextKind.Parenthesis, open);
        var arguments = ParseArguments();
        Expect(TokenKind.RightParenthesis, "`)`");
        PopContext();
        return new CallExpression(SpanFrom(start), callee, arguments);
    }

    /// <summary>
    /// Parses `<c>[ namedArguments | positionalArguments ]</c> (DESIGN 2). A call is fully named or fully
    /// positional; the first argument decides the form and the first argument of the other form is
    /// <c>SCRIPTS-PARSE-0058</c>. `NAME = expr` is unambiguous here: scripts has no assignment expression.
    /// </summary>
    private List<CallArgument> ParseArguments()
    {
        var arguments = new List<CallArgument>();
        if (Current.Kind == TokenKind.RightParenthesis)
        {
            return arguments;
        }

        if (StartsNamedArgument())
        {
            while (true)
            {
                var name = Expect(TokenKind.Identifier, "an argument name");
                if (name.Text == "_")
                {
                    // `_` is a wildcard, not a name (§1.4): it cannot label an argument (audit F10).
                    throw ErrorAt(ErrorCodes.Parse0023, name, ("found", "`_`"), ("expected", "an argument name"));
                }

                Expect(TokenKind.Equal, "`=`");
                var expression = ParseExpression();
                arguments.Add(new CallArgument(new Span(name.Start, expression.Span.End), expression, name.Text, SpanOf(name)));
                if (Current.Kind != TokenKind.Comma)
                {
                    break;
                }

                Advance();
                if (Current.Kind == TokenKind.RightParenthesis)
                {
                    break;
                }

                // A missing argument (stray comma, immediate `)`) is the usual parse error, not a mixed-form call.
                if (Current.Kind is TokenKind.Comma or TokenKind.RightParenthesis or TokenKind.EndOfFile)
                {
                    throw ErrorAt(ErrorCodes.Parse0023, Current, ("found", Describe(Current)), ("expected", "an expression"));
                }

                if (!StartsNamedArgument())
                {
                    throw ErrorAt(ErrorCodes.Parse0058, Current);
                }
            }

            return arguments;
        }

        while (true)
        {
            var expression = ParseExpression();
            arguments.Add(new CallArgument(expression.Span, expression, null, null));
            if (Current.Kind != TokenKind.Comma)
            {
                break;
            }

            Advance();
            if (Current.Kind == TokenKind.RightParenthesis)
            {
                break;
            }

            if (StartsNamedArgument())
            {
                throw ErrorAt(ErrorCodes.Parse0058, Current);
            }
        }

        return arguments;
    }

    /// <summary>True when the current argument starts the named form: an identifier immediately followed by `=`.</summary>
    private bool StartsNamedArgument()
    {
        if (Current.Kind != TokenKind.Identifier)
        {
            return false;
        }

        var index = position + 1;
        while (Fetch(index).Kind == TokenKind.NewLine)
        {
            index++;
        }

        return Fetch(index).Kind == TokenKind.Equal;
    }

    private IndexExpression ParseIndex(Expression target, int start)
    {
        var open = Advance();
        PushContext(ContextKind.Bracket, open);
        var index = ParseExpression();
        Expect(TokenKind.RightBracket, "`]`");
        PopContext();
        return new IndexExpression(SpanFrom(start), target, index);
    }

    private MemberAccessExpression ParseMemberAccess(Expression target, int start)
    {
        Advance();
        var (name, nameSpan) = ExpectMemberName();
        return new MemberAccessExpression(SpanFrom(start), target, name, nameSpan);
    }

    private Expression ParsePrimary()
    {
        var token = Current;
        if (token.Kind == TokenKind.LeftParenthesis)
        {
            // Parentheses only group and have no node of their own (DESIGN §3.2, S12): the span stays the inner
            // one and the depth of the tree is the inner one. The grouping still costs one level of parser
            // recursion, which the stack guard of EnterNesting covers.
            Advance();
            EnterNesting(token.Start);
            try
            {
                PushContext(ContextKind.Parenthesis, token);
                var inner = ParseExpression();
                Expect(TokenKind.RightParenthesis, "`)`");
                PopContext();
                return inner;
            }
            finally
            {
                LeaveNesting();
            }
        }

        switch (token.Kind)
        {
            case TokenKind.Integer:
                Advance();
                return new IntegerLiteral(SpanOf(token), ((IntegerToken)token).Value);
            case TokenKind.Float:
                Advance();
                return new FloatLiteral(SpanOf(token), ((FloatToken)token).Value);
            case TokenKind.True:
            case TokenKind.False:
                Advance();
                return new BooleanLiteral(SpanOf(token), token.Kind == TokenKind.True);
            case TokenKind.String:
                return ParseStringExpression();
            case TokenKind.Identifier:
                {
                    var identifier = IsResultConstructor(token) ? ParseResultConstructor(token) : ParseIdentifierExpression(token);
                    if (Current.Kind == TokenKind.Arrow)
                    {
                        throw ErrorAt(ErrorCodes.Parse0056, Current);
                    }

                    return identifier;
                }

            case TokenKind.If:
                throw ErrorAt(ErrorCodes.Parse0057, token);
            case TokenKind.LeftParenthesis:
                return ParseParenthesized();
            case TokenKind.LeftBracket:
                return ParseListLiteral();
            case TokenKind.LeftBrace:
                return ParseMapLiteral();
            case TokenKind.Match:
                return ParseMatch(isStatement: false);
            default:
                throw Unexpected(token, ExpectedExpression);
        }
    }

    private bool IsResultConstructor(Token identifier) =>
        identifier.Text is "ok" or "error" && Fetch(position + 1).Kind == TokenKind.LeftParenthesis;

    private Expression ParseIdentifierExpression(Token token)
    {
        if (token.Text == "_")
        {
            throw ErrorAt(ErrorCodes.Parse0027, token);
        }

        RejectFutureReserved(token);
        Advance();
        return new IdentifierExpression(SpanOf(token), token.Text);
    }

    private Expression ParseResultConstructor(Token name)
    {
        Advance();
        var open = Advance();
        PushContext(ContextKind.Parenthesis, open);
        var isOk = name.Text == "ok";
        if (isOk)
        {
            var okValue = Current.Kind == TokenKind.RightParenthesis ? null : ParseExpression();
            Expect(TokenKind.RightParenthesis, "`)`");
            PopContext();
            return new OkExpression(SpanFrom(name.Start), okValue);
        }

        var errorValue = ParseExpression();
        if (Current.Kind == TokenKind.Comma)
        {
            return ParseErrorConstructor(name, errorValue);
        }

        Expect(TokenKind.RightParenthesis, "`)`");
        PopContext();
        return new ErrorExpression(SpanFrom(name.Start), errorValue);
    }

    /// <summary>error(kind, message, subject) — the value-namespace overload that builds an `error` (DESIGN 3.5.1).</summary>
    private ErrorConstructorExpression ParseErrorConstructor(Token name, Expression kind)
    {
        Advance();
        var message = ParseExpression();
        Expect(TokenKind.Comma, "`,`");
        var subject = ParseExpression();
        Expect(TokenKind.RightParenthesis, "`)`");
        PopContext();
        return new ErrorConstructorExpression(SpanFrom(name.Start), kind, subject, message);
    }

    private Expression ParseParenthesized()
    {
        var open = Advance();
        PushContext(ContextKind.Parenthesis, open);
        var inner = ParseExpression();
        Expect(TokenKind.RightParenthesis, "`)`");
        PopContext();
        return inner;
    }

    private ListLiteral ParseListLiteral()
    {
        var open = Advance();
        PushContext(ContextKind.Bracket, open);
        var elements = new List<Expression>();
        while (Current.Kind != TokenKind.RightBracket)
        {
            elements.Add(ParseExpression());
            if (Current.Kind != TokenKind.Comma)
            {
                break;
            }

            Advance();
        }

        Expect(TokenKind.RightBracket, "`]`");
        PopContext();
        return new ListLiteral(SpanFrom(open.Start), elements);
    }

    private MapLiteral ParseMapLiteral()
    {
        var open = Advance();
        PushContext(ContextKind.Map, open);
        var entries = new List<MapEntry>();
        while (Current.Kind != TokenKind.RightBrace)
        {
            entries.Add(ParseMapEntry());
            if (Current.Kind != TokenKind.Comma)
            {
                break;
            }

            Advance();
        }

        Expect(TokenKind.RightBrace, "`}`");
        PopContext();
        return new MapLiteral(SpanFrom(open.Start), entries);
    }

    private MapEntry ParseMapEntry()
    {
        var start = Current.Start;
        EnterNesting(start);
        var key = ParseExpression(isMapKey: true);
        Expect(TokenKind.Colon, "`:`");
        var value = ParseExpression();
        LeaveNesting();
        return new MapEntry(SpanFrom(start), key, value);
    }
}
