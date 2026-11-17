using Scripts.Core;
using static Scripts.Runtime.Tests.Pure.PureTestSupport;

namespace Scripts.Runtime.Tests.Pure;

public class PureArithmeticOperationsTests
{
    [Fact]
    public void IntegerOperationsComputeExactResults()
    {
        Assert.Equal(Integer(5), ArithmeticOperations.Add(Integer(2), Integer(3)));
        Assert.Equal(Integer(-1), ArithmeticOperations.Subtract(Integer(2), Integer(3)));
        Assert.Equal(Integer(6), ArithmeticOperations.Multiply(Integer(2), Integer(3)));
        Assert.Equal(Integer(long.MaxValue), ArithmeticOperations.Add(Integer(long.MaxValue - 1), Integer(1)));
        Assert.Equal(Integer(long.MinValue), ArithmeticOperations.Subtract(Integer(long.MinValue + 1), Integer(1)));
        Assert.Equal(Integer(long.MinValue), ArithmeticOperations.Multiply(Integer(long.MinValue), Integer(1)));
    }

    [Theory]
    [InlineData(7L, 2L, 3L, 1L)]
    [InlineData(-7L, 2L, -3L, -1L)]
    [InlineData(7L, -2L, -3L, 1L)]
    [InlineData(-7L, -2L, 3L, -1L)]
    [InlineData(0L, 5L, 0L, 0L)]
    [InlineData(long.MinValue, 1L, long.MinValue, 0L)]
    [InlineData(long.MinValue, 2L, long.MinValue / 2, 0L)]
    public void DivisionTruncatesTowardZeroAndRemainderFollowsDividend(long dividend, long divisor, long quotient, long remainder)
    {
        Assert.Equal(Integer(quotient), ArithmeticOperations.Divide(Integer(dividend), Integer(divisor)));
        Assert.Equal(Integer(remainder), ArithmeticOperations.Remainder(Integer(dividend), Integer(divisor)));
    }

    [Theory]
    [InlineData(long.MaxValue, 1L, "9223372036854775807 + 1")]
    [InlineData(long.MinValue, -1L, "-9223372036854775808 + -1")]
    public void AdditionOverflowIsFatal(long left, long right, string operation)
    {
        var failure = AssertFailure(ErrorCodes.Runtime0001, () => ArithmeticOperations.Add(Integer(left), Integer(right)));
        Assert.Equal(operation, ArgumentValue(failure, "operation"));
    }

    [Fact]
    public void SubtractionAndMultiplicationOverflowAreFatal()
    {
        AssertFailure(ErrorCodes.Runtime0001, () => ArithmeticOperations.Subtract(Integer(long.MinValue), Integer(1)));
        AssertFailure(ErrorCodes.Runtime0001, () => ArithmeticOperations.Subtract(Integer(0), Integer(long.MinValue)));
        AssertFailure(ErrorCodes.Runtime0001, () => ArithmeticOperations.Multiply(Integer(long.MaxValue), Integer(2)));
        AssertFailure(ErrorCodes.Runtime0001, () => ArithmeticOperations.Multiply(Integer(long.MinValue), Integer(-1)));
        AssertFailure(ErrorCodes.Runtime0001, () => ArithmeticOperations.Multiply(Integer(3037000500L), Integer(3037000500L)));
    }

    [Fact]
    public void MinimumValueDividedOrModuloMinusOneOverflows()
    {
        var division = AssertFailure(ErrorCodes.Runtime0001, () => ArithmeticOperations.Divide(Integer(long.MinValue), Integer(-1)));
        Assert.Equal("-9223372036854775808 / -1", ArgumentValue(division, "operation"));
        var remainder = AssertFailure(ErrorCodes.Runtime0001, () => ArithmeticOperations.Remainder(Integer(long.MinValue), Integer(-1)));
        Assert.Equal("-9223372036854775808 % -1", ArgumentValue(remainder, "operation"));
        Assert.Equal(Integer(0), ArithmeticOperations.Remainder(Integer(5), Integer(-1)));
    }

