using Scripts.Core;

namespace Scripts.Syntax;

internal sealed partial class SyntaxParser
{
    private List<ImportDeclaration> ParseImportHeader()
    {
        var imports = new List<ImportDeclaration>();
        SkipNewLines();
        while (Current.Kind == TokenKind.Import)
        {
            imports.Add(ParseImport());
            FinishStatement();
            SkipNewLines();
        }

        return imports;
    }

    private ImportDeclaration ParseImport()
    {
        var importToken = Advance();
        var pathToken = Current;
        if (pathToken.Kind == TokenKind.PipeGreater)
        {
            // Removed operator, rejected before any other rule (DESIGN §1.7).
            throw ErrorAt(ErrorCodes.Parse0055, pathToken);
        }

        if (pathToken is not StringToken { Style: StringStyle.Normal, HasInterpolation: false } pathString)
        {
            throw ErrorAt(ErrorCodes.Parse0052, pathToken);
        }

        Advance();
        if (pathString.Failure is { } failure)
        {
            throw new SyntaxErrorException(failure);
        }

        var path = string.Concat(pathString.Segments.OfType<TextSegment>().Select(segment => segment.Text));
        if (Current.Kind != TokenKind.As)
        {
            throw ErrorAt(
                ErrorCodes.Module0007,
                pathToken,
                ("path", path),
                ("reason", "every import requires an explicit `as <name>` alias"));
        }

        Advance();
        var (alias, aliasSpan) = ExpectName(allowWildcard: false);
        return new ImportDeclaration(SpanFrom(importToken.Start), path, SpanOf(pathString), alias, aliasSpan);
    }

    private List<Statement> ParseStatementList(TokenKind terminator, bool isTopLevel)
    {
        var statements = new List<Statement>();
        while (true)
        {
            SkipNewLines();
            var token = Current;
            if (token.Kind == terminator)
            {
                return statements;
            }

            if (token.Kind == TokenKind.Semicolon)
            {
                // Scripts separates statements with a newline: a `;` is always an error.
                throw ErrorAt(ErrorCodes.Parse0033, token);
            }

            // The AST depth limit (DESIGN §1.1, SCRIPTS-PARSE-0005) counts every node of every
            // statement, in the main script and inside blocks alike: a nested block must not hide a deep
            // statement. The check is iterative (SyntaxDepthChecker), so it costs no stack.
            var statement = ParseStatement(isTopLevel);
            RejectExcessiveDepth(statement);

            statements.Add(statement);
        }
    }

    private Statement ParseStatement(bool isTopLevel)
    {
        EnterNesting(Current.Start);
        var statement = ParseStatementCore(isTopLevel);
        LeaveNesting();
        FinishStatement();
        return statement;
    }

