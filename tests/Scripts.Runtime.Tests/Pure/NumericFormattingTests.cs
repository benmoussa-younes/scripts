using static Scripts.Runtime.Tests.Pure.PureTestSupport;

namespace Scripts.Runtime.Tests.Pure;

/// <summary>`integer.bit_*`, `integer.shift_*`, `integer.to_radix`, `string.parse_integer_radix` and `float.to_fixed` (DESIGN §3.2).</summary>
public class NumericFormattingTests
{
    [Theory]
    [InlineData(493L, 56L, 40L)]
    [InlineData(493L, 18L, 0L)]
    [InlineData(0L, 0L, 0L)]
    [InlineData(-1L, -1L, -1L)]
    [InlineData(long.MinValue, -1L, long.MinValue)]
    [InlineData(long.MinValue, long.MaxValue, 0L)]
    public void Bit_and_is_the_64_bit_two_s_complement_and(long left, long right, long expected) =>
        Assert.Equal(Integer(expected), Call("integer.bit_and", Integer(left), Integer(right)));

    [Theory]
    [InlineData(493L, 18L, 511L)]
    [InlineData(0L, 0L, 0L)]
    [InlineData(-1L, 0L, -1L)]
    [InlineData(long.MinValue, 0L, long.MinValue)]
    [InlineData(493L, 56L, 509L)]
    public void Bit_or_combines_the_64_bit_two_s_complement_images(long left, long right, long expected) =>
        Assert.Equal(Integer(expected), Call("integer.bit_or", Integer(left), Integer(right)));

    [Theory]
    [InlineData(493L, 56L, 469L)]
    [InlineData(-1L, -1L, 0L)]
    [InlineData(long.MinValue, -1L, long.MaxValue)]
    [InlineData(0L, 0L, 0L)]
    public void Bit_xor_toggles_the_differing_bits(long left, long right, long expected) =>
        Assert.Equal(Integer(expected), Call("integer.bit_xor", Integer(left), Integer(right)));

    [Theory]
    [InlineData(0L, -1L)]
    [InlineData(-1L, 0L)]
    [InlineData(1L, -2L)]
    [InlineData(long.MinValue, long.MaxValue)]
    [InlineData(long.MaxValue, long.MinValue)]
    public void Bit_not_is_the_two_s_complement_complement(long value, long expected) =>
        Assert.Equal(Integer(expected), Call("integer.bit_not", Integer(value)));

    [Fact]
    public void Shift_left_matches_the_unsigned_shift_for_every_allowed_count()
    {
        for (var count = 0; count <= 63; count++)
        {
            Assert.Equal(
                unchecked(1L << count),
                OkInteger(Call("integer.shift_left", Integer(1), Integer(count))));
            Assert.Equal(
                unchecked(-1L << count),
                OkInteger(Call("integer.shift_left", Integer(-1), Integer(count))));
        }
    }

    [Theory]
    [InlineData(1L, 4, 16L)]
    [InlineData(1L, 63, long.MinValue)]
    [InlineData(-1L, 63, long.MinValue)]
    [InlineData(0L, 63, 0L)]
    public void Shift_left_wraps_modulo_two_to_the_64(long value, int count, long expected) =>
        Assert.Equal(expected, OkInteger(Call("integer.shift_left", Integer(value), Integer(count))));

    [Theory]
    [InlineData(8L, 1, 4L)]
    [InlineData(-8L, 1, -4L)]
    [InlineData(-1L, 1, -1L)]
    [InlineData(-1L, 63, -1L)]
    [InlineData(long.MinValue, 63, -1L)]
    [InlineData(long.MaxValue, 63, 0L)]
    [InlineData(5L, 0, 5L)]
    public void Shift_right_is_an_arithmetic_shift(long value, int count, long expected) =>
        Assert.Equal(expected, OkInteger(Call("integer.shift_right", Integer(value), Integer(count))));

    [Fact]
    public void Shift_right_matches_the_arithmetic_shift_for_every_allowed_count()
    {
        for (var count = 0; count <= 63; count++)
        {
            Assert.Equal(
                unchecked(-1L >> count),
                OkInteger(Call("integer.shift_right", Integer(-1), Integer(count))));
            Assert.Equal(
                unchecked(long.MinValue >> count),
                OkInteger(Call("integer.shift_right", Integer(long.MinValue), Integer(count))));
        }
    }

