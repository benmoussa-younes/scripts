using Scripts.Core;

namespace Scripts.Syntax;

/// <summary>Pull lexer: the parser asks for one token at a time; after an error it only yields end-of-file.</summary>
internal sealed partial class Lexer
{
    private static readonly Dictionary<string, TokenKind> Keywords = BuildKeywords();

    private static Dictionary<string, TokenKind> BuildKeywords()
    {
        var keywords = new Dictionary<string, TokenKind>(StringComparer.Ordinal);
        foreach (var keyword in LanguageTables.Keywords)
        {
            keywords[keyword] = KeywordKind(keyword);
        }

        return keywords;
    }

    private static TokenKind KeywordKind(string keyword) => keyword switch
    {
        "let" => TokenKind.Let,
        "mutable" => TokenKind.Mutable,
        "function" => TokenKind.Function,
        "return" => TokenKind.Return,
        "if" => TokenKind.If,
        "else" => TokenKind.Else,
        "while" => TokenKind.While,
        "for" => TokenKind.For,
        "in" => TokenKind.In,
        "match" => TokenKind.Match,
        "break" => TokenKind.Break,
        "continue" => TokenKind.Continue,
        "import" => TokenKind.Import,
        "as" => TokenKind.As,
        "public" => TokenKind.Public,
        "private" => TokenKind.Private,
        "true" => TokenKind.True,
        "false" => TokenKind.False,
        "and" => TokenKind.And,
        "or" => TokenKind.Or,
        "not" => TokenKind.Not,
        _ => throw new InvalidOperationException($"missing lexer token kind for keyword `{keyword}`"),
    };

    private readonly SourceFile source;
    private readonly string text;
    private readonly NameTable names;
    private int offset;
    private bool stopped;
    private int interpolationNesting;

    public Lexer(SourceFile source, NameTable? names = null)
    {
        this.source = source;
        text = source.Text;
        this.names = names ?? new NameTable();
    }

    public Token ReadToken()
    {
        if (stopped)
        {
            return new Token(TokenKind.EndOfFile, text.Length, text.Length);
        }

        var newlineStart = -1;
        var insideComment = false;
        while (offset < text.Length)
        {
            var current = text[offset];
            if (current == '\n')
            {
                insideComment = false;
                newlineStart = newlineStart < 0 ? offset : newlineStart;
                offset++;
            }
            else if (current == '\r')
            {
                if (offset + 1 >= text.Length || text[offset + 1] != '\n')
                {
                    if (newlineStart >= 0)
                    {
                        break;
                    }

                    return Fail(ErrorCodes.Parse0003, offset, offset + 1);
                }

                insideComment = false;
                newlineStart = newlineStart < 0 ? offset : newlineStart;
                offset += 2;
            }
            else if (insideComment || current is ' ' or '\t')
            {
                offset++;
            }
            else if (current == '#')
            {
                insideComment = true;
                offset++;
            }
            else
            {
                break;
            }
        }

        if (newlineStart >= 0)
        {
            return new Token(TokenKind.NewLine, newlineStart, newlineStart + 1);
        }

        if (offset >= text.Length)
        {
            return new Token(TokenKind.EndOfFile, text.Length, text.Length);
        }

        var token = ReadSignificantToken();
        token.IsPrecededByBlank = token.Start > 0 && text[token.Start - 1] is ' ' or '\t';
        return token;
    }

