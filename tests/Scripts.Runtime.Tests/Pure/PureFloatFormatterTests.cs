using static Scripts.Runtime.Tests.Pure.PureTestSupport;

namespace Scripts.Runtime.Tests.Pure;

public class PureFloatFormatterTests
{
    public static TheoryData<double, string> SpecVectors => new()
    {
        { 0.0, "0.0" },
        { double.NegativeZero, "-0.0" },
        { 0.5, "0.5" },
        { 3.0, "3.0" },
        { -3.0, "-3.0" },
        { 100.0, "100.0" },
        { 1e2, "100.0" },
        { 1e0, "1.0" },
        { 1e15, "1000000000000000.0" },
        { 2.0 * 1e15, "2000000000000000.0" },
        { 9999999999999998.0, "9999999999999998.0" },
        { 1e16, "1.0E+16" },
        { -1e16, "-1.0E+16" },
        { 1e16 + 2.0, "1.0000000000000002E+16" },
        { 1e20, "1.0E+20" },
        { 1e21, "1.0E+21" },
        { 1.2345678901234567e19, "1.2345678901234567E+19" },
        { 1.5e300, "1.5E+300" },
        { 1.0e100, "1.0E+100" },
        { 1.7976931348623157e308, "1.7976931348623157E+308" },
        { 1e-4, "0.0001" },
        { 1e-5, "0.00001" },
        { 1.2e-5, "0.000012" },
        { 0.00001234, "0.00001234" },
        { 1e-6, "1.0E-6" },
        { 9.99e-6, "9.99E-6" },
        { 1e-5 - 1e-20, "9.99999999999999E-6" },
        { 1.5E-7, "1.5E-7" },
        { -1.5e-7, "-1.5E-7" },
        { 1e-5 / 10.0, "1.0000000000000002E-6" },
        { 2.0 / 1e300, "2.0E-300" },
        { 5e-324, "5.0E-324" },
        { 123456789.125, "123456789.125" },
        { 123456.789, "123456.789" },
        { 1234567.0, "1234567.0" },
        { 12345678.9, "12345678.9" },
        { 0.1, "0.1" },
        { 0.1 + 0.2, "0.30000000000000004" },
        { 0.1 * 3.0, "0.30000000000000004" },
        { 0.1 + 0.7, "0.7999999999999999" },
        { 1.0 - 0.9, "0.09999999999999998" },
        { 1.0 / 3.0, "0.3333333333333333" },
        { 2.0 / 3.0, "0.6666666666666666" },
        { 100.0 / 3.0, "33.333333333333336" },
        { 2.5e-3, "0.0025" },
        { 4.35, "4.35" },
        { 3.14159, "3.14159" },
        { 0.000123, "0.000123" },
        { 255.0 * 0.5, "127.5" },
        { 1e300 * 1e-300, "1.0" },
        { 9007199254740993.0, "9007199254740992.0" },
        { (double)9007199254740993L, "9007199254740992.0" },
    };

    [Fact]
    public void SpecVectorsCoverFiftyTwoCases() => Assert.Equal(52, SpecVectors.Count);

    [Fact]
    public void RendersEverySpecVector()
    {
        var mismatches = new List<string>();
        foreach (var row in SpecVectors)
        {
            var (number, expected) = row.Data;
            var rendered = FloatFormatter.Render(number);
            if (rendered != expected || TextRenderer.Render(Float(number)) != expected)
            {
                mismatches.Add($"{number:R} -> {rendered} (expected {expected})");
            }
        }

        Assert.Empty(mismatches);
    }

    [Theory]
    [InlineData(double.MaxValue, "1.7976931348623157E+308")]
    [InlineData(double.MinValue, "-1.7976931348623157E+308")]
    [InlineData(double.Epsilon, "5.0E-324")]
    [InlineData(9999999999999999.0, "1.0E+16")]
    [InlineData(1e-5, "0.00001")]
    [InlineData(-0.00001, "-0.00001")]
    [InlineData(123456789012345.0, "123456789012345.0")]
    public void RendersBoundaries(double number, string expected) => Assert.Equal(expected, FloatFormatter.Render(number));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsNonFiniteNumbers(double number) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FloatFormatter.Render(number));

    [Fact]
    public void RoundTripsRandomDoublesThroughParse()
    {
        var generator = new Random(20260101);
        for (var attempt = 0; attempt < 5000; attempt++)
        {
            var number = BitConverter.Int64BitsToDouble(generator.NextInt64());
            if (!double.IsFinite(number))
            {
                continue;
            }

            var rendered = FloatFormatter.Render(number);
            Assert.Equal(number, double.Parse(rendered, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Contains('.', rendered);
        }
    }
}