    [Theory]
    [InlineData(64)]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void A_shift_count_outside_zero_to_sixty_three_is_out_of_range(long count)
    {
        AssertValueError(Call("integer.shift_left", Integer(1), Integer(count)), "out_of_range");
        AssertValueError(Call("integer.shift_right", Integer(1), Integer(count)), "out_of_range");
    }

    [Theory]
    [InlineData(0L, 2, "0")]
    [InlineData(255L, 2, "11111111")]
    [InlineData(255L, 8, "377")]
    [InlineData(255L, 10, "255")]
    [InlineData(255L, 16, "ff")]
    [InlineData(-1L, 16, "-1")]
    [InlineData(-255L, 16, "-ff")]
    [InlineData(35L, 36, "z")]
    [InlineData(36L, 36, "10")]
    [InlineData(long.MaxValue, 10, "9223372036854775807")]
    [InlineData(long.MinValue, 2, "-1000000000000000000000000000000000000000000000000000000000000000")]
    public void To_radix_uses_lowercase_digits_and_one_leading_minus(long value, int radix, string expected) =>
        Assert.Equal(expected, OkText(Call("integer.to_radix", Integer(value), Integer(radix))));

    [Fact]
    public void To_radix_handles_the_minimum_integer_without_negating_it() =>
        Assert.Equal("-1y2p0ij32e8e8", OkText(Call("integer.to_radix", Integer(long.MinValue), Integer(36))));

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(37)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void A_radix_outside_two_to_thirty_six_is_invalid_argument(long radix)
    {
        AssertValueError(Call("integer.to_radix", Integer(1), Integer(radix)), "invalid_argument");
        AssertValueError(Call("string.parse_integer_radix", Text("1"), Integer(radix)), "invalid_argument");
    }

    [Theory]
    [InlineData("0", 2, 0L)]
    [InlineData("1", 2, 1L)]
    [InlineData("11111111", 2, 255L)]
    [InlineData("377", 8, 255L)]
    [InlineData("255", 10, 255L)]
    [InlineData("ff", 16, 255L)]
    [InlineData("FF", 16, 255L)]
    [InlineData("+ff", 16, 255L)]
    [InlineData("-ff", 16, -255L)]
    [InlineData("z", 36, 35L)]
    [InlineData("Z", 36, 35L)]
    [InlineData("00000000000000000000000042", 10, 42L)]
    [InlineData("-0", 10, 0L)]
    [InlineData("+0", 10, 0L)]
    [InlineData("9223372036854775807", 10, long.MaxValue)]
    [InlineData("-9223372036854775808", 10, long.MinValue)]
    [InlineData("7FFFFFFFFFFFFFFF", 16, long.MaxValue)]
    [InlineData("-8000000000000000", 16, long.MinValue)]
    public void Parse_integer_radix_accepts_the_strict_syntax(string text, int radix, long expected) =>
        Assert.Equal(expected, OkInteger(Call("string.parse_integer_radix", Text(text), Integer(radix))));

    [Theory]
    [InlineData("9223372036854775808", 10)]
    [InlineData("-9223372036854775809", 10)]
    [InlineData("18446744073709551615", 10)]
    [InlineData("ffffffffffffffff", 16)]
    [InlineData("8000000000000000", 16)]
    [InlineData("-8000000000000001", 16)]
    [InlineData("zzzzzzzzzzzzz", 36)]
    public void Parse_integer_radix_reports_overflow_as_out_of_range(string text, int radix) =>
        AssertValueError(Call("string.parse_integer_radix", Text(text), Integer(radix)), "out_of_range");