    [Fact]
    public void NegationOfMinimumValueOverflows()
    {
        var failure = AssertFailure(ErrorCodes.Runtime0001, () => ArithmeticOperations.Negate(Integer(long.MinValue)));
        Assert.Equal("-(-9223372036854775808)", ArgumentValue(failure, "operation"));
        Assert.Equal(Integer(-5), ArithmeticOperations.Negate(Integer(5)));
        Assert.Equal(Integer(long.MinValue + 1), ArithmeticOperations.Negate(Integer(long.MaxValue)));
        Assert.Equal(Integer(0), ArithmeticOperations.Negate(Integer(0)));
    }

    [Fact]
    public void IntegerDivisionAndModuloByZeroAreFatal()
    {
        var division = AssertFailure(ErrorCodes.Runtime0002, () => ArithmeticOperations.Divide(Integer(1), Integer(0)));
        Assert.Equal("1 / 0", ArgumentValue(division, "operation"));
        var remainder = AssertFailure(ErrorCodes.Runtime0002, () => ArithmeticOperations.Remainder(Integer(1), Integer(0)));
        Assert.Equal("1 % 0", ArgumentValue(remainder, "operation"));
    }

    [Fact]
    public void FloatOperationsComputeIeeeResults()
    {
        Assert.Equal(Float(0.1 + 0.2), ArithmeticOperations.Add(Float(0.1), Float(0.2)));
        Assert.Equal(Float(0.75), ArithmeticOperations.Subtract(Float(1.0), Float(0.25)));
        Assert.Equal(Float(6.0), ArithmeticOperations.Multiply(Float(2.0), Float(3.0)));
        Assert.Equal(Float(2.5), ArithmeticOperations.Divide(Float(5.0), Float(2.0)));
        Assert.Equal(Float(1.0), ArithmeticOperations.Multiply(Float(1e300), Float(1e-300)));
        Assert.Equal(Float(0.0), ArithmeticOperations.Multiply(Float(1e-300), Float(1e-300)));
        Assert.Equal(Float(1.7976931348623157e308), ArithmeticOperations.Add(Float(1.7976931348623157e308), Float(0.0)));
    }

    [Fact]
    public void FloatNegationPreservesNegativeZero()
    {
        Assert.Equal(Float(-2.5), ArithmeticOperations.Negate(Float(2.5)));
        Assert.True(double.IsNegative(((FloatValue)ArithmeticOperations.Negate(Float(0.0))).Number));
        Assert.False(double.IsNegative(((FloatValue)ArithmeticOperations.Negate(Float(double.NegativeZero))).Number));
    }

