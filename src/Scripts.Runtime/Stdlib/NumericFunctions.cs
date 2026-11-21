using System.Globalization;
using Scripts.Core;
using static Scripts.Runtime.ArgumentAccess;

namespace Scripts.Runtime;

/// <summary>Bitwise, shift, radix and fixed-point formatting methods (DESIGN §3.2).</summary>
internal static class NumericFunctions
{
    private const string RadixDigits = "0123456789abcdefghijklmnopqrstuvwxyz";

    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["integer.bit_and"] = (context, arguments) => IntegerValue.From(Integer(arguments, 0) & Integer(arguments, 1));
        table["integer.bit_or"] = (context, arguments) => IntegerValue.From(Integer(arguments, 0) | Integer(arguments, 1));
        table["integer.bit_xor"] = (context, arguments) => IntegerValue.From(Integer(arguments, 0) ^ Integer(arguments, 1));
        table["integer.bit_not"] = (context, arguments) => IntegerValue.From(~Integer(arguments, 0));
        table["integer.shift_left"] = ShiftLeft;
        table["integer.shift_right"] = ShiftRight;
        table["integer.to_radix"] = ToRadix;
        table["integer.absolute"] = AbsoluteInteger;
        table["integer.power"] = IntegerPower;
        table["integer.clamp"] = IntegerClamp;
        table["integer.minimum"] = (context, arguments) => IntegerValue.From(Math.Min(Integer(arguments, 0), Integer(arguments, 1)));
        table["integer.maximum"] = (context, arguments) => IntegerValue.From(Math.Max(Integer(arguments, 0), Integer(arguments, 1)));
        table["string.parse_integer_radix"] = ParseIntegerRadix;
        table["float.to_fixed"] = ToFixed;
        table["float.absolute"] = AbsoluteFloat;
        table["float.floor"] = (context, arguments) => new FloatValue(NormalizeZero(Math.Floor(Float(arguments, 0))));
        table["float.ceiling"] = (context, arguments) => new FloatValue(NormalizeZero(Math.Ceiling(Float(arguments, 0))));
        table["float.round"] = (context, arguments) => new FloatValue(NormalizeZero(Math.Round(Float(arguments, 0), MidpointRounding.ToEven)));
        table["float.truncate"] = (context, arguments) => new FloatValue(NormalizeZero(Math.Truncate(Float(arguments, 0))));
        table["float.power"] = FloatPower;
        table["float.square_root"] = FloatSquareRoot;
        table["float.natural_log"] = FloatNaturalLog;
        table["float.log2"] = FloatLog2;
        table["float.log10"] = FloatLog10;
        table["float.exponential"] = FloatExponential;
        table["float.clamp"] = FloatClamp;
        table["float.minimum"] = (context, arguments) => new FloatValue(NormalizeZero(Math.Min(Float(arguments, 0), Float(arguments, 1))));
        table["float.maximum"] = (context, arguments) => new FloatValue(NormalizeZero(Math.Max(Float(arguments, 0), Float(arguments, 1))));
    }

    private static Value ShiftLeft(RuntimeContext context, Value[] arguments)
    {
        var value = Integer(arguments, 0);
        var count = Integer(arguments, 1);
        if (count is < 0 or > 63)
        {
            return OutOfRangeShift(count);
        }

        return ResultValue.Ok(IntegerValue.From(unchecked((long)((ulong)value << (int)count))));
    }

    private static Value ShiftRight(RuntimeContext context, Value[] arguments)
    {
        var value = Integer(arguments, 0);
        var count = Integer(arguments, 1);
        if (count is < 0 or > 63)
        {
            return OutOfRangeShift(count);
        }

        return ResultValue.Ok(IntegerValue.From(unchecked(value >> (int)count)));
    }

    private static Value ToRadix(RuntimeContext context, Value[] arguments)
    {
        var value = Integer(arguments, 0);
        var radix = Integer(arguments, 1);
        if (radix is < 2 or > 36)
        {
            return InvalidRadix(radix);
        }

        var magnitude = value < 0 ? unchecked((ulong)(-(value + 1)) + 1UL) : (ulong)value;
        var buffer = new char[65];
        var position = buffer.Length;
        do
        {
            buffer[--position] = RadixDigits[(int)(magnitude % (ulong)radix)];
            magnitude /= (ulong)radix;
        }
        while (magnitude != 0);

        if (value < 0)
        {
            buffer[--position] = '-';
        }

        return ResultValue.Ok(new StringValue(new string(buffer, position, buffer.Length - position)));
    }

    private static Value ParseIntegerRadix(RuntimeContext context, Value[] arguments)
    {
        var text = Text(arguments, 0);
        var radix = Integer(arguments, 1);
        if (radix is < 2 or > 36)
        {
            return InvalidRadix(radix);
        }

        if (text.Length == 0)
        {
            return InvalidInteger(text);
        }

        var position = 0;
        var negative = false;
        if (text[0] is '+' or '-')
        {
            negative = text[0] == '-';
            position = 1;
        }

        if (position == text.Length)
        {
            return InvalidInteger(text);
        }

        var limit = negative ? 9223372036854775808UL : 9223372036854775807UL;
        var magnitude = 0UL;
        for (; position < text.Length; position++)
        {
            var digit = DigitValue(text[position]);
            if (digit < 0 || digit >= radix)
            {
                return InvalidInteger(text);
            }

            var digitValue = (ulong)digit;
            if (magnitude > (limit - digitValue) / (ulong)radix)
            {
                return OutOfRange(text);
            }

            magnitude = (magnitude * (ulong)radix) + digitValue;
        }

        var number = negative ? unchecked((long)(0UL - magnitude)) : (long)magnitude;
        return ResultValue.Ok(IntegerValue.From(number));
    }

    private static Value ToFixed(RuntimeContext context, Value[] arguments)
    {
        var number = Float(arguments, 0);
        var fractionDigits = Integer(arguments, 1);
        if (fractionDigits is < 0 or > 99)
        {
            return ResultValue.Error(ErrorValues.Error(
                "invalid_argument",
                fractionDigits.ToString(CultureInfo.InvariantCulture),
                "the fraction digit count must be in 0..99"));
        }

        var text = number.ToString("F" + fractionDigits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (double.IsNegative(number) && !text.StartsWith('-'))
        {
            text = "-" + text;
        }

        return ResultValue.Ok(new StringValue(text));
    }

    private static Value AbsoluteInteger(RuntimeContext context, Value[] arguments)
    {
        var value = Integer(arguments, 0);
        if (value == long.MinValue)
        {
            return ResultValue.Error(ErrorValues.Error(
                "out_of_range",
                value.ToString(CultureInfo.InvariantCulture),
                "the absolute value of the minimum 64-bit integer is outside the 64-bit range"));
        }

        return ResultValue.Ok(IntegerValue.From(value < 0 ? -value : value));
    }

    private static Value AbsoluteFloat(RuntimeContext context, Value[] arguments) =>
        new FloatValue(NormalizeZero(Math.Abs(Float(arguments, 0))));

    private static Value IntegerPower(RuntimeContext context, Value[] arguments)
    {
        var baseValue = Integer(arguments, 0);
        var exponent = Integer(arguments, 1);
        if (exponent < 0)
        {
            return ResultValue.Error(ErrorValues.Error(
                "invalid_argument",
                exponent.ToString(CultureInfo.InvariantCulture),
                "integer exponent must not be negative"));
        }

        if (exponent == 0 || baseValue == 1)
        {
            return ResultValue.Ok(IntegerValue.From(1));
        }

        if (baseValue == 0)
        {
            return ResultValue.Ok(IntegerValue.From(0));
        }

        if (baseValue == -1)
        {
            return ResultValue.Ok(IntegerValue.From((exponent & 1) == 0 ? 1 : -1));
        }

        try
        {
            checked
            {
                var result = 1L;
                var factor = baseValue;
                var remaining = exponent;
                while (remaining > 0)
                {
                    if ((remaining & 1) != 0)
                    {
                        result *= factor;
                    }

                    remaining >>= 1;
                    if (remaining > 0)
                    {
                        factor *= factor;
                    }
                }

                return ResultValue.Ok(IntegerValue.From(result));
            }
        }
        catch (OverflowException)
        {
            return ResultValue.Error(ErrorValues.Error(
                "out_of_range",
                string.Empty,
                "integer power overflows the 64-bit range"));
        }
    }

    private static Value IntegerClamp(RuntimeContext context, Value[] arguments)
    {
        var value = Integer(arguments, 0);
        var minimum = Integer(arguments, 1);
        var maximum = Integer(arguments, 2);
        if (minimum > maximum)
        {
            return ResultValue.Error(ErrorValues.Error(
                "invalid_argument",
                string.Empty,
                "minimum must be less than or equal to maximum"));
        }

        return ResultValue.Ok(IntegerValue.From(Math.Clamp(value, minimum, maximum)));
    }

    private static Value FloatPower(RuntimeContext context, Value[] arguments) =>
        FiniteFloatResult(Math.Pow(Float(arguments, 0), Float(arguments, 1)), "float power result is not finite");

    private static Value FloatSquareRoot(RuntimeContext context, Value[] arguments)
    {
        var value = Float(arguments, 0);
        if (value < 0.0)
        {
            return FloatOutOfRange("square root requires a non-negative float");
        }

        return FiniteFloatResult(Math.Sqrt(value), "square root result is not finite");
    }

    private static Value FloatNaturalLog(RuntimeContext context, Value[] arguments)
    {
        var value = Float(arguments, 0);
        if (value <= 0.0)
        {
            return FloatOutOfRange("natural logarithm requires a strictly positive float");
        }

        return FiniteFloatResult(Math.Log(value), "natural logarithm result is not finite");
    }

    private static Value FloatLog2(RuntimeContext context, Value[] arguments)
    {
        var value = Float(arguments, 0);
        if (value <= 0.0)
        {
            return FloatOutOfRange("base-2 logarithm requires a strictly positive float");
        }

        return FiniteFloatResult(Math.Log2(value), "base-2 logarithm result is not finite");
    }

    private static Value FloatLog10(RuntimeContext context, Value[] arguments)
    {
        var value = Float(arguments, 0);
        if (value <= 0.0)
        {
            return FloatOutOfRange("base-10 logarithm requires a strictly positive float");
        }

        return FiniteFloatResult(Math.Log10(value), "base-10 logarithm result is not finite");
    }

    private static Value FloatExponential(RuntimeContext context, Value[] arguments) =>
        FiniteFloatResult(Math.Exp(Float(arguments, 0)), "exponential result is not finite");

    private static Value FloatClamp(RuntimeContext context, Value[] arguments)
    {
        var value = Float(arguments, 0);
        var minimum = Float(arguments, 1);
        var maximum = Float(arguments, 2);
        if (minimum > maximum)
        {
            return ResultValue.Error(ErrorValues.Error(
                "invalid_argument",
                string.Empty,
                "minimum must be less than or equal to maximum"));
        }

        return ResultValue.Ok(new FloatValue(NormalizeZero(Math.Clamp(value, minimum, maximum))));
    }

    private static double NormalizeZero(double value) => value == 0.0 ? 0.0 : value;

    private static ResultValue FiniteFloatResult(double value, string message) =>
        double.IsFinite(value)
            ? ResultValue.Ok(new FloatValue(NormalizeZero(value)))
            : FloatOutOfRange(message);

    private static ResultValue FloatOutOfRange(string message) =>
        ResultValue.Error(ErrorValues.Error("out_of_range", string.Empty, message));

    private static int DigitValue(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'a' and <= 'z' => character - 'a' + 10,
        >= 'A' and <= 'Z' => character - 'A' + 10,
        _ => -1,
    };

    private static ResultValue OutOfRangeShift(long count) =>
        ResultValue.Error(ErrorValues.Error(
            "out_of_range",
            count.ToString(CultureInfo.InvariantCulture),
            "the shift count must be in 0..63"));

    private static ResultValue InvalidRadix(long radix) =>
        ResultValue.Error(ErrorValues.Error(
            "invalid_argument",
            radix.ToString(CultureInfo.InvariantCulture),
            "the radix must be in 2..36"));

    private static ResultValue InvalidInteger(string text) =>
        ResultValue.Error(ErrorValues.Error(
            "invalid_integer",
            string.Empty,
            $"invalid integer: \"{text}\""));

    private static ResultValue OutOfRange(string text) =>
        ResultValue.Error(ErrorValues.Error(
            "out_of_range",
            text,
            "the integer is outside the 64-bit range"));
}
