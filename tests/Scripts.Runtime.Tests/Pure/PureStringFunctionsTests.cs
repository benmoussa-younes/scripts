using Scripts.Core;
using static Scripts.Runtime.Tests.Pure.PureTestSupport;

namespace Scripts.Runtime.Tests.Pure;

public class PureStringFunctionsTests
{
    [Theory]
    [InlineData("", 0L)]
    [InlineData("hello", 5L)]
    [InlineData("h\u00E9llo", 5L)]
    [InlineData("\U0001F600", 1L)]
    [InlineData("a\U0001F600b", 3L)]
    [InlineData("\0", 1L)]
    [InlineData("e\u0301", 2L)]
    public void LengthCountsUnicodeScalars(string text, long expected) =>
        Assert.Equal(Integer(expected), Call("string.length", Text(text)));

    // A2: the ASCII fast path must give the same result as counting Unicode scalars.
    [Theory]
    [InlineData("abcdef", 6L)]
    [InlineData("caf\u00E9", 4L)]
    [InlineData("\U0001F600", 1L)]
    [InlineData("", 0L)]
    public void LengthIsStableAcrossAsciiAccentsEmojiAndEmpty(string text, long expected) =>
        Assert.Equal(Integer(expected), Call("string.length", Text(text)));

    [Theory]
    [InlineData("", ",", new[] { "" })]
    [InlineData("a", ",", new[] { "a" })]
    [InlineData("a,b", ",", new[] { "a", "b" })]
    [InlineData(",a", ",", new[] { "", "a" })]
    [InlineData("a,", ",", new[] { "a", "" })]
    [InlineData(",", ",", new[] { "", "" })]
    [InlineData("a,,b", ",", new[] { "a", "", "b" })]
    [InlineData("a--b", "--", new[] { "a", "b" })]
    [InlineData("aaa", "aa", new[] { "", "a" })]
    [InlineData("abc", "abc", new[] { "", "" })]
    [InlineData("a b", " ", new[] { "a", "b" })]
    [InlineData("a,b,,c", ",", new[] { "a", "b", "", "c" })]
    [InlineData("x\U0001F600y\U0001F600z", "\U0001F600", new[] { "x", "y", "z" })]
    [InlineData("A,a", "a", new[] { "A,", "" })]
    public void SplitCutsAtNonOverlappingSeparators(string text, string separator, string[] expected) =>
        Assert.Equal(expected, ToTexts(Call("string.split", Text(text), Text(separator))));