    [Fact]
    public void NonFiniteFloatResultsAreFatal()
    {
        var overflow = AssertFailure(ErrorCodes.Runtime0003, () => ArithmeticOperations.Multiply(Float(1e300), Float(1e300)));
        Assert.Equal("overflow", ArgumentValue(overflow, "reason"));
        Assert.Equal("1.0E+300 * 1.0E+300", ArgumentValue(overflow, "operation"));
        AssertFailure(ErrorCodes.Runtime0003, () => ArithmeticOperations.Add(Float(double.MaxValue), Float(double.MaxValue)));
        AssertFailure(ErrorCodes.Runtime0003, () => ArithmeticOperations.Subtract(Float(-double.MaxValue), Float(double.MaxValue)));
        AssertFailure(ErrorCodes.Runtime0003, () => ArithmeticOperations.Divide(Float(1e300), Float(1e-300)));
    }

    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(-1.0, 0.0)]
    [InlineData(5.0, double.NegativeZero)]
    public void FloatDivisionByZeroIsFatal(double dividend, double divisor)
    {
        var failure = AssertFailure(ErrorCodes.Runtime0003, () => ArithmeticOperations.Divide(Float(dividend), Float(divisor)));
        Assert.Equal("division by zero", ArgumentValue(failure, "reason"));
    }

    [Fact]
    public void PlusConcatenatesStringsAndLists()
    {
        Assert.Equal(Text("abcd"), ArithmeticOperations.Add(Text("ab"), Text("cd")));
        Assert.Equal(Text("é\U0001F600"), ArithmeticOperations.Add(Text("é"), Text("\U0001F600")));
        Assert.Equal(IntegerList(1, 2, 3), ArithmeticOperations.Add(IntegerList(1, 2), IntegerList(3)));
        Assert.Equal(List(), ArithmeticOperations.Add(List(), List()));
    }

    [Fact]
    public void ListConcatenationDoesNotAliasNestedLists()
    {
        var left = List(IntegerList(1));
        var right = List(IntegerList(2));
        var joined = (ListValue)ArithmeticOperations.Add(left, right);
        ((ListValue)joined.Items[0]).Items.Add(Integer(99));
        Assert.Equal(List(IntegerList(1)), left);
        Assert.NotSame(left, joined);
    }

    [Fact]
    public void StringConcatenationBeyondLimitIsFatalBeforeAllocation()
    {
        var half = new string('a', (Limits.MaximumCollectionLength / 2) + 1);
        var failure = AssertFailure(ErrorCodes.Runtime0013, () => ArithmeticOperations.Add(Text(half), Text(half)));
        Assert.Equal((half.Length * 2L).ToString(), ArgumentValue(failure, "length"));
    }

    [Theory]
    [InlineData("/tmp", "a", "/tmp/a")]
    [InlineData("/tmp/", "a", "/tmp/a")]
    [InlineData("", "b", "b")]
    [InlineData("/", "x", "/x")]
    [InlineData("a//", "b", "a//b")]
    public void SlashJoinsPathsWithStringOrPath(string basePath, string child, string expected)
    {
        Assert.Equal(Path(expected), ArithmeticOperations.Divide(Path(basePath), Text(child)));
        Assert.Equal(Path(expected), ArithmeticOperations.Divide(Path(basePath), Path(child)));
    }

    [Theory]
    [InlineData("/etc", "/etc")]
    [InlineData("", "\"\"")]
    public void AbsoluteOrEmptyComputedRightOperandIsFatal(string child, string displayed)
    {
        var fromText = AssertFailure(ErrorCodes.Runtime0022, () => ArithmeticOperations.Divide(Path("/srv"), Text(child)));
        Assert.Equal(displayed, ArgumentValue(fromText, "value"));
        var fromPath = AssertFailure(ErrorCodes.Runtime0022, () => ArithmeticOperations.Divide(Path("/srv"), Path(child)));
        Assert.Equal(displayed, ArgumentValue(fromPath, "value"));
    }

    [Fact]
    public void RuntimeGuardDoesNotLeakControlCharactersOrUnpairedSurrogates()
    {
        var failure = AssertFailure(ErrorCodes.Runtime0022, () => ArithmeticOperations.Divide(Path("/srv"), Text("/etc\0\uD800\u2028\u2029")));
        Assert.Equal("/etc\uFFFD\uFFFD\uFFFD\uFFFD", ArgumentValue(failure, "value"));
    }

    [Fact]
    public void RuntimeGuardBoundsAVeryLongOperandButKeepsItsBeginning()
    {
        var segment = "/" + new string('a', 1000);
        var failure = AssertFailure(ErrorCodes.Runtime0022, () => ArithmeticOperations.Divide(Path("/srv"), Text(segment)));
        var displayed = ArgumentValue(failure, "value");
        Assert.StartsWith("/" + new string('a', 255), displayed, StringComparison.Ordinal);
        Assert.EndsWith("…", displayed, StringComparison.Ordinal);
        Assert.True(displayed.Length <= 257, $"displayed length {displayed.Length}");
    }

    [Theory]
    [InlineData("a", "/b", "/b")]
    [InlineData("a", "", "a")]
    [InlineData("", "", "")]
    [InlineData("", "x", "x")]
    public void LexicalPathJoinKeepsItsInternalContract(string basePath, string child, string expected) =>
        Assert.Equal(expected, LexicalPath.Join(basePath, child));

    [Fact]
    public void UnsupportedOperandsAreProgrammingErrors()
    {
        Assert.Throws<ArgumentException>(() => ArithmeticOperations.Add(Integer(1), Float(1.0)));
        Assert.Throws<ArgumentException>(() => ArithmeticOperations.Add(Path("a"), Path("b")));
        Assert.Throws<ArgumentException>(() => ArithmeticOperations.Subtract(Text("a"), Text("b")));
        Assert.Throws<ArgumentException>(() => ArithmeticOperations.Remainder(Float(1.0), Float(1.0)));
        Assert.Throws<ArgumentException>(() => ArithmeticOperations.Negate(Text("a")));
    }
}

