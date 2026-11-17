using System.Globalization;
using System.Text;

namespace Scripts.Runtime;

/// <summary>Canonical text of a float (DESIGN 5.5): shortest round-trip digits, decimal notation for 1e-5 &lt;= |x| &lt; 1e16, otherwise d.dddE+xx.</summary>
public static class FloatFormatter
{
    private const int LowestDecimalExponent = -5;
    private const int HighestDecimalExponent = 15;

    public static string Render(double number)
    {
        if (!double.IsFinite(number))
        {
            throw new ArgumentOutOfRangeException(nameof(number), "a scripts float is always finite");
        }

        var roundTripText = number.ToString("R", CultureInfo.InvariantCulture);
        var isNegative = roundTripText[0] == '-';
        var unsignedText = isNegative ? roundTripText[1..] : roundTripText;

        var exponentMarker = unsignedText.IndexOf('E', StringComparison.Ordinal);
        var mantissaText = exponentMarker < 0 ? unsignedText : unsignedText[..exponentMarker];
        var exponent = exponentMarker < 0
            ? 0
            : int.Parse(unsignedText.AsSpan(exponentMarker + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        var decimalPoint = mantissaText.IndexOf('.', StringComparison.Ordinal);
        var integerDigits = decimalPoint < 0 ? mantissaText : mantissaText[..decimalPoint];
        var fractionDigits = decimalPoint < 0 ? string.Empty : mantissaText[(decimalPoint + 1)..];

        // value = 0.DIGITS x 10^pointPosition
        var allDigits = integerDigits + fractionDigits;
        var significantDigits = allDigits.TrimStart('0');
        var pointPosition = integerDigits.Length + exponent - (allDigits.Length - significantDigits.Length);
        significantDigits = significantDigits.TrimEnd('0');

        var builder = new StringBuilder();
        if (isNegative)
        {
            builder.Append('-');
        }

        if (significantDigits.Length == 0)
        {
            return builder.Append("0.0").ToString();
        }

        var scientificExponent = pointPosition - 1;
        if (scientificExponent is >= LowestDecimalExponent and <= HighestDecimalExponent)
        {
            AppendDecimalNotation(builder, significantDigits, pointPosition);
        }
        else
        {
            AppendScientificNotation(builder, significantDigits, scientificExponent);
        }

        return builder.ToString();
    }

    private static void AppendDecimalNotation(StringBuilder builder, string significantDigits, int pointPosition)
    {
        if (pointPosition <= 0)
        {
            builder.Append("0.").Append('0', -pointPosition).Append(significantDigits);
        }
        else if (pointPosition >= significantDigits.Length)
        {
            builder.Append(significantDigits).Append('0', pointPosition - significantDigits.Length).Append(".0");
        }
        else
        {
            builder.Append(significantDigits, 0, pointPosition).Append('.').Append(significantDigits, pointPosition, significantDigits.Length - pointPosition);
        }
    }

    private static void AppendScientificNotation(StringBuilder builder, string significantDigits, int scientificExponent)
    {
        builder.Append(significantDigits[0]).Append('.');
        if (significantDigits.Length > 1)
        {
            builder.Append(significantDigits, 1, significantDigits.Length - 1);
        }
        else
        {
            builder.Append('0');
        }

        builder.Append('E').Append(scientificExponent < 0 ? '-' : '+').Append(Math.Abs(scientificExponent).ToString(CultureInfo.InvariantCulture));
    }
}