    private Token ReadSignificantToken()
    {
        var start = offset;
        var current = text[start];
        if (IsIdentifierStart(current))
        {
            return ReadWord();
        }

        if (IsDigit(current))
        {
            return ReadNumber();
        }

        var next = start + 1 < text.Length ? text[start + 1] : '\0';
        switch (current)
        {
            case '"':
                offset++;
                return ReadString(start, StringStyle.Normal);
            case '+':
                return next == '+' ? ForbiddenOperator("++", "+= 1") : next == '=' ? Simple(TokenKind.PlusEqual, 2) : Simple(TokenKind.Plus, 1);
            case '-':
                return ReadMinus(next);
            case '*':
                return next == '=' ? Simple(TokenKind.StarEqual, 2) : Simple(TokenKind.Star, 1);
            case '/':
                return next == '=' ? Simple(TokenKind.SlashEqual, 2) : Simple(TokenKind.Slash, 1);
            case '%':
                return next == '=' ? Simple(TokenKind.PercentEqual, 2) : Simple(TokenKind.Percent, 1);
            case '=':
                return next == '=' ? Simple(TokenKind.EqualEqual, 2) : next == '>' ? Simple(TokenKind.FatArrow, 2) : Simple(TokenKind.Equal, 1);
            case '!':
                return next == '=' ? Simple(TokenKind.NotEqual, 2) : ForbiddenOperator("!", "not");
            case '<':
                return next == '=' ? Simple(TokenKind.LessEqual, 2) : Simple(TokenKind.Less, 1);
            case '>':
                return next == '=' ? Simple(TokenKind.GreaterEqual, 2) : Simple(TokenKind.Greater, 1);
            case '|':
                return next == '>' ? Simple(TokenKind.PipeGreater, 2) : next == '|' ? ForbiddenOperator("||", "or") : Simple(TokenKind.Pipe, 1);
            case '&':
                return next == '&' ? ForbiddenOperator("&&", "and") : ForbiddenOperator("&", "process.spawn(…)");
            case '?':
                return Simple(TokenKind.Question, 1);
            case '.':
                return Simple(TokenKind.Dot, 1);
            case ',':
                return Simple(TokenKind.Comma, 1);
            case ':':
                return Simple(TokenKind.Colon, 1);
            case ';':
                return Simple(TokenKind.Semicolon, 1);
            case '(':
                return Simple(TokenKind.LeftParenthesis, 1);
            case ')':
                return Simple(TokenKind.RightParenthesis, 1);
            case '[':
                return Simple(TokenKind.LeftBracket, 1);
            case ']':
                return Simple(TokenKind.RightBracket, 1);
            case '{':
                return Simple(TokenKind.LeftBrace, 1);
            case '}':
                return Simple(TokenKind.RightBrace, 1);
            default:
                return ReadUnexpectedCharacter();
        }
    }

    private Token ReadMinus(char next)
    {
        if (next == '-')
        {
            return EndsExpressionAfterBlanks(offset + 2) ? ForbiddenOperator("--", "-= 1") : Simple(TokenKind.Minus, 1);
        }

        return next == '=' ? Simple(TokenKind.MinusEqual, 2) : next == '>' ? Simple(TokenKind.Arrow, 2) : Simple(TokenKind.Minus, 1);
    }

    /// <summary>`--` followed by the end of an expression is a decrement attempt; otherwise `5--3` is `5 - -3`.</summary>
    private bool EndsExpressionAfterBlanks(int cursor)
    {
        while (cursor < text.Length && text[cursor] is ' ' or '\t')
        {
            cursor++;
        }

        return cursor >= text.Length || text[cursor] is '\n' or '\r' or ';' or '#' or ')' or ']' or '}' or ',';
    }

    private Token Simple(TokenKind kind, int length)
    {
        var start = offset;
        offset += length;
        return new Token(kind, start, offset);
    }

    private Token ForbiddenOperator(string operatorText, string replacement) =>
        Fail(ErrorCodes.Parse0008, offset, offset + operatorText.Length, ("operator", operatorText), ("replacement", replacement));

    private Token ReadWord()
    {
        var start = offset;
        while (offset < text.Length && IsIdentifierPart(text[offset]))
        {
            offset++;
        }

        var word = text.Substring(start, offset - start);
        if (word.Length == 1 && offset < text.Length && text[offset] == '"' && word[0] is 'r' or 'p')
        {
            if (word[0] == 'r' && offset + 2 < text.Length && text[offset + 1] == '"' && text[offset + 2] == '"')
            {
                offset += 3;
                return ReadRawMultilineString(start);
            }

            offset++;
            return ReadString(start, word[0] == 'r' ? StringStyle.Raw : StringStyle.Path);
        }

        // Only identifiers are interned: keywords form a fixed set with no possible collision.
        return Keywords.TryGetValue(word, out var kind)
            ? new Token(kind, start, offset, word)
            : new Token(TokenKind.Identifier, start, offset, names.Intern(word));
    }

    private Token ReadUnexpectedCharacter()
    {
        var start = offset;
        var current = text[start];
        if (current < 0x80)
        {
            var shown = char.IsControl(current) ? $"U+{(int)current:X4}" : current.ToString();
            return Fail(ErrorCodes.Parse0006, start, start + 1, ("character", shown));
        }

        var length = char.IsHighSurrogate(current) && start + 1 < text.Length && char.IsLowSurrogate(text[start + 1]) ? 2 : 1;
        return Fail(
            ErrorCodes.Parse0007,
            start,
            start + length,
            ("character", text.Substring(start, length)),
            ("ascii_name", AsciiNames.Suggest(text, start)));
    }

    private ErrorToken Fail(string code, int start, int end, params (string Name, string Value)[] arguments)
    {
        stopped = true;
        return new ErrorToken(Diagnostics.Create(code, source, new Span(start, end), arguments), start, end);
    }

    private static bool IsIdentifierStart(char character) => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';

    private static bool IsIdentifierPart(char character) => IsIdentifierStart(character) || IsDigit(character);

    private static bool IsDigit(char character) => character is >= '0' and <= '9';
}