public class PureValueComparisonTests
{
    [Theory]
    [InlineData("a", "b", -1)]
    [InlineData("b", "a", 1)]
    [InlineData("a", "a", 0)]
    [InlineData("", "a", -1)]
    [InlineData("B", "a", -1)]
    [InlineData("ab", "a", 1)]
    [InlineData("\uFFFF", "\U00010000", 1)]
    [InlineData("\U00010000", "\uFFFF", -1)]
    [InlineData("é", "z", 1)]
    public void StringsAndPathsCompareByUtf16Units(string left, string right, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(ValueComparison.Compare(Text(left), Text(right))));
        Assert.Equal(expectedSign, Math.Sign(ValueComparison.Compare(Path(left), Path(right))));
    }

    [Fact]
    public void NumbersCompareNumerically()
    {
        Assert.True(ValueComparison.Compare(Integer(-1), Integer(0)) < 0);
        Assert.True(ValueComparison.Compare(Integer(long.MaxValue), Integer(long.MinValue)) > 0);
        Assert.Equal(0, ValueComparison.Compare(Integer(5), Integer(5)));
        Assert.True(ValueComparison.Compare(Float(-1.5), Float(0.5)) < 0);
        Assert.Equal(0, ValueComparison.Compare(Float(0.0), Float(double.NegativeZero)));
    }

    [Fact]
    public void MismatchedOrUnorderedValuesAreProgrammingErrors()
    {
        Assert.Throws<ArgumentException>(() => ValueComparison.Compare(Integer(1), Float(1.0)));
        Assert.Throws<ArgumentException>(() => ValueComparison.Compare(Boolean(true), Boolean(false)));
        Assert.Throws<ArgumentException>(() => ValueComparison.Compare(List(), List()));
    }

    [Fact]
    public void StructuralEqualityCoversEveryComparableType()
    {
        Assert.True(ValueComparison.AreEqual(Text("a"), Text("a")));
        Assert.False(ValueComparison.AreEqual(Text("a"), Path("a")));
        Assert.True(ValueComparison.AreEqual(Float(0.0), Float(double.NegativeZero)));
        Assert.True(ValueComparison.AreEqual(Boolean(true), Boolean(true)));
        Assert.False(ValueComparison.AreEqual(Integer(1), Integer(2)));
        Assert.True(ValueComparison.AreEqual(List(IntegerList(1, 2), List()), List(IntegerList(1, 2), List())));
        Assert.False(ValueComparison.AreEqual(IntegerList(1, 2), IntegerList(2, 1)));
        Assert.False(ValueComparison.AreEqual(IntegerList(1), IntegerList(1, 2)));
    }

    [Fact]
    public void MapEqualityIgnoresInsertionOrder()
    {
        var first = new MapValue();
        first.Set(Text("a"), Integer(1));
        first.Set(Text("b"), Integer(2));
        var second = new MapValue();
        second.Set(Text("b"), Integer(2));
        second.Set(Text("a"), Integer(1));
        Assert.True(ValueComparison.AreEqual(first, second));
        second.Set(Text("a"), Integer(3));
        Assert.False(ValueComparison.AreEqual(first, second));
    }
}

