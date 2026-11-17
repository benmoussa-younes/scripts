using System.Runtime.CompilerServices;
using Scripts.Core;

namespace Scripts.Syntax;

/// <summary>Hand-written recursive-descent parser over a pull lexer; the first error aborts through <see cref="SyntaxErrorException"/>.</summary>
internal sealed partial class SyntaxParser
{
    private const string ExpectedExpression = "an expression";
    private const string ExpectedStatementEnd = "the end of the statement (newline or `;`)";

    private readonly SourceFile source;
    private readonly string text;
    private readonly bool isModule;
    private readonly List<Context> contexts = [];
    private Lexer? lexer;
    private List<Token> tokens = [];
    private int position;
    private int previousEnd;

    /// <summary>Depth of the node being parsed; the program itself is level 1.</summary>
    private int nestingDepth = 1;

    internal SyntaxParser(SourceFile source, bool isModule, NameTable? names = null)
    {
        this.source = source;
        text = source.Text;
        this.isModule = isModule;
        lexer = new Lexer(source, names);
    }

    private enum ContextKind
    {
        Block,
        Match,
        Parenthesis,
        Bracket,
        Map,
        Interpolation,
    }

    private readonly record struct Context(ContextKind Kind, Token? Opening);

    /// <summary>Parses the leading `import` declarations; modules are loaded before the rest of the file is read.</summary>
    internal bool TryParseImports(out IReadOnlyList<ImportDeclaration> imports, out Diagnostic? error)
    {
        try
        {
            imports = ParseImportHeader();
            error = null;
            return true;
        }
        catch (SyntaxErrorException failure)
        {
            imports = [];
            error = failure.Diagnostic;
            return false;
        }
    }

    internal bool TryParseBody(IReadOnlyList<ImportDeclaration> imports, out ProgramNode? program, out Diagnostic? error)
    {
        try
        {
            var statements = ParseStatementList(TokenKind.EndOfFile, isTopLevel: true);
            program = new ProgramNode(new Span(0, text.Length), imports, statements);
            error = null;
            return true;
        }
        catch (SyntaxErrorException failure)
        {
            program = null;
            error = failure.Diagnostic;
            return false;
        }
    }

    private Token Fetch(int index)
    {
        while (tokens.Count <= index && lexer is not null && (tokens.Count == 0 || tokens[^1].Kind != TokenKind.EndOfFile))
        {
            tokens.Add(lexer.ReadToken());
        }

        return tokens[Math.Min(index, tokens.Count - 1)];
    }

    /// <summary>Next token with the insignificant line break (§1.3 rules) already skipped.</summary>
    private Token Current
    {
        get
        {
            var raw = Fetch(position);
            if (raw.Kind == TokenKind.NewLine && ShouldIgnoreNewLine(raw, Fetch(position + 1)))
            {
                position++;
            }

            return Fetch(position);
        }
    }

    private bool IgnoresNewLines =>
        contexts.Count > 0
        && contexts[^1].Kind is ContextKind.Parenthesis or ContextKind.Bracket or ContextKind.Map or ContextKind.Interpolation;

    private bool ShouldIgnoreNewLine(Token newLine, Token following)
    {
        var previous = position > 0 ? tokens[position - 1] : null;
        if (following.Kind == TokenKind.Dot || (previous is not null && IsContinuation(previous)))
        {
            return true;
        }

        if (!IgnoresNewLines)
        {
            return false;
        }

        if (previous is not null && EndsExpression(previous.Kind) && following.Kind is TokenKind.Minus or TokenKind.LeftParenthesis or TokenKind.LeftBracket)
        {
            throw ErrorAt(ErrorCodes.Parse0030, newLine.Start, newLine.End, ("token", Describe(following)));
        }

        return true;
    }

    private static bool IsContinuation(Token previous) =>
        previous.Kind switch
        {
            TokenKind.Plus or TokenKind.Minus or TokenKind.Star or TokenKind.Slash or TokenKind.Percent => true,
            TokenKind.EqualEqual or TokenKind.NotEqual or TokenKind.Less or TokenKind.LessEqual or TokenKind.GreaterEqual => true,
            TokenKind.And or TokenKind.Or or TokenKind.Not or TokenKind.Comma => true,
            TokenKind.Equal or TokenKind.FatArrow or TokenKind.Arrow or TokenKind.Dot or TokenKind.Colon => true,
            TokenKind.PlusEqual or TokenKind.MinusEqual or TokenKind.StarEqual or TokenKind.SlashEqual or TokenKind.PercentEqual => true,
            TokenKind.Greater => !previous.ClosesTypeArguments,
            _ => false,
        };

    private static bool EndsExpression(TokenKind kind) =>
        kind is TokenKind.Identifier or TokenKind.Integer or TokenKind.Float or TokenKind.String or TokenKind.True or TokenKind.False
            or TokenKind.RightParenthesis or TokenKind.RightBracket or TokenKind.RightBrace or TokenKind.Question;

    private Token Advance()
    {
        var token = Current;
        if (token.Kind == TokenKind.Error)
        {
            throw new SyntaxErrorException(token.Failure!);
        }

        if (token.Kind != TokenKind.EndOfFile)
        {
            position++;
        }

        if (token.Kind != TokenKind.NewLine)
        {
            previousEnd = token.End;
        }

        return token;
    }