    private Statement ParseStatementCore(bool isTopLevel)
    {
        var first = Current;
        var visibility = ParseVisibility(first, isTopLevel);
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.Let:
                return ParseLet(first.Start, visibility);
            case TokenKind.Function:
                return ParseFunction(first.Start, visibility);
            case TokenKind.If:
                return ParseIf();
            case TokenKind.While:
                return ParseWhile();
            case TokenKind.For:
                return ParseFor();
            case TokenKind.Return:
                return ParseReturn();
            case TokenKind.Break:
                Advance();
                return new BreakStatement(SpanOf(token));
            case TokenKind.Continue:
                Advance();
                return new ContinueStatement(SpanOf(token));
            case TokenKind.Match:
                return ParseMatchStatement();
            case TokenKind.Import:
                throw ErrorAt(ErrorCodes.Parse0051, token);
            default:
                return ParseExpressionOrAssignment();
        }
    }

    /// <summary>Consumes `public`/`private` when allowed (top level of a module, before let/function) and enforces its presence there.</summary>
    private Visibility ParseVisibility(Token first, bool isTopLevel)
    {
        if (first.Kind == TokenKind.Private)
        {
            // `private` is implicit: it is never written, even where the old keyword was accepted.
            throw ErrorAt(ErrorCodes.Parse0049, first, ("visibility", first.Text));
        }

        if (first.Kind == TokenKind.Public)
        {
            if (!isModule || !isTopLevel)
            {
                throw ErrorAt(ErrorCodes.Parse0049, first, ("visibility", first.Text));
            }

            Advance();
            if (Current.Kind is not TokenKind.Let and not TokenKind.Function)
            {
                throw ErrorAt(ErrorCodes.Parse0049, first, ("visibility", first.Text));
            }

            return Visibility.Public;
        }

        // A module declaration without `public` is private; a script declaration has no visibility.
        return isModule && isTopLevel && first.Kind is TokenKind.Let or TokenKind.Function
            ? Visibility.Private
            : Visibility.None;
    }

    private void FinishStatement()
    {
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.NewLine:
                Advance();
                return;
            case TokenKind.Semicolon:
                // A `;` is never a statement separator; the diagnostic names the newline rule.
                throw ErrorAt(ErrorCodes.Parse0032, token);
            case TokenKind.RightBrace:
            case TokenKind.EndOfFile:
                return;
            default:
                throw Unexpected(token, ExpectedStatementEnd);
        }
    }

    private LetStatement ParseLet(int start, Visibility visibility)
    {
        Advance();
        Span? mutableSpan = null;
        if (Current.Kind == TokenKind.Mutable)
        {
            mutableSpan = SpanOf(Advance());
        }

        var (name, nameSpan) = ExpectName(allowWildcard: true);
        TypeSyntax? annotation = null;
        var annotationText = string.Empty;
        if (Current.Kind == TokenKind.Colon)
        {
            var colon = Advance();
            annotation = ParseType();
            annotationText = text.Substring(colon.Start, previousEnd - colon.Start);
        }

        var equals = Current;
        if (equals.Kind != TokenKind.Equal)
        {
            throw MissingInitializer(equals, name, annotationText);
        }

        Advance();
        var initializer = ParseExpression();
        return new LetStatement(SpanFrom(start), visibility, mutableSpan.HasValue, name, nameSpan, annotation, initializer, mutableSpan);
    }

    private SyntaxErrorException MissingInitializer(Token found, string name, string annotationText)
    {
        var endsStatement = found.Kind is TokenKind.NewLine or TokenKind.Semicolon or TokenKind.RightBrace
            || (found.Kind == TokenKind.EndOfFile && !(contexts.Count > 0 && contexts[^1].Opening is not null));
        return endsStatement
            // S6: at end of file the diagnostic covers one code point (`end_column = column + 1`).
            ? ErrorAt(
                ErrorCodes.Parse0041,
                found.Start,
                found.Kind == TokenKind.EndOfFile ? found.End + 1 : Math.Max(found.End, found.Start),
                ("name", name),
                ("annotation", annotationText))
            : Unexpected(found, "`=`");
    }

    private FunctionDeclaration ParseFunction(int start, Visibility visibility)
    {
        Advance();
        var (name, nameSpan) = ExpectName(allowWildcard: false);
        var open = Expect(TokenKind.LeftParenthesis, "`(`");
        PushContext(ContextKind.Parenthesis, open);
        var parameters = new List<Parameter>();
        while (Current.Kind != TokenKind.RightParenthesis)
        {
            parameters.Add(ParseParameter());
            if (Current.Kind != TokenKind.Comma)
            {
                break;
            }

            Advance();
        }

        Expect(TokenKind.RightParenthesis, "`)`");
        PopContext();
        TypeSyntax? returnType = null;
        if (Current.Kind == TokenKind.Arrow)
        {
            Advance();
            returnType = ParseType();
        }

        var body = ParseBlock("function");
        return new FunctionDeclaration(SpanFrom(start), visibility, name, nameSpan, parameters, returnType, body);
    }

    private Parameter ParseParameter()
    {
        var start = Current.Start;
        EnterNesting(start);
        // S20: `_` is a valid parameter name: an anonymous, unreadable parameter.
        var (name, nameSpan) = ExpectName(allowWildcard: true);
        var next = Current;
        if (next.Kind != TokenKind.Colon)
        {
            throw next.Kind is TokenKind.Comma or TokenKind.RightParenthesis
                ? ErrorAt(ErrorCodes.Parse0042, nameSpan.Start, nameSpan.End, ("name", name))
                : Unexpected(next, "`:`");
        }

        Advance();
        var type = ParseType();
        LeaveNesting();
        return new Parameter(SpanFrom(start), name, type);
    }

    private Token RequireOpeningBrace(string construct)
    {
        var token = Current;
        if (token.Kind == TokenKind.LeftBrace)
        {
            return token;
        }

        if (token.Kind == TokenKind.NewLine && Fetch(position + 1).Kind == TokenKind.LeftBrace)
        {
            throw ErrorAt(ErrorCodes.Parse0028, token.Start, token.End, ("construct", construct));
        }

        throw Unexpected(token, "`{`");
    }

    private BlockStatement ParseBlock(string construct)
    {
        var open = RequireOpeningBrace(construct);
        EnterNesting(open.Start);
        Advance();
        PushContext(ContextKind.Block, open);
        var statements = ParseStatementList(TokenKind.RightBrace, isTopLevel: false);
        Expect(TokenKind.RightBrace, "`}`");
        PopContext();
        LeaveNesting();
        return new BlockStatement(SpanFrom(open.Start), statements);
    }

    private IfStatement ParseIf()
    {
        var start = Current.Start;
        var branches = new List<IfBranch>();
        BlockStatement? elseBlock = null;
        var branchStart = Advance().Start;
        while (true)
        {
            branches.Add(ParseIfBranch(branchStart));
            if (Fetch(position).Kind == TokenKind.NewLine && Fetch(position + 1).Kind == TokenKind.Else)
            {
                position++;
            }

            if (Current.Kind != TokenKind.Else)
            {
                break;
            }

            Advance();
            var afterElse = Current;
            if (afterElse.Kind == TokenKind.NewLine)
            {
                RejectLineBreakAfterElse(afterElse);
            }

            if (afterElse.Kind == TokenKind.If)
            {
                branchStart = Advance().Start;
                continue;
            }

            elseBlock = ParseBlock("else");
            break;
        }

        return new IfStatement(SpanFrom(start), branches, elseBlock);
    }

    private void RejectLineBreakAfterElse(Token newLine)
    {
        var following = Fetch(position + 1).Kind;
        if (following == TokenKind.If)
        {
            throw ErrorAt(ErrorCodes.Parse0029, newLine);
        }

        if (following == TokenKind.LeftBrace)
        {
            throw ErrorAt(ErrorCodes.Parse0028, newLine, ("construct", "else"));
        }
    }

    private IfBranch ParseIfBranch(int start)
    {
        EnterNesting(start);
        var condition = ParseExpression();
        var body = ParseBlock("if");
        LeaveNesting();
        return new IfBranch(SpanFrom(start), condition, body);
    }

    private WhileStatement ParseWhile()
    {
        var start = Advance().Start;
        var condition = ParseExpression();
        var body = ParseBlock("while");
        return new WhileStatement(SpanFrom(start), condition, body);
    }

    private ForStatement ParseFor()
    {
        var start = Advance().Start;
        var (name, nameSpan) = ExpectName(allowWildcard: true);
        Expect(TokenKind.In, "`in`");
        var iterable = ParseExpression();
        var body = ParseBlock("for");
        return new ForStatement(SpanFrom(start), name, nameSpan, iterable, body);
    }

    private ReturnStatement ParseReturn()
    {
        var start = Advance().Start;
        var hasValue = Current.Kind is not (TokenKind.NewLine or TokenKind.Semicolon or TokenKind.RightBrace or TokenKind.EndOfFile);
        var value = hasValue ? ParseExpression() : null;
        return new ReturnStatement(SpanFrom(start), value);
    }

    private ExpressionStatement ParseMatchStatement()
    {
        var start = Current.Start;
        EnterNesting(start);
        var match = ParseMatch(isStatement: true);
        LeaveNesting();
        var follower = Current;
        if (follower.Kind is TokenKind.Dot or TokenKind.Question or TokenKind.LeftBracket or TokenKind.LeftParenthesis)
        {
            throw ErrorAt(ErrorCodes.Parse0048, follower, ("token", Describe(follower)));
        }

        return new ExpressionStatement(match.Span, match);
    }

    private Statement ParseExpressionOrAssignment()
    {
        var first = Current;
        var expression = ParseExpression();
        var next = Current;
        if (TryGetAssignOperator(next.Kind, out var assignOperator))
        {
            return ParseAssignment(first, expression, next, assignOperator);
        }

        if (next.Kind == TokenKind.PipeGreater)
        {
            // The removed operator is rejected in every position, including statement position,
            // where the generic "invalid statement" rule would otherwise win (DESIGN §5.4).
            throw ErrorAt(ErrorCodes.Parse0055, next);
        }

        if (next.Kind == TokenKind.Question || !IsValidExpressionStatement(expression))
        {
            throw ErrorAt(ErrorCodes.Parse0039, first);
        }

        return new ExpressionStatement(SpanFrom(first.Start), expression);
    }

    private AssignmentStatement ParseAssignment(Token first, Expression target, Token operatorToken, AssignOperator assignOperator)
    {
        var indexes = new List<Expression>();
        var root = target;
        while (root is IndexExpression indexExpression)
        {
            indexes.Insert(0, indexExpression.Index);
            root = indexExpression.Target;
        }

        if (first.Kind != TokenKind.Identifier || root is not IdentifierExpression identifier)
        {
            throw ErrorAt(ErrorCodes.Parse0040, first);
        }

        Advance();
        var value = ParseExpression();
        return new AssignmentStatement(SpanFrom(first.Start), identifier.Name, identifier.Span, indexes, assignOperator, value, SpanOf(operatorToken));
    }

    private static bool TryGetAssignOperator(TokenKind kind, out AssignOperator assignOperator)
    {
        assignOperator = kind switch
        {
            TokenKind.PlusEqual => AssignOperator.AddAssign,
            TokenKind.MinusEqual => AssignOperator.SubtractAssign,
            TokenKind.StarEqual => AssignOperator.MultiplyAssign,
            TokenKind.SlashEqual => AssignOperator.DivideAssign,
            TokenKind.PercentEqual => AssignOperator.ModuloAssign,
            _ => AssignOperator.Assign,
        };
        return kind is TokenKind.Equal or TokenKind.PlusEqual or TokenKind.MinusEqual or TokenKind.StarEqual or TokenKind.SlashEqual or TokenKind.PercentEqual;
    }

    /// <summary>
    /// §5.7: a call or pipeline (a pipeline always ends with a call), followed by at most one `?`. Parentheses have no
    /// node of their own (DESIGN §3.2, S12), so `(f())` is the call `f()` with no bare parentheses to strip.
    /// </summary>
    private static bool IsValidExpressionStatement(Expression expression)
    {
        var core = expression is PropagateExpression propagation ? propagation.Operand : expression;
        return core is CallExpression;
    }

    /// <summary>
    /// Enforces the documented syntax depth (DESIGN §1.1, SCRIPTS-PARSE-0005) on every statement, at the top
    /// level and inside blocks: program = 1, each statement and each sub-expression +1. Iterative, so it costs no
    /// stack.
    /// </summary>
    private void RejectExcessiveDepth(Statement statement)
    {
        if (SyntaxDepthChecker.FindFirstTooDeepNode(statement, 2) is { } node)
        {
            throw ErrorAt(ErrorCodes.Parse0005, node.Span.Start, Math.Max(node.Span.End, node.Span.Start + 1));
        }
    }
}
