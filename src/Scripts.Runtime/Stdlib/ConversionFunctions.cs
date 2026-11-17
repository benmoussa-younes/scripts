using System.Globalization;
using System.Text;
using Scripts.Core;
using static Scripts.Runtime.ArgumentAccess;

namespace Scripts.Runtime;

/// <summary>format_template, to_float, to_integer, to_path and range (SPEC-STDLIB 7.1, 7.6).</summary>
internal static class ConversionFunctions
{
    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["string.format_template"] = Format;
        table["to_float"] = ToFloat;
        table["to_integer"] = ToInteger;
        table["string.to_path"] = ToPath;
        table["range"] = Range;
    }

    private static Value Format(RuntimeContext context, Value[] arguments)
    {
        var template = Text(arguments, 0);
        var values = List(arguments, 1).Items;
        var builder = new StringBuilder();
        var position = 0;
        while (position < template.Length)
        {
            var character = template[position];
            var isDoubledBrace = character is '{' or '}' && position + 1 < template.Length && template[position + 1] == character;
            if (isDoubledBrace)
            {
                builder.Append(character);
                position += 2;
            }
            else if (character == '{')
            {
                var closingPosition = template.IndexOf('}', position + 1);
                if (closingPosition < 0)
                {
                    return InvalidFormat("opening brace without a closing brace");
                }

                var indexText = template.AsSpan(position + 1, closingPosition - position - 1);
                if (!TryParsePlaceholderIndex(indexText, out var valueIndex))
                {
                    return InvalidFormat("invalid substitution index");
                }

                if (valueIndex >= values.Count)
                {
                    return InvalidFormat("substitution index outside the value list");
                }

                var replacementText = ((StringValue)values[valueIndex]).Text;
                SizeLimits.EnsureStringLength((long)builder.Length + replacementText.Length);
                builder.Append(replacementText);
                position = closingPosition + 1;
            }
            else if (character == '}')
            {
                return InvalidFormat("stray closing brace");
            }
            else
            {
                builder.Append(character);
                position++;
            }

            SizeLimits.EnsureStringLength(builder.Length);
        }

        return ResultValue.Ok(new StringValue(builder.ToString()));
    }

    // Decimal digits without sign or leading zero (except "0"); more than 9 digits can never be a valid index.
    private static bool TryParsePlaceholderIndex(ReadOnlySpan<char> indexText, out int valueIndex)
    {
        valueIndex = 0;
        if (indexText.Length is 0 or > 9 || indexText.Length > 1 && indexText[0] == '0')
        {
            return false;
        }

        foreach (var digit in indexText)
        {
            if (!char.IsAsciiDigit(digit))
            {
                return false;
            }

            valueIndex = valueIndex * 10 + (digit - '0');
        }

        return true;
    }

    private static ResultValue InvalidFormat(string reason) =>
        ResultValue.Error(ErrorValues.Error("invalid_format", "", $"invalid template: {reason}"));

    private static Value ToFloat(RuntimeContext context, Value[] arguments) => new FloatValue(Integer(arguments, 0));

    private static Value ToInteger(RuntimeContext context, Value[] arguments)
    {
        var truncated = Math.Truncate(Float(arguments, 0));
        if (truncated is >= -9223372036854775808.0 and < 9223372036854775808.0)
        {
            return ResultValue.Ok(IntegerValue.From((long)truncated));
        }

        return ResultValue.Error(ErrorValues.Error(
            "out_of_range", "",
            $"float outside the 64-bit integer range: {FloatFormatter.Render(Float(arguments, 0))}"));
    }

    private static Value ToPath(RuntimeContext context, Value[] arguments) => new PathValue(Text(arguments, 0));

    private static Value Range(RuntimeContext context, Value[] arguments)
    {
        var startValue = Integer(arguments, 0);
        var endValue = Integer(arguments, 1);
        if (startValue >= endValue)
        {
            return new ListValue();
        }

        var elementCount = unchecked((ulong)endValue - (ulong)startValue);
        if (elementCount > Limits.MaximumRangeLength)
        {
            throw new RuntimeFailure(ErrorCodes.Runtime0010, ("count", elementCount.ToString(CultureInfo.InvariantCulture)));
        }

        var items = new List<Value>((int)elementCount);
        for (var offset = 0L; offset < (long)elementCount; offset++)
        {
            items.Add(IntegerValue.From(startValue + offset));
        }

        return new ListValue(items);
    }
}
