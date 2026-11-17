using System.Globalization;
using Scripts.Core;

namespace Scripts.Syntax;

internal sealed partial class Lexer
{
    private Token ReadNumber()
    {
        var start = offset;
        if (text[start] == '0' && start + 1 < text.Length && text[start + 1] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O')
        {
            return ReadPrefixedInteger(start);
        }

        return ReadDecimalNumber(start);
    }

    private Token ReadPrefixedInteger(int start)
    {
        var prefixCharacter = text[start + 1];
        var end = ScanWordCharacters(start + 2);
        offset = end;
        var literal = text.Substring(start, end - start);
        if (char.IsUpper(prefixCharacter))
        {
            return Fail(
                ErrorCodes.Parse0012,
                start,
                end,
                ("prefix", "0" + prefixCharacter),
                ("lowercase_literal", "0" + char.ToLowerInvariant(prefixCharacter) + literal.Substring(2)));
        }

        var radix = prefixCharacter switch { 'x' => 16, 'b' => 2, _ => 8 };
        var digits = literal.Substring(2);
        var problem = DescribeDigitProblem(digits, radix);
        if (problem is not null)
        {
            return Fail(ErrorCodes.Parse0013, start, end, ("literal", Abbreviate(literal)), ("detail", problem));
        }

        var value = 0L;
        foreach (var digit in digits.Where(character => character != '_').Select(DigitValue))
        {
            if (value > (long.MaxValue - digit) / radix)
            {
                return Fail(ErrorCodes.Parse0010, start, end, ("literal", Abbreviate(literal)));
            }

            value = (value * radix) + digit;
        }

        return new IntegerToken(start, end, value);
    }

    private static string? DescribeDigitProblem(string digits, int radix)
    {
        if (digits.Length == 0)
        {
            return "no digit after the prefix";
        }

        if (HasMisplacedUnderscore(digits))
        {
            return "misplaced `_`: it must separate two digits";
        }

        foreach (var character in digits.Where(character => character != '_'))
        {
            if (!Uri.IsHexDigit(character) || DigitValue(character) >= radix)
            {
                return $"invalid digit `{character}` in base {radix}";
            }
        }

        return null;
    }

    private Token ReadDecimalNumber(int start)
    {
        var cursor = ScanDigitsAndUnderscores(start);
        var integerEnd = cursor;
        var fractionStart = -1;
        var fractionEnd = -1;
        if (cursor + 1 < text.Length && text[cursor] == '.' && IsDigit(text[cursor + 1]))
        {
            fractionStart = cursor + 1;
            cursor = ScanDigitsAndUnderscores(fractionStart);
            fractionEnd = cursor;
        }

        var exponentStart = -1;
        var exponentDigitsStart = -1;
        var exponentDigitsEnd = -1;
        if (cursor < text.Length && text[cursor] is 'e' or 'E')
        {
            exponentStart = cursor;
            cursor++;
            if (cursor < text.Length && text[cursor] is '+' or '-')
            {
                cursor++;
            }

            exponentDigitsStart = cursor;
            while (cursor < text.Length && IsDigit(text[cursor]))
            {
                cursor++;
            }

            exponentDigitsEnd = cursor;
        }

        var literalEnd = ScanWordCharacters(cursor);
        offset = literalEnd;
        var literal = text.Substring(start, literalEnd - start);
        var integerPart = text.Substring(start, integerEnd - start);
        var integerDigits = integerPart.Replace("_", string.Empty, StringComparison.Ordinal);
        if (integerDigits.Length > 1 && integerDigits[0] == '0')
        {
            var withoutZeros = integerDigits.TrimStart('0');
            return Fail(
                ErrorCodes.Parse0011,
                start,
                literalEnd,
                ("literal", Abbreviate(integerPart)),
                ("decimal", withoutZeros.Length > 0 ? withoutZeros : "0"),
                ("literal_digits", integerDigits));
        }

        if (HasMisplacedUnderscore(integerPart) || (fractionStart >= 0 && HasMisplacedUnderscore(text.Substring(fractionStart, fractionEnd - fractionStart))))
        {
            return Malformed(start, literalEnd, literal, "misplaced `_`: it must separate two digits");
        }

        if (exponentStart >= 0 && exponentDigitsEnd == exponentDigitsStart)
        {
            return Malformed(start, literalEnd, literal, "exponent without a digit");
        }

        var exponentDigits = exponentStart >= 0 ? text.Substring(exponentDigitsStart, exponentDigitsEnd - exponentDigitsStart) : string.Empty;
        if (exponentDigits.Length > 1 && exponentDigits[0] == '0')
        {
            var corrected = text.Substring(start, exponentDigitsStart - start) + exponentDigits.TrimStart('0');
            return Fail(ErrorCodes.Parse0014, start, literalEnd, ("literal", Abbreviate(literal)), ("corrected", Abbreviate(corrected)));
        }

        if (literalEnd > cursor)
        {
            return Malformed(start, literalEnd, literal, $"unexpected character `{text[cursor]}` after the literal");
        }

        return fractionStart < 0 && exponentStart < 0
            ? CreateIntegerToken(start, literalEnd, literal)
            : CreateFloatToken(start, literalEnd, literal);
    }

    private Token CreateIntegerToken(int start, int end, string literal)
    {
        var cleaned = literal.Replace("_", string.Empty, StringComparison.Ordinal);
        return long.TryParse(cleaned, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? new IntegerToken(start, end, value)
            : Fail(ErrorCodes.Parse0010, start, end, ("literal", Abbreviate(literal)));
    }

    private Token CreateFloatToken(int start, int end, string literal)
    {
        var cleaned = literal.Replace("_", string.Empty, StringComparison.Ordinal);
        var value = double.Parse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture);
        var mantissaHasNonZeroDigit = cleaned.TakeWhile(character => character is not ('e' or 'E')).Any(character => character is >= '1' and <= '9');
        return double.IsInfinity(value) || (value == 0 && mantissaHasNonZeroDigit)
            ? Fail(ErrorCodes.Parse0015, start, end, ("literal", Abbreviate(literal)))
            : new FloatToken(start, end, value);
    }

    private Token Malformed(int start, int end, string literal, string detail) =>
        Fail(ErrorCodes.Parse0013, start, end, ("literal", Abbreviate(literal)), ("detail", detail));

    private int ScanDigitsAndUnderscores(int cursor)
    {
        while (cursor < text.Length && (IsDigit(text[cursor]) || text[cursor] == '_'))
        {
            cursor++;
        }

        return cursor;
    }

    private int ScanWordCharacters(int cursor)
    {
        while (cursor < text.Length && IsIdentifierPart(text[cursor]))
        {
            cursor++;
        }

        return cursor;
    }

    private static int DigitValue(char digit) => digit <= '9' ? digit - '0' : (digit | 0x20) - 'a' + 10;

    private static bool HasMisplacedUnderscore(string digits) =>
        digits[0] == '_' || digits[^1] == '_' || digits.Contains("__", StringComparison.Ordinal);

    private static string Abbreviate(string literal) => literal.Length <= 64 ? literal : literal.Substring(0, 64) + "…";
}
