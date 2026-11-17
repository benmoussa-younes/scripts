using System.Globalization;
using Scripts.Core;

namespace Scripts.Syntax.Tests;

internal static class Lexing
{
    public static List<Token> Tokens(string text)
    {
        var lexer = new Lexer(new SourceFile("test.scs", text));
        var tokens = new List<Token>();
        while (true)
        {
            var token = lexer.ReadToken();
            if (token.Kind == TokenKind.EndOfFile)
            {
                return tokens;
            }

            tokens.Add(token);
        }
    }

    public static string Render(string text) => string.Join(" ", Tokens(text).Select(Render));

    public static string Render(Token token) =>
        token switch
        {
            IntegerToken integer => "int:" + integer.Value.ToString(CultureInfo.InvariantCulture),
            FloatToken number => "float:" + number.Value.ToString("R", CultureInfo.InvariantCulture),
            ErrorToken error => "error:" + error.Failure!.Code[^4..],
            StringToken text => RenderString(text),
            _ when token.Kind == TokenKind.Identifier => "id:" + token.Text,
            _ when TokenKinds.IsKeyword(token.Kind) => "kw:" + token.Text,
            _ when token.Kind == TokenKind.NewLine => "NL",
            _ => token.Kind.ToString(),
        };

    private static string RenderString(StringToken token)
    {
        var parts = token.Segments.Select(segment => segment switch
        {
            TextSegment text => "'" + Escape(text.Text) + "'",
            InterpolationSegment interpolation => "{" + string.Join(" ", interpolation.Tokens.Select(Render)) + "}",
            _ => throw new InvalidOperationException(),
        });
        var failure = token.Failure is null ? string.Empty : "!" + token.Failure.Code[^4..];
        return $"str[{token.Style}]({string.Join(",", parts)}){failure}";
    }

    public static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\0", "\\0", StringComparison.Ordinal);

    public static Diagnostic FirstFailure(string text)
    {
        foreach (var token in Tokens(text))
        {
            if (FailureOf(token) is { } failure)
            {
                return failure;
            }
        }

        throw new InvalidOperationException("No lexical failure in: " + text);
    }

    private static Diagnostic? FailureOf(Token token)
    {
        if (token.Failure is { } direct)
        {
            return direct;
        }

        var inner = token is StringToken text ? text.Segments.OfType<InterpolationSegment>().SelectMany(segment => segment.Tokens) : [];
        return inner.Select(FailureOf).FirstOrDefault(failure => failure is not null);
    }
}

public sealed class LexerTokenTests
{
    [Fact]
    public void Every_operator_and_punctuation_token_is_recognized()
    {
        var rendered = Lexing.Render("+ - * / % == != < <= > >= |> = += -= *= /= %= -> => ? . , : ; | ( ) [ ] { }");
        Assert.Equal(
            "Plus Minus Star Slash Percent EqualEqual NotEqual Less LessEqual Greater GreaterEqual PipeGreater Equal PlusEqual MinusEqual StarEqual SlashEqual PercentEqual Arrow FatArrow Question Dot Comma Colon Semicolon Pipe LeftParenthesis RightParenthesis LeftBracket RightBracket LeftBrace RightBrace",
            rendered);
    }

    [Fact]
    public void Every_keyword_is_a_keyword_token()
    {
        var words = string.Join(" ", LanguageTables.Keywords);
        Assert.Equal(string.Join(" ", LanguageTables.Keywords.Select(word => "kw:" + word)), Lexing.Render(words));
    }

    [Fact]
    public void Keywords_in_the_language_table_match_the_lexer()
    {
        var tokens = Lexing.Tokens(string.Join(" ", LanguageTables.Keywords));
        Assert.Equal(LanguageTables.Keywords, tokens.Select(token => token.Text));
        Assert.All(tokens, token => Assert.True(TokenKinds.IsKeyword(token.Kind), token.Text));
    }

