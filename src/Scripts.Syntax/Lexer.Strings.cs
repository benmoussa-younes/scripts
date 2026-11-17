using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Scripts.Core;

namespace Scripts.Syntax;

internal sealed partial class Lexer
{
    /// <summary>Nested strings deeper than this cannot fit in the 200-level AST limit (each level costs two nodes).</summary>
    private const int MaximumInterpolationNesting = 99;

    /// <summary>Reads a string whose opening quote (after an optional r or p prefix) has just been consumed.</summary>
    private Token ReadString(int tokenStart, StringStyle style) =>
        style == StringStyle.Raw ? ReadRawString(tokenStart) : new StringScanner(this, tokenStart, style).Scan();

    /// <summary>Reads `r"""…"""`: the first following `"""` closes; CRLF is normalized to LF in the value only.</summary>
    private Token ReadRawMultilineString(int tokenStart)
    {
        var contentStart = offset;
        var value = new StringBuilder();
        while (offset < text.Length)
        {
            if (text[offset] == '"' && offset + 2 < text.Length && text[offset + 1] == '"' && text[offset + 2] == '"')
            {
                var contentEnd = offset;
                offset += 3;
                IReadOnlyList<StringSegment> segments = contentEnd == contentStart
                    ? []
                    : [new TextSegment(contentStart, contentEnd, value.ToString())];
                return new StringToken(tokenStart, offset, StringStyle.Raw, segments, null);
            }

            if (text[offset] == '\r')
            {
                if (offset + 1 < text.Length && text[offset + 1] == '\n')
                {
                    value.Append('\n');
                    offset += 2;
                    continue;
                }

                return Fail(ErrorCodes.Parse0003, offset, offset + 1);
            }

            value.Append(text[offset]);
            offset++;
        }

        return Fail(ErrorCodes.Parse0016, tokenStart, tokenStart + 4);
    }

    private Token ReadRawString(int tokenStart)
    {
        var contentStart = offset;
        while (offset < text.Length && text[offset] != '"' && text[offset] != '\n')
        {
            if (text[offset] == '\r')
            {
                if (offset + 1 < text.Length && text[offset + 1] == '\n')
                {
                    break;
                }

                return Fail(ErrorCodes.Parse0003, offset, offset + 1);
            }

            offset++;
        }

        if (offset >= text.Length || text[offset] != '"')
        {
            return Fail(ErrorCodes.Parse0016, tokenStart, tokenStart + 2);
        }

        var content = text.Substring(contentStart, offset - contentStart);
        offset++;
        IReadOnlyList<StringSegment> segments = content.Length == 0 ? [] : [new TextSegment(contentStart, contentStart + content.Length, content)];
        return new StringToken(tokenStart, offset, StringStyle.Raw, segments, null);
    }

    private sealed class StringScanner
    {
        private readonly Lexer lexer;
        private readonly int tokenStart;
        private readonly StringStyle style;
        private readonly List<StringSegment> segments = [];
        private readonly StringBuilder pendingText = new();
        private int pendingTextStart;

        public StringScanner(Lexer lexer, int tokenStart, StringStyle style)
        {
            this.lexer = lexer;
            this.tokenStart = tokenStart;
            this.style = style;
        }

        public Token Scan()
        {
            var text = lexer.text;
            pendingTextStart = lexer.offset;
            if (lexer.interpolationNesting >= MaximumInterpolationNesting || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
            {
                return lexer.Fail(ErrorCodes.Parse0005, tokenStart, tokenStart + 1);
            }

            while (true)
            {
                if (lexer.offset >= text.Length)
                {
                    return Unterminated();
                }

                var current = text[lexer.offset];
                Token? finished = null;
                switch (current)
                {
                    case '"':
                        FlushText(lexer.offset);
                        lexer.offset++;
                        return new StringToken(tokenStart, lexer.offset, style, segments, null);
                    case '\n':
                        return Unterminated();
                    case '\r':
                        return lexer.offset + 1 < text.Length && text[lexer.offset + 1] == '\n'
                            ? Unterminated()
                            : Failure(ErrorCodes.Parse0003, lexer.offset, lexer.offset + 1);
                    case '\\':
                        finished = ScanEscape();
                        break;
                    case '{':
                        if (style == StringStyle.Path)
                        {
                            return Failure(ErrorCodes.Parse0053, lexer.offset, lexer.offset + 1);
                        }

                        finished = ScanInterpolation();
                        break;
                    case '}':
                        return Failure(ErrorCodes.Parse0021, lexer.offset, lexer.offset + 1);
                    default:
                        pendingText.Append(current);
                        lexer.offset++;
                        break;
                }

                if (finished is not null)
                {
                    return finished;
                }
            }
        }

        private Token? ScanEscape()
        {
            var text = lexer.text;
            var escapeStart = lexer.offset;
            if (escapeStart + 1 >= text.Length || text[escapeStart + 1] is '\n' or '\r')
            {
                return Unterminated();
            }

            var code = text[escapeStart + 1];
            char? decoded = code switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '0' => '\0',
                '\\' => '\\',
                '"' => '"',
                '{' => '{',
                '}' => '}',
                _ => null,
            };
            if (decoded is { } value)
            {
                pendingText.Append(value);
                lexer.offset += 2;
                return null;
            }

            if (code == 'u')
            {
                return ScanUnicodeEscape(escapeStart);
            }

            var length = char.IsHighSurrogate(code) && escapeStart + 2 < text.Length && char.IsLowSurrogate(text[escapeStart + 2]) ? 2 : 1;
            return Failure(ErrorCodes.Parse0017, escapeStart, escapeStart + 1 + length, ("escape", text.Substring(escapeStart + 1, length)));
        }

