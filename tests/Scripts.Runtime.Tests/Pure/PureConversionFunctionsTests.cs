using Scripts.Core;
using static Scripts.Runtime.Tests.Pure.PureTestSupport;

namespace Scripts.Runtime.Tests.Pure;

public class PureConversionFunctionsTests
{
    [Theory]
    [InlineData("{0}", new[] { "x" }, "x")]
    [InlineData("{0}{0}", new[] { "ab" }, "abab")]
    [InlineData("{1} {0}", new[] { "a", "b" }, "b a")]
    [InlineData("a", new string[0], "a")]
    [InlineData("", new string[0], "")]
    [InlineData("x", new[] { "unused" }, "x")]
    [InlineData("{{}}", new string[0], "{}")]
    [InlineData("{{0}}", new[] { "x" }, "{0}")]
    [InlineData("{{{0}}}", new[] { "a" }, "{a}")]
    [InlineData("{0}", new[] { "{1}", "z" }, "{1}")]
    [InlineData("{1}-{0}-{{{0}}}", new[] { "a", "b" }, "b-a-{a}")]
    [InlineData("{10}", new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "ten" }, "ten")]
    [InlineData("é{0}\u00e8", new[] { "\U0001F600" }, "é\U0001F600è")]
    public void FormatsValidTemplates(string template, string[] values, string expected) =>
        Assert.Equal(expected, OkText(Call("string.format_template", Text(template), TextList(values))));

    [Theory]
    [InlineData("{0}", new string[0])]
    [InlineData("{1}", new[] { "a" })]
    [InlineData("{99999999999999999999}", new[] { "a" })]
    [InlineData("{", new string[0])]
    [InlineData("}", new string[0])]
    [InlineData("{}", new[] { "a" })]
    [InlineData("{0", new[] { "a" })]
    [InlineData("{00}", new[] { "a" })]
    [InlineData("{ 0 }", new[] { "a" })]
    [InlineData("{-1}", new[] { "a" })]
    [InlineData("{x}", new[] { "a" })]
    [InlineData("{0{1}}", new[] { "a", "b" })]
    [InlineData("{{0}", new[] { "a" })]
    [InlineData("a}b", new[] { "a" })]
    [InlineData("{+0}", new[] { "a" })]
    [InlineData("{\uFF10}", new[] { "a" })]
    public void RejectsInvalidTemplates(string template, string[] values) =>
        AssertValueError(Call("string.format_template", Text(template), TextList(values)), "invalid_format");

    [Fact]
    public void ToFloatConvertsExactlyUpToTwoPowerFiftyThree()
    {
        Assert.Equal(7.0, ((FloatValue)Call("to_float", Integer(7))).Number);
        Assert.Equal(9007199254740992.0, ((FloatValue)Call("to_float", Integer(9007199254740992L))).Number);
        Assert.Equal(9007199254740992.0, ((FloatValue)Call("to_float", Integer(9007199254740993L))).Number);
        Assert.Equal(9007199254740996.0, ((FloatValue)Call("to_float", Integer(9007199254740995L))).Number);
        Assert.Equal(9.223372036854775807e18, ((FloatValue)Call("to_float", Integer(long.MaxValue))).Number);
        Assert.Equal(-9.223372036854775808e18, ((FloatValue)Call("to_float", Integer(long.MinValue))).Number);
        Assert.Equal("9007199254740992.0", TextRenderer.Render(Call("to_float", Integer(9007199254740993L))));
    }

    [Theory]
    [InlineData(3.7, 3L)]
    [InlineData(-3.7, -3L)]
    [InlineData(0.9, 0L)]
    [InlineData(-0.9, 0L)]
    [InlineData(-0.0, 0L)]
    [InlineData(1e15, 1000000000000000L)]
    [InlineData(-9223372036854775808.0, long.MinValue)]
    [InlineData(9223372036854774784.0, 9223372036854774784L)]
    public void ToIntegerTruncatesTowardZero(double number, long expected) =>
        Assert.Equal(expected, OkInteger(Call("to_integer", Float(number))));

    [Theory]
    [InlineData(9223372036854775808.0)]
    [InlineData(1e19)]
    [InlineData(-9223372036854777856.0)]
    [InlineData(-1e300)]
    [InlineData(1.7976931348623157e308)]
    public void ToIntegerRejectsOutOfRange(double number) =>
        AssertValueError(Call("to_integer", Float(number)), "out_of_range");

    [Theory]
    [InlineData("directory//file.txt")]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("a\0b")]
    public void ToPathWrapsTextWithoutChange(string text)
    {
        var converted = Assert.IsType<PathValue>(Call("string.to_path", Text(text)));
        Assert.Equal(text, converted.Text);
        Assert.Equal(text, ((StringValue)Call("path.to_string", converted)).Text);
    }

    [Fact]
    public void RangeBuildsAscendingList()
    {
        Assert.Equal(IntegerList(0, 1, 2), Call("range", Integer(0), Integer(3)));
        Assert.Equal(IntegerList(-2, -1, 0), Call("range", Integer(-2), Integer(1)));
        Assert.Equal(IntegerList(5), Call("range", Integer(5), Integer(6)));
    }

    [Theory]
    [InlineData(3L, 3L)]
    [InlineData(4L, 3L)]
    [InlineData(long.MaxValue, long.MinValue)]
    public void RangeIsEmptyWhenStartIsNotBeforeEnd(long start, long end) =>
        Assert.Empty(((ListValue)Call("range", Integer(start), Integer(end))).Items);

    [Fact]
    public void RangeAcceptsExactlyTenMillionElements()
    {
        var list = (ListValue)Call("range", Integer(0), Integer(10_000_000));
        Assert.Equal(10_000_000, list.Items.Count);
        Assert.Equal(9_999_999L, ((IntegerValue)list.Items[^1]).Number);
    }

    [Theory]
    [InlineData(0L, 10_000_001L, "10000001")]
    [InlineData(long.MinValue, long.MaxValue, "18446744073709551615")]
    [InlineData(-5_000_000L, 5_000_001L, "10000001")]
    public void RangeRejectsMoreThanTenMillionElements(long start, long end, string expectedCount)
    {
        var failure = AssertFailure(ErrorCodes.Runtime0010, () => Call("range", Integer(start), Integer(end)));
        Assert.Equal(expectedCount, ArgumentValue(failure, "count"));
    }
}