    [Fact]
    public void SplitWithEmptySeparatorIsFatal() =>
        AssertFailure(ErrorCodes.Runtime0008, () => Call("string.split", Text("a,b"), Text(string.Empty)));

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(" a ", "a")]
    [InlineData("\ta\n", "a")]
    [InlineData("a b", "a b")]
    [InlineData(" a  b ", "a  b")]
    [InlineData("\u00A0a\u00A0", "a")]
    [InlineData("\u0085a", "a")]
    [InlineData("\u3000a", "a")]
    [InlineData("\u2028a\u2029", "a")]
    [InlineData("\uFEFFa", "\uFEFFa")]
    [InlineData("\u200Ba", "\u200Ba")]
    [InlineData("  x y \t\n", "x y")]
    public void TrimRemovesWhiteSpaceOnly(string text, string expected) =>
        Assert.Equal(Text(expected), Call("string.trim_whitespace", Text(text)));

    [Theory]
    [InlineData("a-b-c", "-", "+", "a+b+c")]
    [InlineData("aaa", "aa", "b", "ba")]
    [InlineData("abc", "x", "y", "abc")]
    [InlineData("abc", "b", "", "ac")]
    [InlineData("abc", "abc", "", "")]
    [InlineData("aaaa", "a", "aa", "aaaaaaaa")]
    [InlineData("ab", "a", "{0}", "{0}b")]
    [InlineData("\U0001F600x\U0001F600", "\U0001F600", "e", "exe")]
    [InlineData("Aa", "a", "b", "Ab")]
    [InlineData("", "a", "b", "")]
    public void ReplaceSubstitutesAllNonOverlappingOccurrences(string text, string search, string replacement, string expected) =>
        Assert.Equal(Text(expected), Call("string.replace", Text(text), Text(search), Text(replacement)));

    [Fact]
    public void ReplaceWithEmptySearchIsFatal() =>
        AssertFailure(ErrorCodes.Runtime0009, () => Call("string.replace", Text("abc"), Text(string.Empty), Text("x")));

    [Theory]
    [InlineData("abcdef", "cd", true)]
    [InlineData("abc", "", true)]
    [InlineData("abc", "x", false)]
    [InlineData("abc", "ABC", false)]
    [InlineData("", "", true)]
    [InlineData("", "a", false)]
    [InlineData("e\u0301", "\u00E9", false)]
    public void ContainsIsOrdinal(string text, string fragment, bool expected) =>
        Assert.Equal(Boolean(expected), Call("string.contains", Text(text), Text(fragment)));

    [Theory]
    [InlineData("abc", "ab", true)]
    [InlineData("abc", "b", false)]
    [InlineData("abc", "", true)]
    [InlineData("abc", "abcd", false)]
    [InlineData("", "", true)]
    [InlineData("ABC", "ab", false)]
    public void StartsWithIsOrdinal(string text, string prefix, bool expected) =>
        Assert.Equal(Boolean(expected), Call("string.starts_with", Text(text), Text(prefix)));

    [Theory]
    [InlineData("file.txt", ".txt", true)]
    [InlineData("file.txt", "file", false)]
    [InlineData("abc", "", true)]
    [InlineData("abc", "xabc", false)]
    [InlineData("", "", true)]
    [InlineData("ABC", "bc", false)]
    public void EndsWithIsOrdinal(string text, string suffix, bool expected) =>
        Assert.Equal(Boolean(expected), Call("string.ends_with", Text(text), Text(suffix)));

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("a", new[] { "a" })]
    [InlineData("a\n", new[] { "a" })]
    [InlineData("a\nb", new[] { "a", "b" })]
    [InlineData("a\r\nb\r\n", new[] { "a", "b" })]
    [InlineData("\n", new[] { "" })]
    [InlineData("\n\n", new[] { "", "" })]
    [InlineData("a\n\nb", new[] { "a", "", "b" })]
    [InlineData("a\n\n", new[] { "a", "" })]
    [InlineData("a\r\n\r\n", new[] { "a", "" })]
    [InlineData("a\rb", new[] { "a\rb" })]
    [InlineData("a\r", new[] { "a\r" })]
    [InlineData("\r\n", new[] { "" })]
    [InlineData("\r\r\n", new[] { "\r" })]
    [InlineData("a\r\nb\n\nc\n", new[] { "a", "b", "", "c" })]
    [InlineData("\u2028x", new[] { "\u2028x" })]
    public void LinesSplitsOnLineFeedAndCarriageReturnLineFeed(string text, string[] expected) =>
        Assert.Equal(expected, ToTexts(Call("string.lines", Text(text))));

    [Theory]
    [InlineData("42", 42L)]
    [InlineData("-7", -7L)]
    [InlineData("+5", 5L)]
    [InlineData("007", 7L)]
    [InlineData("-0", 0L)]
    [InlineData("+0", 0L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("0000000000000000000000000000000000042", 42L)]
    public void ParseIntegerAcceptsStrictSyntax(string text, long expected) =>
        Assert.Equal(expected, OkInteger(Call("string.parse_integer", Text(text))));

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("+")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1_000")]
    [InlineData("0x10")]
    [InlineData("1.0")]
    [InlineData("\uFF11\uFF12")]
    [InlineData("1\0")]
    [InlineData("++1")]
    [InlineData("-+1")]
    [InlineData("1e3")]
    [InlineData("4x2")]
    [InlineData("1\n")]
    [InlineData("\u0661")]
    public void ParseIntegerRejectsEverythingElse(string text) =>
        AssertValueError(Call("string.parse_integer", Text(text)), "invalid_integer");

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData("999999999999999999999999")]
    public void ParseIntegerOverflowIsOutOfRange(string text) =>
        AssertValueError(Call("string.parse_integer", Text(text)), "out_of_range");

    [Theory]
    [InlineData("3.5", 3.5)]
    [InlineData("3", 3.0)]
    [InlineData("0.0", 0.0)]
    [InlineData("-0.0", -0.0)]
    [InlineData("+2.5", 2.5)]
    [InlineData("1e3", 1000.0)]
    [InlineData("1E+3", 1000.0)]
    [InlineData("2.5e-3", 0.0025)]
    [InlineData("0e0", 0.0)]
    [InlineData("-0", -0.0)]
    [InlineData("1.5E-7", 1.5E-7)]
    [InlineData("4.9e-324", 5.0E-324)]
    [InlineData("0.000", 0.0)]
    [InlineData("0e-999", 0.0)]
    [InlineData("007", 7.0)]
    [InlineData("01.5", 1.5)]
    [InlineData("00.5", 0.5)]
    [InlineData("1e05", 100000.0)]
    [InlineData("1.7976931348623157e308", double.MaxValue)]
    public void ParseFloatAcceptsGrammar(string text, double expected)
    {
        var parsed = OkFloat(Call("string.parse_float", Text(text)));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(parsed));
    }

    [Theory]
    [InlineData("1_")]
    [InlineData("1__0")]
    [InlineData("_1")]
    [InlineData("1._5")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("1e1_0")]
    [InlineData("1_000.5")]
    [InlineData("1_000")]
    [InlineData("1_0_0")]
    [InlineData("1.0_5")]
    [InlineData("+-1")]
    [InlineData("0b1")]
    [InlineData(".5")]
    [InlineData("5.")]
    [InlineData("")]
    [InlineData(" 1.5")]
    [InlineData("1.5 ")]
    [InlineData("1,5")]
    [InlineData("1.5.2")]
    [InlineData("--1")]
    [InlineData("NaN")]
    [InlineData("inf")]
    [InlineData("Infinity")]
    [InlineData("0x10")]
    [InlineData("1e-999")]
    [InlineData("1.5\0")]
    [InlineData("\uFF11")]
    [InlineData("1e0_1")]
    [InlineData("0_1")]
    [InlineData("1.5e")]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("1.e5")]
    [InlineData("1e5.5")]
    public void ParseFloatRejectsEverythingElse(string text) =>
        AssertValueError(Call("string.parse_float", Text(text)), "invalid_float");

    [Theory]
    [InlineData("1e999")]
    [InlineData("-1e999")]
    public void ParseFloatOverflowIsOutOfRange(string text) =>
        AssertValueError(Call("string.parse_float", Text(text)), "out_of_range");

    [Theory]
    [InlineData("h\u00E9llo", "H\u00C9LLO")]
    [InlineData("stra\u00DFe", "STRA\u00DFE")]
    [InlineData("", "")]
    [InlineData("abc123", "ABC123")]
    [InlineData("\U0001F600", "\U0001F600")]
    public void ToUppercaseUsesSimpleInvariantMapping(string text, string expected)
    {
        Assert.Equal(Text(expected), Call("string.uppercased", Text(text)));
        Assert.Equal(Integer(text.EnumerateRunes().Count()), Call("string.length", Call("string.uppercased", Text(text))));
    }

    [Theory]
    [InlineData("H\u00C9LLO", "h\u00E9llo")]
    [InlineData("\u0130", "\u0130")]
    [InlineData("", "")]
    [InlineData("ABC123", "abc123")]
    [InlineData("I", "i")]
    public void ToLowercaseUsesSimpleInvariantMapping(string text, string expected) =>
        Assert.Equal(Text(expected), Call("string.lowercased", Text(text)));

    [Theory]
    [InlineData("abc", "b+", true)]
    [InlineData("abc", "^b", false)]
    [InlineData("abc", "^abc$", true)]
    [InlineData("abc", "", true)]
    [InlineData("", "", true)]
    [InlineData("", "a", false)]
    [InlineData("a\n", "^a$", true)]
    [InlineData("\uFF11\uFF12", "^\\d+$", true)]
    [InlineData("\U0001F600", "^.$", false)]
    [InlineData("\U0001F600", "^..$", true)]
    [InlineData("h\u00E9llo", "^h.llo$", true)]
    [InlineData("ABC", "abc", false)]
    [InlineData("a1", "[a-z][0-9]", true)]
    [InlineData("foo bar", "\\bbar\\b", true)]
    [InlineData("abc", "(a|x)bc", true)]
    [InlineData("a:b", "[:]", true)]
    [InlineData("a:", "[:alpha:]", true)]
    [InlineData("x", "\\[:alpha:\\]", false)]
    public void MatchesFindsPatternAnywhere(string text, string pattern, bool expected) =>
        Assert.Equal(expected, ((BooleanValue)OkPayload(Call("string.matches", Text(text), Text(pattern)))).Flag);

    [Theory]
    [InlineData("(")]
    [InlineData("a)")]
    [InlineData("(a)\\1")]
    [InlineData("(?=a)")]
    [InlineData("(?<=a)b")]
    [InlineData("(?<!a)b")]
    [InlineData("(?>a)")]
    [InlineData("[")]
    [InlineData("*a")]
    [InlineData("a{2,1}")]
    [InlineData("\\")]
    [InlineData("[z-a]")]
    [InlineData("(a{1000}){1000}")]
    [InlineData("[[:alpha:]]")]
    [InlineData("[[:digit:]]+")]
    [InlineData("[^[:space:]]")]
    [InlineData("[a[:alpha:]b]")]
    [InlineData("[]x[:alpha:]]")]
    [InlineData("[[:alpha:][:digit:]]")]
    public void MatchesRejectsInvalidAndUnsupportedPatterns(string pattern) =>
        AssertValueError(Call("string.matches", Text("abc"), Text(pattern)), "invalid_regex");

    [Fact]
    public void MatchesIsLinearOnPathologicalPattern()
    {
        var input = new string('a', 50_000) + "!";
        Assert.False(((BooleanValue)OkPayload(Call("string.matches", Text(input), Text("^(a+)+$")))).Flag);
    }

    [Fact]
    public void Regex_captures_returns_whole_match_then_groups_and_empty_unmatched_groups()
    {
        Assert.Equal(
            ["a1", "a", "1"],
            ToTexts(OkPayload(Call("string.capture_groups", Text("a1b22"), Text("([a-z])(\\d+)")))));
        Assert.Equal(
            ["a", "a", ""],
            ToTexts(OkPayload(Call("string.capture_groups", Text("a"), Text("(a)(b)?")))));
        AssertValueError(Call("string.capture_groups", Text("abc"), Text("\\d+")), "not_found");
        AssertValueError(Call("string.capture_groups", Text("abc"), Text("(")), "invalid_regex");
        AssertValueError(Call("string.capture_groups", Text("abc"), Text("[[:alpha:]]")), "invalid_regex");
    }

    [Fact]
    public void Regex_helpers_never_return_or_construct_a_lone_surrogate()
    {
        // The engine works on UTF-16 code units: selecting half of an emoji must become U+FFFD, not a lone surrogate.
        Assert.Equal("\uFFFD", ToTexts(OkPayload(Call("string.capture_groups", Text("\U0001F600"), Text("."))))[0]);
        AssertValidUtf16(OkText(Call("string.replace_regex", Text("\U0001F600"), Text("."), Text("x"))));
        AssertValidUtf16(OkText(Call("string.replace_regex", Text("\U0001F600"), Text("(.)"), Text("[{0}]"))));
        AssertValidUtf16(OkText(Call("string.pad_start", Text("a"), Integer(3),
            Text(ToTexts(OkPayload(Call("string.capture_groups", Text("\U0001F600"), Text("."))))[0]))));
        foreach (var piece in ToTexts(OkPayload(Call("string.split_regex", Text("a\U0001F600b"), Text(".")))))
        {
            AssertValidUtf16(piece);
        }
    }

    private static void AssertValidUtf16(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                Assert.True(index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]),
                    $"lone high surrogate at {index}");
                index++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(text[index]), $"lone low surrogate at {index}");
            }
        }
    }

    [Fact]
    public void Replace_regex_replaces_every_match_with_bcs_placeholders_and_literal_braces()
    {
        Assert.Equal(
            "a[1]b[22]",
            OkText(Call("string.replace_regex", Text("a1b22"), Text("(\\d+)"), Text("[{1}]"))));
        Assert.Equal(
            "a<b>c",
            OkText(Call("string.replace_regex", Text("abc"), Text("b"), Text("<{0}>"))));
        Assert.Equal(
            "{x}",
            OkText(Call("string.replace_regex", Text("x"), Text("x"), Text("{{x}}"))));
        Assert.Equal(
            "$1",
            OkText(Call("string.replace_regex", Text("x"), Text("x"), Text("$1"))));
        Assert.Equal(
            "[]",
            OkText(Call("string.replace_regex", Text("a"), Text("a(x)?"), Text("[{1}]"))));
        Assert.Equal(
            "a-b-",
            OkText(Call("string.replace_regex", Text("a1b22"), Text("\\d+"), Text("-"))));
        AssertValueError(Call("string.replace_regex", Text("a"), Text("a"), Text("{1}")), "invalid_format");
        AssertValueError(
            Call("string.replace_regex", Text("a"), Text("a"), Text("{999999999999999999999}")),
            "invalid_format");
        AssertValueError(Call("string.replace_regex", Text("a"), Text("("), Text("x")), "invalid_regex");
        // Validation is eager: a non-matching pattern must not hide a bad placeholder.
        AssertValueError(Call("string.replace_regex", Text("abc"), Text("\\d+"), Text("{9}")), "invalid_format");
        AssertValueError(Call("string.replace_regex", Text("abc"), Text("\\d+"), Text("{2}")), "invalid_format");
    }

    [Fact]
    public void Split_regex_splits_on_matches_keeps_empty_pieces_and_ignores_captures()
    {
        Assert.Equal(
            ["a", "b", "c"],
            ToTexts(OkPayload(Call("string.split_regex", Text("a1b22c"), Text("\\d+")))));
        Assert.Equal(
            ["", "a", "", "b", ""],
            ToTexts(OkPayload(Call("string.split_regex", Text("-a--b-"), Text("-")))));
        Assert.Equal(
            ["abc"],
            ToTexts(OkPayload(Call("string.split_regex", Text("abc"), Text("\\d+")))));
        Assert.Equal(
            ["a", "b"],
            ToTexts(OkPayload(Call("string.split_regex", Text("a1b"), Text("(\\d)")))));
        AssertValueError(Call("string.split_regex", Text("abc"), Text("")), "invalid_argument");
        AssertValueError(Call("string.split_regex", Text("abc"), Text("(")), "invalid_regex");
    }

    [Fact]
    public void Pad_start_and_pad_end_count_scalars_and_repeat_truncate_the_padding()
    {
        Assert.Equal("00042", OkText(Call("string.pad_start", Text("42"), Integer(5), Text("0"))));
        Assert.Equal("abc", OkText(Call("string.pad_start", Text("abc"), Integer(2), Text("0"))));
        Assert.Equal("xyxa", OkText(Call("string.pad_start", Text("a"), Integer(4), Text("xy"))));
        Assert.Equal("abxyx", OkText(Call("string.pad_end", Text("ab"), Integer(5), Text("xy"))));
        Assert.Equal("\U0001F600\U0001F600a", OkText(Call("string.pad_start", Text("a"), Integer(3), Text("\U0001F600"))));
        Assert.Equal("abc", OkText(Call("string.pad_start", Text("abc"), Integer(2), Text(""))));
        AssertValueError(Call("string.pad_start", Text("a"), Integer(-1), Text("0")), "out_of_range");
        AssertValueError(Call("string.pad_end", Text("a"), Integer(3), Text("")), "invalid_argument");
        AssertFailure(
            "SCRIPTS-RUNTIME-0013",
            () => Call("string.pad_start", Text("a"), Integer(long.MaxValue), Text("x")));
    }

    [Theory]
    [InlineData("abc", 0L, 3L, "abc")]
    [InlineData("abc", 1L, 2L, "bc")]
    [InlineData("abc", 2L, 1L, "c")]
    [InlineData("\U0001F600x", 0L, 1L, "\U0001F600")]
    [InlineData("abc", 0L, 0L, "")]
    public void SubstringAcceptsWindowsInsideTheText(string text, long start, long count, string expected) =>
        Assert.Equal(expected, OkText(Call("string.substring", Text(text), Integer(start), Integer(count))));

    [Theory]
    [InlineData("abc", 3L, 1L)]
    [InlineData("abc", 1L, 3L)]
    [InlineData("abc", -1L, 1L)]
    public void SubstringRejectsWindowsOutsideTheText(string text, long start, long count) =>
        AssertValueError(Call("string.substring", Text(text), Integer(start), Integer(count)), "out_of_range");

    [Fact]
    public void SubstringWithHugeCountIsOutOfRange() =>
        AssertValueError(Call("string.substring", Text("a"), Integer(1), Integer(long.MaxValue)), "out_of_range");

    [Fact]
    public void Find_all_regex_returns_non_overlapping_matches_and_handles_empty_or_invalid_patterns()
    {
        Assert.Equal(
            ["12", "345"],
            ToTexts(OkPayload(Call("string.find_all_regex", Text("a12b345c"), Text("\\d+")))));
        Assert.Equal(
            [],
            ToTexts(OkPayload(Call("string.find_all_regex", Text("abc"), Text("\\d+")))));
        Assert.Equal(
            ["a", "", ""],
            ToTexts(OkPayload(Call("string.find_all_regex", Text("ab"), Text("a*")))));
        Assert.Equal(
            [""],
            ToTexts(OkPayload(Call("string.find_all_regex", Text(""), Text("")))));
        AssertValueError(Call("string.find_all_regex", Text("abc"), Text("(")), "invalid_regex");
        AssertValueError(Call("string.find_all_regex", Text("abc"), Text("[[:digit:]]")), "invalid_regex");
    }
}