public class PureTextRendererTests
{
    [Fact]
    public void RendersInterpolableTypes()
    {
        Assert.Equal("text é", TextRenderer.Render(Text("text é")));
        Assert.Equal("/a/b", TextRenderer.Render(Path("/a/b")));
        Assert.Equal("-9223372036854775808", TextRenderer.Render(Integer(long.MinValue)));
        Assert.Equal("0", TextRenderer.Render(Integer(0)));
        Assert.Equal("3.0", TextRenderer.Render(Float(3.0)));
        Assert.Equal("1.0E+20", TextRenderer.Render(Float(1e20)));
        Assert.Equal("true", TextRenderer.Render(Boolean(true)));
        Assert.Equal("false", TextRenderer.Render(Boolean(false)));
    }

    [Fact]
    public void RejectsNonInterpolableValues()
    {
        Assert.Throws<ArgumentException>(() => TextRenderer.Render(List()));
        Assert.Throws<ArgumentException>(() => TextRenderer.Render(UnitValue.Instance));
        Assert.Throws<ArgumentException>(() => TextRenderer.Render(new MapValue()));
    }
}

public class PureSizeLimitsTests
{
    [Fact]
    public void AcceptsExactlyTwoPowerTwentyEightUnits()
    {
        SizeLimits.EnsureStringLength(Limits.MaximumCollectionLength);
        SizeLimits.EnsureListLength(Limits.MaximumCollectionLength);
        SizeLimits.EnsureStringLength(0);
    }

    [Fact]
    public void RejectsMoreThanTwoPowerTwentyEight()
    {
        var stringFailure = AssertFailure(ErrorCodes.Runtime0013, () => SizeLimits.EnsureStringLength(Limits.MaximumCollectionLength + 1L));
        Assert.Equal("268435457", ArgumentValue(stringFailure, "length"));
        var listFailure = AssertFailure(ErrorCodes.Runtime0014, () => SizeLimits.EnsureListLength(Limits.MaximumCollectionLength + 1L));
        Assert.Equal("268435457", ArgumentValue(listFailure, "length"));
    }
}

public class PureErrorValuesTests
{
    [Fact]
    public void BuildsRecordsWithCatalogFieldOrder()
    {
        var valueError = ErrorValues.Error("invalid_integer", "", "msg");
        Assert.Equal("error", valueError.TypeName);
        Assert.Equal("invalid_integer", valueError.GetText("kind"));
        Assert.Equal("msg", valueError.GetText("message"));
        Assert.Equal("", valueError.GetText("subject"));

        var environmentError = ErrorValues.EnvironmentFailure("not_found", "NOM", "msg");
        Assert.Equal("NOM", environmentError.GetText("subject"));
        Assert.Equal("not_found", environmentError.GetText("kind"));
        Assert.Equal("msg", environmentError.GetText("message"));

        var ioError = ErrorValues.Error("not_found", "/x", "msg");
        Assert.Equal("/x", ioError.GetText("subject"));
        Assert.Equal("not_found", ioError.GetText("kind"));

        var processError = ErrorValues.Error("timed_out", "sleep", "msg");
        Assert.Equal("sleep", processError.GetText("subject"));
        Assert.Equal("timed_out", processError.GetText("kind"));
    }

    [Fact]
    public void DescribesErrorsLikeUnhandledErrorDiagnostics()
    {
        Assert.Equal("invalid_regex : m", ErrorValues.Describe(ErrorValues.Error("invalid_regex", "", "m")));
        Assert.Equal("other : s : m", ErrorValues.Describe(ErrorValues.Error("other", "s", "m")));
        Assert.Equal("not_found : p : m", ErrorValues.Describe(ErrorValues.Error("not_found", "p", "m")));
        Assert.Equal("not_found : N : m", ErrorValues.Describe(ErrorValues.EnvironmentFailure("not_found", "N", "m")));
        Assert.Equal("text", ErrorValues.Describe(Text("text")));
        Assert.Equal("42", ErrorValues.Describe(Integer(42)));
        Assert.Equal("list", ErrorValues.Describe(List()));
    }
}