        private Token? ScanUnicodeEscape(int escapeStart)
        {
            var text = lexer.text;
            var cursor = escapeStart + 2;
            if (cursor >= text.Length || text[cursor] != '{')
            {
                return InvalidUnicodeEscape(escapeStart, cursor);
            }

            cursor++;
            var digitsStart = cursor;
            while (cursor < text.Length && Uri.IsHexDigit(text[cursor]))
            {
                cursor++;
            }

            var digitCount = cursor - digitsStart;
            var isClosed = cursor < text.Length && text[cursor] == '}';
            var end = isClosed ? cursor + 1 : cursor;
            if (!isClosed || digitCount is < 1 or > 6)
            {
                return InvalidUnicodeEscape(escapeStart, end);
            }

            var scalar = int.Parse(text.AsSpan(digitsStart, digitCount), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            if (scalar > 0x10FFFF || scalar is >= 0xD800 and <= 0xDFFF)
            {
                return InvalidUnicodeEscape(escapeStart, end);
            }

            pendingText.Append(char.ConvertFromUtf32(scalar));
            lexer.offset = end;
            return null;
        }

        private Token InvalidUnicodeEscape(int escapeStart, int end) =>
            Failure(ErrorCodes.Parse0018, escapeStart, end, ("escape", lexer.text.Substring(escapeStart, end - escapeStart)));

        /// <summary>Collects the tokens of one interpolation; a nested error ends the string and surfaces when the parser reaches it.</summary>
        private Token? ScanInterpolation()
        {
            var openOffset = lexer.offset;
            FlushText(openOffset);
            lexer.offset++;
            lexer.interpolationNesting++;
            var tokens = new List<Token>();
            var braceDepth = 0;
            while (true)
            {
                var token = lexer.ReadToken();
                if (token.IsFaulty)
                {
                    tokens.Add(token);
                    lexer.interpolationNesting--;
                    segments.Add(new InterpolationSegment(openOffset, lexer.offset, tokens));
                    return new StringToken(tokenStart, lexer.offset, style, segments, null);
                }

                if (token.Kind is TokenKind.NewLine or TokenKind.EndOfFile)
                {
                    lexer.interpolationNesting--;
                    return Failure(ErrorCodes.Parse0020, openOffset, openOffset + 1);
                }

                if (token.Kind == TokenKind.RightBrace && braceDepth == 0)
                {
                    break;
                }

                braceDepth += token.Kind == TokenKind.LeftBrace ? 1 : token.Kind == TokenKind.RightBrace ? -1 : 0;
                tokens.Add(token);
            }

            lexer.interpolationNesting--;
            segments.Add(new InterpolationSegment(openOffset, lexer.offset, tokens));
            pendingTextStart = lexer.offset;
            return null;
        }

        private void FlushText(int endOffset)
        {
            if (pendingText.Length > 0)
            {
                segments.Add(new TextSegment(pendingTextStart, endOffset, pendingText.ToString()));
                pendingText.Clear();
            }

            pendingTextStart = endOffset;
        }

        private Token Unterminated() =>
            Failure(ErrorCodes.Parse0016, tokenStart, tokenStart + (style == StringStyle.Normal ? 1 : 2));

        private StringToken Failure(string code, int start, int end, params (string Name, string Value)[] arguments)
        {
            lexer.stopped = true;
            var diagnostic = Diagnostics.Create(code, lexer.source, new Span(start, end), arguments);
            return new StringToken(tokenStart, Math.Max(lexer.offset, end), style, segments, diagnostic);
        }
    }
}