    [Fact]
    public void Parse0026_lists_the_future_reserved_table()
    {
        var info = ErrorCatalog.All.Single(entry => entry.Code == "SCRIPTS-PARSE-0026");
        Assert.Contains(string.Join(' ', LanguageTables.FutureReserved), info.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_operator_described_by_the_language_is_a_token()
    {
        foreach (var symbol in LanguageReference.Operators.Select(operatorInfo => operatorInfo.Symbol))
        {
            var token = Lexing.Tokens(symbol).Single();
            Assert.NotEqual(TokenKind.Identifier, token.Kind);
        }
    }

    [Fact]
    public void Reserved_and_future_words_are_plain_identifiers_for_the_lexer()
    {
        var words = LanguageTables.ReservedIdentifiers.Concat(LanguageTables.FutureReserved).Append("foo").Append("_").Append("_x1").ToArray();
        Assert.Equal(string.Join(" ", words.Select(word => "id:" + word)), Lexing.Render(string.Join(" ", words)));
    }

    [Theory]
    [InlineData("0", "int:0")]
    [InlineData("42", "int:42")]
    [InlineData("1_000_000", "int:1000000")]
    [InlineData("0xFF", "int:255")]
    [InlineData("0xff", "int:255")]
    [InlineData("0x1_F", "int:31")]
    [InlineData("0b1010", "int:10")]
    [InlineData("0o755", "int:493")]
    [InlineData("9223372036854775807", "int:9223372036854775807")]
    [InlineData("0x7FFFFFFFFFFFFFFF", "int:9223372036854775807")]
    [InlineData("1.5", "float:1.5")]
    [InlineData("0.5", "float:0.5")]
    [InlineData("1e3", "float:1000")]
    [InlineData("1E3", "float:1000")]
    [InlineData("2.5e-3", "float:0.0025")]
    [InlineData("1.5e+2", "float:150")]
    [InlineData("1_0.5_0", "float:10.5")]
    [InlineData("0e5", "float:0")]
    [InlineData("0.0", "float:0")]
    [InlineData("1.length", "int:1 Dot id:length")]
    [InlineData("1.e5", "int:1 Dot id:e5")]
    [InlineData("1.5.length", "float:1.5 Dot id:length")]
    [InlineData("5--3", "int:5 Minus Minus int:3")]
    [InlineData("5 - -3", "int:5 Minus Minus int:3")]
    public void Numeric_literals(string input, string expected) => Assert.Equal(expected, Lexing.Render(input));

    [Fact]
    public void Smallest_positive_float_is_accepted()
    {
        var token = Assert.IsType<FloatToken>(Assert.Single(Lexing.Tokens("5e-324")));
        Assert.Equal(double.Epsilon, token.Value);
    }

    [Theory]
    [InlineData("007", ErrorCodes.Parse0011)]
    [InlineData("0_7", ErrorCodes.Parse0011)]
    [InlineData("00.5", ErrorCodes.Parse0011)]
    [InlineData("0XFF", ErrorCodes.Parse0012)]
    [InlineData("0B1", ErrorCodes.Parse0012)]
    [InlineData("0O7", ErrorCodes.Parse0012)]
    [InlineData("0x", ErrorCodes.Parse0013)]
    [InlineData("0b2", ErrorCodes.Parse0013)]
    [InlineData("0o8", ErrorCodes.Parse0013)]
    [InlineData("0xG", ErrorCodes.Parse0013)]
    [InlineData("0b_1", ErrorCodes.Parse0013)]
    [InlineData("1__0", ErrorCodes.Parse0013)]
    [InlineData("1_", ErrorCodes.Parse0013)]
    [InlineData("1_.5", ErrorCodes.Parse0013)]
    [InlineData("1.5_", ErrorCodes.Parse0013)]
    [InlineData("1e", ErrorCodes.Parse0013)]
    [InlineData("1e+", ErrorCodes.Parse0013)]
    [InlineData("12abc", ErrorCodes.Parse0013)]
    [InlineData("1.5x", ErrorCodes.Parse0013)]
    [InlineData("1e05", ErrorCodes.Parse0014)]
    [InlineData("1e-05", ErrorCodes.Parse0014)]
    [InlineData("1.5e+007", ErrorCodes.Parse0014)]
    [InlineData("1e999", ErrorCodes.Parse0015)]
    [InlineData("1e-999", ErrorCodes.Parse0015)]
    [InlineData("1e-324", ErrorCodes.Parse0015)]
    [InlineData("9223372036854775808", ErrorCodes.Parse0010)]
    [InlineData("99999999999999999999", ErrorCodes.Parse0010)]
    [InlineData("0xFFFFFFFFFFFFFFFF", ErrorCodes.Parse0010)]
    [InlineData("0b1111111111111111111111111111111111111111111111111111111111111111", ErrorCodes.Parse0010)]
    public void Malformed_numeric_literals(string input, string code)
    {
        var token = Assert.Single(Lexing.Tokens(input));
        Assert.Equal(code, token.Failure!.Code);
        Assert.Equal((1, 1), token.Failure.Start);
    }

    [Fact]
    public void Numeric_error_messages_carry_the_catalog_placeholders()
    {
        Assert.Contains("007", Lexing.FirstFailure("007").Message, StringComparison.Ordinal);
        Assert.Contains("7", Lexing.FirstFailure("007").Suggestion, StringComparison.Ordinal);
        Assert.Contains("0xFF", Lexing.FirstFailure("0XFF").Suggestion, StringComparison.Ordinal);
        Assert.Contains("1e5", Lexing.FirstFailure("1e05").Suggestion, StringComparison.Ordinal);
        Assert.Contains("9223372036854775808", Lexing.FirstFailure("9223372036854775808").Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("&&", "&&", "and")]
    [InlineData("||", "||", "or")]
    [InlineData("!", "!", "not")]
    [InlineData("&", "&", "process.spawn")]
    [InlineData("++", "++", "+= 1")]
    [InlineData("x--", "--", "-= 1")]
    [InlineData("x-- )", "--", "-= 1")]
    [InlineData("x--\n", "--", "-= 1")]
    [InlineData("[x--]", "--", "-= 1")]
    public void Forbidden_operators_are_rejected_with_a_replacement(string input, string operatorText, string replacement)
    {
        var failure = Lexing.FirstFailure(input);
        Assert.Equal(ErrorCodes.Parse0008, failure.Code);
        Assert.Contains(operatorText, failure.Message, StringComparison.Ordinal);
        Assert.Contains(replacement, failure.Suggestion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("!= 1", "NotEqual int:1")]
    [InlineData("a|>b", "id:a PipeGreater id:b")]
    [InlineData("--x", "Minus Minus id:x")]
    [InlineData("x -= 1", "id:x MinusEqual int:1")]
    [InlineData("x => y", "id:x FatArrow id:y")]
    [InlineData("a => b", "id:a FatArrow id:b")]
    public void Operator_prefixes_that_are_not_forbidden(string input, string expected) => Assert.Equal(expected, Lexing.Render(input));

    [Theory]
    [InlineData("$")]
    [InlineData("@")]
    [InlineData("`")]
    [InlineData("~")]
    [InlineData("^")]
    [InlineData("\\")]
    public void Characters_outside_the_language_are_unexpected(string character)
    {
        var failure = Lexing.FirstFailure("let x = 1 " + character + " 2");
        Assert.Equal(ErrorCodes.Parse0006, failure.Code);
        Assert.Contains(character, failure.Message, StringComparison.Ordinal);
        Assert.Equal((1, 11), failure.Start);
    }

    [Fact]
    public void Control_characters_are_reported_by_code_point()
    {
        var failure = Lexing.FirstFailure("a\u0001");
        Assert.Equal(ErrorCodes.Parse0006, failure.Code);
        Assert.Contains("U+0001", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("café", "cafe", 4)]
    [InlineData("naïve", "naive", 3)]
    [InlineData("Éric", "Eric", 1)]
    [InlineData("x😀y", "x_y", 2)]
    public void Non_ascii_identifier_characters_are_rejected_with_an_ascii_suggestion(string input, string suggested, int column)
    {
        var failure = Lexing.FirstFailure(input);
        Assert.Equal(ErrorCodes.Parse0007, failure.Code);
        Assert.Contains(suggested, failure.Suggestion, StringComparison.Ordinal);
        Assert.Equal((1, column), failure.Start);
    }

    [Fact]
    public void Non_ascii_text_is_fine_in_strings_and_comments()
    {
        Assert.Equal("str[Normal]('é😀') NL id:x", Lexing.Render("\"é😀\" # café 😀\nx"));
    }

    [Fact]
    public void Tokens_expose_blank_adjacency_for_the_minus_ambiguity_rule()
    {
        var tokens = Lexing.Tokens("a -b a-b a\t-b");
        var minuses = tokens.Where(token => token.Kind == TokenKind.Minus).ToList();
        Assert.Equal([true, false, true], minuses.Select(token => token.IsPrecededByBlank));
    }
}

public sealed class LexerStringTests
{
    [Theory]
    [InlineData("\"\"", "str[Normal]()")]
    [InlineData("\"abc\"", "str[Normal]('abc')")]
    [InlineData("\"a{b}c\"", "str[Normal]('a',{id:b},'c')")]
    [InlineData("\"{a}{b}\"", "str[Normal]({id:a},{id:b})")]
    [InlineData("\"{a + 1}\"", "str[Normal]({id:a Plus int:1})")]
    [InlineData("\"a\\{b\\}c\"", "str[Normal]('a{b}c')")]
    [InlineData("\"{\"x{y}\"}\"", "str[Normal]({str[Normal]('x',{id:y})})")]
    [InlineData("\"{ {\"k\": 1}[\"k\"] }\"", "str[Normal]({LeftBrace str[Normal]('k') Colon int:1 RightBrace LeftBracket str[Normal]('k') RightBracket})")]
    [InlineData("\"{\"{\"{a}\"}\"}\"", "str[Normal]({str[Normal]({str[Normal]({id:a})})})")]
    [InlineData("r\"a\\n{b}\"", "str[Raw]('a\\\\n{b}')")]
    [InlineData("r\"\"", "str[Raw]()")]
    [InlineData("r\"\"\"\"\"\"", "str[Raw]()")]
    [InlineData("r\"\"\"a\"b\"\"c\"\"\"", "str[Raw]('a\"b\"\"c')")]
    [InlineData("r\"\"\"a\nb\"\"\"", "str[Raw]('a\\nb')")]
    [InlineData("r\"\"\"a\r\nb\"\"\"", "str[Raw]('a\\nb')")]
    [InlineData("r\"\"\"\n    x\n\"\"\"", "str[Raw]('\\n    x\\n')")]
    [InlineData("p\"\"", "str[Path]()")]
    [InlineData("r \"x\"", "id:r str[Normal]('x')")]
    [InlineData("rr\"x\"", "id:rr str[Normal]('x')")]
    [InlineData("\"{}\"", "str[Normal]({})")]
    public void String_shapes(string input, string expected) => Assert.Equal(expected, Lexing.Render(input));

    [Fact]
    public void Raw_multiline_token_keeps_exact_source_offsets()
    {
        var token = Assert.IsType<StringToken>(Assert.Single(Lexing.Tokens("let x = r\"\"\"a\nb\"\"\"").OfType<StringToken>()));
        Assert.Equal((8, 18), (token.Start, token.End));
        var text = Assert.IsType<TextSegment>(Assert.Single(token.Segments));
        Assert.Equal((12, 15), (text.Start, text.End));
        Assert.Equal("a\nb", text.Text);
    }

    [Fact]
    public void Raw_multiline_crlf_keeps_source_offsets_but_normalizes_the_value()
    {
        var token = Assert.IsType<StringToken>(Assert.Single(Lexing.Tokens("r\"\"\"a\r\nb\"\"\"")));
        Assert.Equal((0, 11), (token.Start, token.End));
        var text = Assert.IsType<TextSegment>(Assert.Single(token.Segments));
        Assert.Equal((4, 8), (text.Start, text.End));
        Assert.Equal("a\nb", text.Text);
    }

    [Fact]
    public void Four_quotes_close_the_raw_multiline_and_leave_a_stray_quote()
    {
        Assert.Equal("str[Raw]('a') str[Normal]()!0016", Lexing.Render("r\"\"\"a\"\"\"\""));
    }

    [Theory]
    [InlineData("r\"\"\"abc", ErrorCodes.Parse0016, 1, 1)]
    [InlineData("let x = r\"\"\"abc\n", ErrorCodes.Parse0016, 1, 9)]
    [InlineData("r\"\"\"a\rb\"\"\"", ErrorCodes.Parse0003, 1, 6)]
    public void Raw_multiline_failures(string input, string code, int line, int column)
    {
        var failure = Lexing.FirstFailure(input);
        Assert.Equal((code, line, column), (failure.Code, failure.Start.Line, failure.Start.Column));
    }

    [Fact]
    public void Simple_escapes_are_decoded()
    {
        var token = Assert.IsType<StringToken>(Assert.Single(Lexing.Tokens("\"\\n\\t\\r\\0\\\\\\\"\\{\\}\"")));
        Assert.Equal("\n\t\r\0\\\"{}", Assert.IsType<TextSegment>(Assert.Single(token.Segments)).Text);
    }

    [Fact]
    public void Unicode_escapes_are_decoded()
    {
        var token = Assert.IsType<StringToken>(Assert.Single(Lexing.Tokens("\"\\u{41}\\u{1F600}\\u{10FFFF}\\u{0}\"")));
        Assert.Equal("A\U0001F600\U0010FFFF\0", Assert.IsType<TextSegment>(Assert.Single(token.Segments)).Text);
    }

    [Fact]
    public void Text_segments_remember_their_source_range()
    {
        var token = Assert.IsType<StringToken>(Assert.Single(Lexing.Tokens("\"a{b}c\\n\"")));
        Assert.Equal([(1, 2), (5, 8)], token.Segments.OfType<TextSegment>().Select(segment => (segment.Start, segment.End)));
        var interpolation = token.Segments.OfType<InterpolationSegment>().Single();
        Assert.Equal((2, 5), (interpolation.Start, interpolation.End));
    }

    [Theory]
    [InlineData("\"abc", ErrorCodes.Parse0016, 1, 1)]
    [InlineData("x = \"abc\ny\"", ErrorCodes.Parse0016, 1, 5)]
    [InlineData("r\"abc", ErrorCodes.Parse0016, 1, 1)]
    [InlineData("p\"abc", ErrorCodes.Parse0016, 1, 1)]
    [InlineData("p\"a{b}\"", ErrorCodes.Parse0053, 1, 4)]
    [InlineData("\"abc\\", ErrorCodes.Parse0016, 1, 1)]
    [InlineData("\"abc\r\n", ErrorCodes.Parse0016, 1, 1)]
    [InlineData("\"a\\qb\"", ErrorCodes.Parse0017, 1, 3)]
    [InlineData("\"\\x41\"", ErrorCodes.Parse0017, 1, 2)]
    [InlineData("\"\\u{110000}\"", ErrorCodes.Parse0018, 1, 2)]
    [InlineData("\"\\u{}\"", ErrorCodes.Parse0018, 1, 2)]
    [InlineData("\"\\u{D800}\"", ErrorCodes.Parse0018, 1, 2)]
    [InlineData("\"\\u{DFFF}\"", ErrorCodes.Parse0018, 1, 2)]
    [InlineData("\"\\u{1234567}\"", ErrorCodes.Parse0018, 1, 2)]
    [InlineData("\"\\u{12\"", ErrorCodes.Parse0018, 1, 2)]
    [InlineData("\"\\u41\"", ErrorCodes.Parse0018, 1, 2)]
    [InlineData("\"a}b\"", ErrorCodes.Parse0021, 1, 3)]
    [InlineData("\"a{x", ErrorCodes.Parse0020, 1, 3)]
    [InlineData("\"a{x\ny}\"", ErrorCodes.Parse0020, 1, 3)]
    [InlineData("\"a{x # c}\"", ErrorCodes.Parse0020, 1, 3)]
    [InlineData("\"a{", ErrorCodes.Parse0020, 1, 3)]
    [InlineData("\"a\rb\"", ErrorCodes.Parse0003, 1, 3)]
    public void String_failures(string input, string code, int line, int column)
    {
        var failure = Lexing.FirstFailure(input);
        Assert.Equal((code, line, column), (failure.Code, failure.Start.Line, failure.Start.Column));
    }

    [Fact]
    public void Unicode_escape_message_quotes_the_offending_text()
    {
        Assert.Contains("\\u{110000}", Lexing.FirstFailure("\"\\u{110000}\"").Message, StringComparison.Ordinal);
        Assert.Contains("q", Lexing.FirstFailure("\"\\q\"").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lexical_error_inside_an_interpolation_ends_the_string_and_is_the_last_inner_token()
    {
        Assert.Equal("str[Normal]('a',{int:1 error:0006})", Lexing.Render("\"a{1 @ 2}b\" tail"));
    }

    [Fact]
    public void Text_before_a_string_failure_is_kept_so_earlier_interpolation_errors_win()
    {
        Assert.Equal("str[Normal]({id:a},{id:b})!0017", Lexing.Render("\"{a}{b}\\q\""));
    }

    [Fact]
    public void After_an_error_the_lexer_only_yields_end_of_file()
    {
        Assert.Equal("id:a error:0006", Lexing.Render("a @ b c d"));
    }

    [Fact]
    public void String_nesting_beyond_the_ast_limit_is_a_depth_error()
    {
        var nested = string.Concat(Enumerable.Repeat("\"{", 120)) + "1" + string.Concat(Enumerable.Repeat("}\"", 120));
        Assert.Equal(ErrorCodes.Parse0005, Lexing.FirstFailure(nested).Code);
    }
}

public sealed class LexerLineTests
{
    [Theory]
    [InlineData("a # c\nb", "id:a NL id:b")]
    [InlineData("a\n\n\n# c\n\nb", "id:a NL id:b")]
    [InlineData("a\n   \n\t\n b", "id:a NL id:b")]
    [InlineData("\n\na", "NL id:a")]
    [InlineData("# only a comment", "")]
    [InlineData("#!/usr/bin/scripts run\nx", "NL id:x")]
    [InlineData("a\n", "id:a NL")]
    [InlineData("a", "id:a")]
    [InlineData("a\r\nb", "id:a NL id:b")]
    [InlineData("a # c\r\n\r\n# d\r\nb", "id:a NL id:b")]
    [InlineData("a\tb", "id:a id:b")]
    public void Runs_of_line_breaks_blank_lines_and_comments_produce_one_new_line_token(string input, string expected) =>
        Assert.Equal(expected, Lexing.Render(input));

    [Fact]
    public void New_line_token_starts_at_the_line_break_even_after_a_trailing_comment()
    {
        var newLine = Lexing.Tokens("a   # note\nb").Single(token => token.Kind == TokenKind.NewLine);
        Assert.Equal(10, newLine.Start);
    }

    [Fact]
    public void Crlf_new_line_starts_at_the_carriage_return_like_lf_does()
    {
        Assert.Equal(1, Lexing.Tokens("a\r\nb").Single(token => token.Kind == TokenKind.NewLine).Start);
        Assert.Equal(1, Lexing.Tokens("a\nb").Single(token => token.Kind == TokenKind.NewLine).Start);
    }

    [Theory]
    [InlineData("a\rb", "id:a error:0003")]
    [InlineData("a # note\rb", "id:a error:0003")]
    [InlineData("a\n\rb", "id:a NL error:0003")]
    public void An_isolated_carriage_return_is_an_error(string input, string expected) => Assert.Equal(expected, Lexing.Render(input));

    [Fact]
    public void Tokens_know_their_offsets_with_tabs_counting_one_column()
    {
        var token = Lexing.Tokens("\t\tfoo").Single();
        Assert.Equal((2, 5), (token.Start, token.End));
        var failure = Lexing.FirstFailure("\t\t@");
        Assert.Equal((1, 3), failure.Start);
    }

    [Fact]
    public void Repeated_identifiers_share_one_canonical_instance()
    {
        // A1: the occurrences of a same name must reference the same instance (lexer name table),
        // so that scope resolution (`Scope.TryGetLocal`, ordinal comparison) takes its fast path.
        var identifiers = Lexing.Tokens("alpha beta alpha beta alpha")
            .Where(token => token.Kind == TokenKind.Identifier)
            .Select(token => token.Text)
            .ToList();
        Assert.Equal(5, identifiers.Count);
        Assert.True(ReferenceEquals(identifiers[0], identifiers[2]));
        Assert.True(ReferenceEquals(identifiers[2], identifiers[4]));
        Assert.False(ReferenceEquals(identifiers[0], identifiers[1]));
    }
}
