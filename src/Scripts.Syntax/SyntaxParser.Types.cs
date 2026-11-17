using Scripts.Core;

namespace Scripts.Syntax;

internal sealed partial class SyntaxParser
{
    private TypeSyntax ParseType()
    {
        var token = Current;
        EnterNesting(token.Start);
        TypeSyntax type = token.Kind == TokenKind.Function ? ParseFunctionType() : ParseNamedType();
        LeaveNesting();
        return type;
    }

    private NamedTypeSyntax ParseNamedType()
    {
        var nameToken = Current;
        if (nameToken.Kind != TokenKind.Identifier || nameToken.Text == "_")
        {
            throw Unexpected(nameToken, "un type");
        }

        RejectFutureReserved(nameToken);
        Advance();
        var typeArguments = new List<TypeSyntax>();
        if (Current.Kind == TokenKind.Less)
        {
            Advance();
            typeArguments.Add(ParseType());
            while (Current.Kind == TokenKind.Comma)
            {
                Advance();
                typeArguments.Add(ParseType());
            }

            ConsumeClosingAngleBracket();
        }

        return new NamedTypeSyntax(SpanFrom(nameToken.Start), nameToken.Text, typeArguments);
    }

    private FunctionTypeSyntax ParseFunctionType()
    {
        var start = Advance().Start;
        var open = Expect(TokenKind.LeftParenthesis, "`(`");
        PushContext(ContextKind.Parenthesis, open);
        var parameterTypes = new List<TypeSyntax>();
        if (Current.Kind != TokenKind.RightParenthesis)
        {
            parameterTypes.Add(ParseType());
            while (Current.Kind == TokenKind.Comma)
            {
                Advance();
                parameterTypes.Add(ParseType());
            }
        }

        Expect(TokenKind.RightParenthesis, "`)`");
        PopContext();
        var arrow = Current;
        if (arrow.Kind != TokenKind.Arrow)
        {
            throw ErrorAt(ErrorCodes.Parse0043, arrow.Start, Math.Max(arrow.End, arrow.Start));
        }

        Advance();
        var returnType = ParseType();
        return new FunctionTypeSyntax(SpanFrom(start), parameterTypes, returnType);
    }

    /// <summary>`&gt;=` closes the type arguments and leaves `=` for the caller (`list&lt;integer&gt;= []`).</summary>
    private void ConsumeClosingAngleBracket()
    {
        var token = Current;
        if (token.Kind == TokenKind.GreaterEqual)
        {
            tokens[position] = new Token(TokenKind.Greater, token.Start, token.Start + 1) { ClosesTypeArguments = true, IsPrecededByBlank = token.IsPrecededByBlank };
            tokens.Insert(position + 1, new Token(TokenKind.Equal, token.Start + 1, token.End));
            Advance();
            return;
        }

        if (token.Kind != TokenKind.Greater)
        {
            throw Unexpected(token, "`>`");
        }

        token.ClosesTypeArguments = true;
        Advance();
    }
}