    private Token Expect(TokenKind kind, string expected)
    {
        var token = Current;
        return token.Kind == kind ? Advance() : throw Unexpected(token, expected);
    }

    private void SkipNewLines()
    {
        while (Current.Kind == TokenKind.NewLine)
        {
            Advance();
        }
    }

    private void PushContext(ContextKind kind, Token? opening) => contexts.Add(new Context(kind, opening));

    private void PopContext() => contexts.RemoveAt(contexts.Count - 1);

    /// <summary>
    /// Recursion guard of the parser. It does NOT enforce the documented depth limit: that limit is measured on the
    /// final syntax tree by <see cref="SyntaxDepthChecker"/> (one convention, DESIGN §1.1), because a level of
    /// nesting here does not map one-to-one onto a level of the tree (an `if` opens a statement, an expression and a
    /// block, so the old counter refused valid scripts at about 66 real levels). Only the real stack is guarded,
    /// which is what keeps an absurd input from crashing the process (exit 139) instead of reporting
    /// <c>SCRIPTS-PARSE-0005</c>; a script within the documented limit never hits this guard.
    /// </summary>
    private void EnterNesting(int startOffset)
    {
        nestingDepth++;
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw ErrorAt(ErrorCodes.Parse0005, startOffset, startOffset + 1);
        }
    }

    private void LeaveNesting() => nestingDepth--;

    private Span SpanFrom(int start) => new(start, previousEnd);

    private static Span SpanOf(Token token) => new(token.Start, token.End);

    private SyntaxErrorException ErrorAt(string code, int start, int end, params (string Name, string Value)[] arguments) =>
        new(Diagnostics.Create(code, source, new Span(start, end), arguments));

    /// <summary>Error located on a token; when that token is itself a lexical error, the lexical error wins.</summary>
    private SyntaxErrorException ErrorAt(string code, Token token, params (string Name, string Value)[] arguments) =>
        token.Kind == TokenKind.Error
            ? new SyntaxErrorException(token.Failure!)
            // S6: at end of file the token is empty; the diagnostic covers one code point (`end_column = column + 1`).
            : ErrorAt(code, token.Start, token.Kind == TokenKind.EndOfFile ? token.End + 1 : token.End, arguments);

    private SyntaxErrorException Unexpected(Token found, string expected)
    {
        if (found.Kind == TokenKind.PipeGreater)
        {
            return ErrorAt(ErrorCodes.Parse0055, found);
        }

        if (found.Kind == TokenKind.EndOfFile && contexts.Count > 0 && contexts[^1].Opening is { } opening)
        {
            return Unclosed(opening);
        }

        if (found.Kind == TokenKind.Pipe)
        {
            return ErrorAt(ErrorCodes.Parse0031, found);
        }

        return ErrorAt(ErrorCodes.Parse0023, found, ("found", Describe(found)), ("expected", expected));
    }

    private SyntaxErrorException Unclosed(Token opening)
    {
        var delimiter = text[opening.Start];
        var closing = delimiter switch { '{' => "}", '(' => ")", _ => "]" };
        return ErrorAt(
            ErrorCodes.Parse0024,
            opening.Start,
            opening.End,
            ("delimiter", delimiter.ToString()),
            ("open_line", source.GetPosition(opening.Start).Line.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("closing", closing));
    }

    private string Describe(Token token) =>
        token.Kind switch
        {
            TokenKind.NewLine => "end of line",
            TokenKind.EndOfFile => token.Text.Length > 0 ? token.Text : "end of file",
            _ => Abbreviate(text.Substring(token.Start, token.End - token.Start)),
        };

    private static string Abbreviate(string value) => value.Length <= 40 ? value : value.Substring(0, 40) + "…";

    /// <summary>Name in a declaration position (variable, parameter, function, alias, loop variable, binding).</summary>
    private (string Name, Span Span) ExpectName(bool allowWildcard)
    {
        var token = Current;
        if (token.Kind == TokenKind.Identifier && (allowWildcard || token.Text != "_"))
        {
            RejectFutureReserved(token);
            Advance();
            return (token.Text, SpanOf(token));
        }

        if (TokenKinds.IsKeyword(token.Kind))
        {
            throw ErrorAt(ErrorCodes.Parse0025, token, ("keyword", token.Text));
        }

        throw Unexpected(token, "a name");
    }

    private void RejectFutureReserved(Token identifier)
    {
        if (LanguageTables.IsFutureReserved(identifier.Text))
        {
            throw ErrorAt(ErrorCodes.Parse0026, identifier, ("word", identifier.Text));
        }
    }

    /// <summary>Method, field, module-function or stage name after `.` or `|&gt;`.</summary>
    private (string Name, Span Span) ExpectMemberName()
    {
        var token = Current;
        if (token.Kind != TokenKind.Identifier || token.Text == "_")
        {
            throw Unexpected(token, "a name");
        }

        RejectFutureReserved(token);
        Advance();
        return (token.Text, SpanOf(token));
    }
}