    [Theory]
    [InlineData("", 10)]
    [InlineData("+", 10)]
    [InlineData("-", 10)]
    [InlineData(" 1", 10)]
    [InlineData("1 ", 10)]
    [InlineData("1\n", 10)]
    [InlineData("\t1", 10)]
    [InlineData("0x10", 16)]
    [InlineData("0b10", 2)]
    [InlineData("0o10", 8)]
    [InlineData("1_0", 10)]
    [InlineData("1.0", 10)]
    [InlineData("1e3", 10)]
    [InlineData("2", 2)]
    [InlineData("g", 16)]
    [InlineData("é", 16)]
    [InlineData("+ 1", 10)]
    public void Parse_integer_radix_reports_bad_digits_and_empty_text_as_invalid_integer(string text, int radix) =>
        AssertValueError(Call("string.parse_integer_radix", Text(text), Integer(radix)), "invalid_integer");

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(255L)]
    [InlineData(-255L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void To_radix_and_parse_integer_radix_round_trip(long value)
    {
        foreach (var radix in new[] { 2, 8, 10, 16, 36 })
        {
            var text = OkText(Call("integer.to_radix", Integer(value), Integer(radix)));
            Assert.Equal(value, OkInteger(Call("string.parse_integer_radix", Text(text), Integer(radix))));
        }
    }

    [Theory]
    [InlineData(0.0, 2, "0.00")]
    [InlineData(1.5, 2, "1.50")]
    [InlineData(-1.5, 2, "-1.50")]
    [InlineData(1.0 / 3.0, 2, "0.33")]
    [InlineData(2.0 / 3.0, 2, "0.67")]
    [InlineData(123.0, 0, "123")]
    [InlineData(1e20, 2, "100000000000000000000.00")]
    [InlineData(1e-10, 2, "0.00")]
    [InlineData(1e-300, 5, "0.00000")]
    [InlineData(1.0, 0, "1")]
    [InlineData(-2.0, 0, "-2")]
    public void To_fixed_uses_invariant_fixed_point_notation(double value, int digits, string expected) =>
        Assert.Equal(expected, OkText(Call("float.to_fixed", Float(value), Integer(digits))));

    [Fact]
    public void To_fixed_never_uses_scientific_notation_for_the_extremes()
    {
        var largest = OkText(Call("float.to_fixed", Float(double.MaxValue), Integer(2)));
        Assert.StartsWith("17976931348623157", largest, StringComparison.Ordinal);
        Assert.DoesNotContain('E', largest);

        var smallest = OkText(Call("float.to_fixed", Float(double.Epsilon), Integer(99)));
        Assert.Equal("0." + new string('0', 99), smallest);
    }

    [Fact]
    public void To_fixed_keeps_the_sign_of_negative_zero()
    {
        Assert.Equal("-0.00", OkText(Call("float.to_fixed", Float(double.NegativeZero), Integer(2))));
        Assert.Equal("-0", OkText(Call("float.to_fixed", Float(double.NegativeZero), Integer(0))));
    }

    [Fact]
    public void To_fixed_renders_exactly_the_requested_number_of_fraction_digits()
    {
        var ninety_nine = OkText(Call("float.to_fixed", Float(1.0), Integer(99)));
        Assert.Equal("1." + new string('0', 99), ninety_nine);

        var zero_digits = OkText(Call("float.to_fixed", Float(1.0), Integer(0)));
        Assert.Equal("1", zero_digits);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void A_fraction_digit_count_outside_zero_to_ninety_nine_is_invalid_argument(long digits) =>
        AssertValueError(Call("float.to_fixed", Float(1.5), Integer(digits)), "invalid_argument");

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(5L, 5L)]
    [InlineData(-5L, 5L)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public void Integer_absolute_removes_the_sign(long value, long expected) =>
        Assert.Equal(expected, OkInteger(Call("integer.absolute", Integer(value))));

    [Fact]
    public void Integer_absolute_of_the_minimum_is_out_of_range()
    {
        AssertValueError(Call("integer.absolute", Integer(long.MinValue)), "out_of_range");
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.5, 1.5)]
    [InlineData(-1.5, 1.5)]
    [InlineData(double.MaxValue, double.MaxValue)]
    [InlineData(-double.MaxValue, double.MaxValue)]
    public void Float_absolute_removes_the_sign_and_normalizes_negative_zero(double value, double expected)
    {
        var result = (FloatValue)Call("float.absolute", Float(value));
        Assert.Equal(expected, result.Number);
        Assert.False(double.IsNegative(result.Number));
    }

    [Fact]
    public void Float_absolute_normalizes_negative_zero()
    {
        var result = (FloatValue)Call("float.absolute", Float(-0.0));
        Assert.Equal(0.0, result.Number);
        Assert.False(double.IsNegative(result.Number));
    }
}
