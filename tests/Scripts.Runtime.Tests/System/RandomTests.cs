using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>`random` (DESIGN §1.5): CSPRNG bytes and unbiased integers. No test pins a random value.</summary>
public class RandomTests
{
    [Fact]
    public void Zero_bytes_are_ok_and_empty()
    {
        Assert.Equal(string.Empty, OkText(Call("random.bytes_hex", Integer(0))));
        Assert.Equal(string.Empty, OkText(Call("random.bytes_base64", Integer(0))));
    }

    [Fact]
    public void Hex_has_two_lowercase_hexadecimal_digits_per_byte()
    {
        var value = OkText(Call("random.bytes_hex", Integer(16)));

        Assert.Equal(32, value.Length);
        Assert.All(value, character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    [Fact]
    public void Base64_is_standard_padded_and_decodes_to_the_requested_byte_count()
    {
        var value = OkText(Call("random.bytes_base64", Integer(16)));

        Assert.Equal(24, value.Length);
        Assert.EndsWith("==", value, StringComparison.Ordinal);
        Assert.Equal(16, Convert.FromBase64String(value).Length);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1_048_577)]
    [InlineData(long.MaxValue)]
    public void Byte_count_outside_the_range_is_invalid_argument(long byteCount)
    {
        AssertError(Call("random.bytes_hex", Integer(byteCount)), "invalid_argument");
        AssertError(Call("random.bytes_base64", Integer(byteCount)), "invalid_argument");
    }

    [Fact]
    public void The_maximum_byte_count_is_accepted()
    {
        Assert.Equal(2_097_152, OkText(Call("random.bytes_hex", Integer(1_048_576))).Length);
    }

    [Fact]
    public void Integer_below_one_is_zero() =>
        Assert.Equal(0, OkInteger(Call("random.integer_below", Integer(1))));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Integer_below_rejects_a_bound_below_one(long upperBound) =>
        AssertError(Call("random.integer_below", Integer(upperBound)), "invalid_argument");

    [Fact]
    public void Integer_below_long_max_stays_in_range()
    {
        for (var sample = 0; sample < 1000; sample++)
        {
            var value = OkInteger(Call("random.integer_below", Integer(long.MaxValue)));
            Assert.InRange(value, 0, long.MaxValue - 1);
        }
    }

    [Fact]
    public void A_thousand_samples_stay_in_range_and_are_not_all_equal()
    {
        var values = new HashSet<long>();
        for (var sample = 0; sample < 1000; sample++)
        {
            var value = OkInteger(Call("random.integer_below", Integer(10)));
            Assert.InRange(value, 0, 9);
            values.Add(value);
        }

        Assert.True(values.Count > 1, "one thousand draws from a ten-value range should not be identical");
    }

    private static string OkText(Value result)
    {
        var typed = Assert.IsType<ResultValue>(result);
        Assert.True(typed.IsOk, "ok(...) expected");
        return Assert.IsType<StringValue>(typed.Payload).Text;
    }

    private static long OkInteger(Value result)
    {
        var typed = Assert.IsType<ResultValue>(result);
        Assert.True(typed.IsOk, "ok(...) expected");
        return Assert.IsType<IntegerValue>(typed.Payload).Number;
    }

    private static void AssertError(Value result, string expectedKind)
    {
        var typed = Assert.IsType<ResultValue>(result);
        Assert.False(typed.IsOk);
        Assert.Equal(expectedKind, Assert.IsType<RecordValue>(typed.Payload).GetText("kind"));
    }
}
