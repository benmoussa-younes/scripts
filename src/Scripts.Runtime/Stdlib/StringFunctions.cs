using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Scripts.Core;
using static Scripts.Runtime.ArgumentAccess;

namespace Scripts.Runtime;

/// <summary>The string methods (SPEC-STDLIB §7.6). All comparisons are ordinal and culture independent.</summary>
internal static class StringFunctions
{
    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["string.length"] = Length;
        table["string.split"] = Split;
        table["string.trim_whitespace"] = (context, arguments) => new StringValue(Text(arguments, 0).Trim());
        table["string.replace"] = Replace;
        table["string.contains"] = (context, arguments) => BooleanValue.From(Text(arguments, 0).Contains(Text(arguments, 1), StringComparison.Ordinal));
        table["string.starts_with"] = (context, arguments) => BooleanValue.From(Text(arguments, 0).StartsWith(Text(arguments, 1), StringComparison.Ordinal));
        table["string.ends_with"] = (context, arguments) => BooleanValue.From(Text(arguments, 0).EndsWith(Text(arguments, 1), StringComparison.Ordinal));
        table["string.matches"] = Matches;
        table["string.lines"] = Lines;
        table["string.parse_integer"] = ParseInteger;
        table["string.parse_float"] = ParseFloat;
        table["string.uppercased"] = (context, arguments) => new StringValue(Text(arguments, 0).ToUpperInvariant());
        table["string.lowercased"] = (context, arguments) => new StringValue(Text(arguments, 0).ToLowerInvariant());
        table["string.substring"] = Substring;
        table["string.strip_prefix"] = (context, arguments) => new StringValue(RemovePrefix(Text(arguments, 0), Text(arguments, 1)));
        table["string.strip_suffix"] = (context, arguments) => new StringValue(RemoveSuffix(Text(arguments, 0), Text(arguments, 1)));
        table["string.replace_first"] = ReplaceFirst;
        table["string.capture_groups"] = RegexCaptures;
        table["string.replace_regex"] = ReplaceRegex;
        table["string.split_regex"] = SplitRegex;
        table["string.pad_start"] = (context, arguments) => Pad(arguments, atStart: true);
        table["string.pad_end"] = (context, arguments) => Pad(arguments, atStart: false);
        table["string.index_of"] = IndexOf;
        table["string.characters"] = Characters;
        table["string.repeat"] = Repeat;
        table["string.trim_start"] = (context, arguments) => new StringValue(Text(arguments, 0).TrimStart());
        table["string.trim_end"] = (context, arguments) => new StringValue(Text(arguments, 0).TrimEnd());
        table["string.sha256"] = Sha256;
    }

    private static Value Sha256(RuntimeContext context, Value[] arguments)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Text(arguments, 0)));
        return new StringValue(Convert.ToHexStringLower(digest));
    }

    private static Value IndexOf(RuntimeContext context, Value[] arguments)
    {
        var text = Text(arguments, 0);
        var searchText = Text(arguments, 1);
        if (searchText.Length == 0)
        {
            return ResultValue.Error(ErrorValues.Error("invalid_argument", string.Empty, "the search text must not be empty"));
        }

        var position = text.IndexOf(searchText, StringComparison.Ordinal);
        if (position < 0)
        {
            return ResultValue.Error(ErrorValues.Error("not_found", string.Empty, "the search text is absent"));
        }

        var scalarIndex = 0;
        foreach (var _ in text.AsSpan(0, position).EnumerateRunes())
        {
            scalarIndex++;
        }

        return ResultValue.Ok(IntegerValue.From(scalarIndex));
    }

    private static Value Characters(RuntimeContext context, Value[] arguments)
    {
        var items = new List<Value>();
        foreach (var rune in Text(arguments, 0).EnumerateRunes())
        {
            items.Add(new StringValue(rune.ToString()));
        }

        SizeLimits.EnsureListLength(items.Count);
        return new ListValue(items);
    }

    private static Value Repeat(RuntimeContext context, Value[] arguments)
    {
        var text = Text(arguments, 0);
        var count = Integer(arguments, 1);
        if (count < 0)
        {
            return ResultValue.Error(ErrorValues.Error("invalid_argument", count.ToString(CultureInfo.InvariantCulture), "the repeat count must not be negative"));
        }

        if (count == 0 || text.Length == 0)
        {
            return ResultValue.Ok(new StringValue(string.Empty));
        }

        if (count > Limits.MaximumCollectionLength / text.Length)
        {
            return ResultValue.Error(ErrorValues.Error("too_large", count.ToString(CultureInfo.InvariantCulture), "the repeated string is too long"));
        }

        var total = (long)text.Length * count;
        if (total > Limits.MaximumCollectionLength)
        {
            return ResultValue.Error(ErrorValues.Error("too_large", total.ToString(CultureInfo.InvariantCulture), "the repeated string is too long"));
        }

        var builder = new StringBuilder((int)total);
        for (var index = 0; index < count; index++)
        {
            builder.Append(text);
        }

        return ResultValue.Ok(new StringValue(builder.ToString()));
    }

    /// <summary>First match captures: [0] whole match, [1..N] groups; an unmatched group is "".</summary>
    private static Value RegexCaptures(RuntimeContext context, Value[] arguments)
    {
        if (!RegexEngine.TryCompile(Text(arguments, 1), out var regex, out var failureMessage))
        {
            return InvalidRegex(failureMessage);
        }

        var match = regex!.Match(Text(arguments, 0));
        if (!match.Success)
        {
            return ResultValue.Error(ErrorValues.Error("not_found", string.Empty, "no match"));
        }

        var items = new List<Value>(match.Groups.Count);
        foreach (Group group in match.Groups)
        {
            items.Add(new StringValue(group.Success ? Sanitize(group.Value) : string.Empty));
        }

        SizeLimits.EnsureListLength(items.Count);
        return ResultValue.Ok(new ListValue(items));
    }

    /// <summary>
    /// Replaces every non-overlapping match; the replacement uses scripts placeholders ({0}, {1}…, {{ and }} for literal
    /// braces), never .NET <c>$</c> substitution. An unknown/out-of-range index is invalid_format.
    /// </summary>
    private static Value ReplaceRegex(RuntimeContext context, Value[] arguments)
    {
        var text = Text(arguments, 0);
        var replacementText = Text(arguments, 2);
        if (!RegexEngine.TryCompile(Text(arguments, 1), out var regex, out var failureMessage))
        {
            return InvalidRegex(failureMessage);
        }

        if (!ValidateReplacement(replacementText, regex!))
        {
            return ResultValue.Error(ErrorValues.Error(
                "invalid_format", string.Empty, "the replacement has an unknown or out-of-range placeholder"));
        }

        var builder = new StringBuilder(text.Length);
        var position = 0;
        foreach (Match match in regex!.Matches(text))
        {
            AppendChecked(builder, text.AsSpan(position, match.Index - position));
            if (!TryAppendReplacement(builder, replacementText, match))
            {
                return ResultValue.Error(ErrorValues.Error(
                    "invalid_format", string.Empty, "the replacement has an unknown or out-of-range placeholder"));
            }

            position = match.Index + match.Length;
        }

        AppendChecked(builder, text.AsSpan(position));
        SizeLimits.EnsureStringLength(builder.Length);
        return ResultValue.Ok(new StringValue(Sanitize(builder.ToString())));
    }

    /// <summary>Splits at every non-overlapping match; captures are ignored and empty pieces are kept.</summary>
    private static Value SplitRegex(RuntimeContext context, Value[] arguments)
    {
        var text = Text(arguments, 0);
        if (!RegexEngine.TryCompile(Text(arguments, 1), out var regex, out var failureMessage))
        {
            return InvalidRegex(failureMessage);
        }

        var items = new List<Value>();
        var position = 0;
        foreach (Match match in regex!.Matches(text))
        {
            if (match.Length == 0)
            {
                return ResultValue.Error(ErrorValues.Error(
                    "invalid_argument", string.Empty, "the pattern matches an empty string"));
            }

            AddPiece(items, text[position..match.Index]);
            position = match.Index + match.Length;
        }

        AddPiece(items, text[position..]);
        return ResultValue.Ok(new ListValue(items));
    }

    private static void AddPiece(List<Value> items, string piece)
    {
        SizeLimits.EnsureListLength((long)items.Count + 1);
        items.Add(new StringValue(Sanitize(piece)));
    }

    /// <summary>
    /// The regex engine works on UTF-16 code units, so a pattern may select half of a surrogate pair. scripts strings must
    /// contain no isolated surrogate (DESIGN §8.4): replace each unpaired half with U+FFFD.
    /// </summary>
    private static string Sanitize(string text)
    {
        var lone = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    index++;
                }
                else
                {
                    lone = true;
                    break;
                }
            }
            else if (char.IsLowSurrogate(text[index]))
            {
                lone = true;
                break;
            }
        }

        if (!lone)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    builder.Append(character).Append(text[index + 1]);
                    index++;
                }
                else
                {
                    builder.Append('\uFFFD');
                }
            }
            else if (char.IsLowSurrogate(character))
            {
                builder.Append('\uFFFD');
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    /// <summary>Validates every {N} placeholder against the regex groups, even when the pattern matches nothing.</summary>
    private static bool ValidateReplacement(string replacement, Regex regex)
    {
        var groupNumbers = regex.GetGroupNumbers();
        for (var index = 0; index < replacement.Length; index++)
        {
            if (replacement[index] != '{')
            {
                if (replacement[index] == '}' && index + 1 < replacement.Length && replacement[index + 1] == '}')
                {
                    index++;
                }

                continue;
            }

            if (index + 1 < replacement.Length && replacement[index + 1] == '{')
            {
                index++;
                continue;
            }

            var digitStart = index + 1;
            var digitEnd = digitStart;
            while (digitEnd < replacement.Length && char.IsAsciiDigit(replacement[digitEnd]))
            {
                digitEnd++;
            }

            if (digitEnd == digitStart || digitEnd >= replacement.Length || replacement[digitEnd] != '}')
            {
                continue;
            }

            if (!long.TryParse(
                    replacement.AsSpan(digitStart, digitEnd - digitStart),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var groupIndex)
                || groupIndex > int.MaxValue
                || Array.IndexOf(groupNumbers, (int)groupIndex) < 0)
            {
                return false;
            }

            index = digitEnd;
        }

        return true;
    }

    /// <summary>Expands {0}/{N}, {{ and }}; every other character is literal. False = invalid_format.</summary>
    private static bool TryAppendReplacement(StringBuilder builder, string replacement, Match match)
    {
        for (var index = 0; index < replacement.Length; index++)
        {
            var character = replacement[index];
            if (character == '{')
            {
                if (index + 1 < replacement.Length && replacement[index + 1] == '{')
                {
                    AppendChecked(builder, "{");
                    index++;
                    continue;
                }

                var digitStart = index + 1;
                var digitEnd = digitStart;
                while (digitEnd < replacement.Length && char.IsAsciiDigit(replacement[digitEnd]))
                {
                    digitEnd++;
                }

                if (digitEnd > digitStart
                    && digitEnd < replacement.Length
                    && replacement[digitEnd] == '}')
                {
                    if (!long.TryParse(
                            replacement.AsSpan(digitStart, digitEnd - digitStart),
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var groupIndex)
                        || groupIndex >= match.Groups.Count)
                    {
                        return false;
                    }

                    var group = match.Groups[(int)groupIndex];
                    if (group.Success)
                    {
                        AppendChecked(builder, group.Value);
                    }

                    index = digitEnd;
                    continue;
                }

                AppendChecked(builder, "{");
                continue;
            }

            if (character == '}')
            {
                if (index + 1 < replacement.Length && replacement[index + 1] == '}')
                {
                    AppendChecked(builder, "}");
                    index++;
                    continue;
                }

                AppendChecked(builder, "}");
                continue;
            }

            AppendChecked(builder, character);
        }

        return true;
    }

    private static void AppendChecked(StringBuilder builder, ReadOnlySpan<char> text)
    {
        SizeLimits.EnsureStringLength((long)builder.Length + text.Length);
        builder.Append(text);
    }

    private static void AppendChecked(StringBuilder builder, char character)
    {
        SizeLimits.EnsureStringLength((long)builder.Length + 1);
        builder.Append(character);
    }

    /// <summary>pad_start/pad_end: width in Unicode scalars, repeated+truncated padding, 2^28 result cap.</summary>
    private static Value Pad(Value[] arguments, bool atStart)
    {
        var text = Text(arguments, 0);
        var width = Integer(arguments, 1);
        var paddingText = Text(arguments, 2);
        if (width < 0)
        {
            return ResultValue.Error(ErrorValues.Error("out_of_range", string.Empty, $"negative width: {width}"));
        }

        var scalarLength = ScalarCount(text);
        if (scalarLength >= width)
        {
            return ResultValue.Ok(new StringValue(text));
        }

        if (paddingText.Length == 0)
        {
            return ResultValue.Error(ErrorValues.Error(
                "invalid_argument", string.Empty, "the padding text is empty"));
        }

        var missing = width - scalarLength;
        var paddingScalars = ScalarCount(paddingText);
        var fullRepeats = missing / paddingScalars;
        var remainder = (int)(missing % paddingScalars);
        long paddingUnits;
        try
        {
            paddingUnits = checked(fullRepeats * paddingText.Length + PrefixUnits(paddingText, remainder));
        }
        catch (OverflowException)
        {
            throw new RuntimeFailure(ErrorCodes.Runtime0013, ("length", missing.ToString(CultureInfo.InvariantCulture)));
        }

        SizeLimits.EnsureStringLength((long)text.Length + paddingUnits);
        var fill = new StringBuilder((int)paddingUnits);
        for (var repeat = 0L; repeat < fullRepeats; repeat++)
        {
            fill.Append(paddingText);
        }

        var appended = 0;
        foreach (var rune in paddingText.EnumerateRunes())
        {
            if (appended == remainder)
            {
                break;
            }

            fill.Append(rune.ToString());
            appended++;
        }

        var result = atStart ? fill.ToString() + text : text + fill.ToString();
        return ResultValue.Ok(new StringValue(Sanitize(result)));
    }

    private static long ScalarCount(string text) =>
        Ascii.IsValid(text) ? text.Length : text.EnumerateRunes().Count();

    private static int PrefixUnits(string text, int scalarCount)
    {
        if (Ascii.IsValid(text))
        {
            return scalarCount;
        }

        var units = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (scalarCount-- == 0)
            {
                break;
            }

            units += rune.Utf16SequenceLength;
        }

        return units;
    }

    private static Value InvalidRegex(string failureMessage) =>
        ResultValue.Error(ErrorValues.Error("invalid_regex", string.Empty, $"invalid regular expression: {failureMessage}"));

    /// <summary>Substring of Unicode scalars [start_value, start_value + count); out of range on any invalid window.</summary>
    private static Value Substring(RuntimeContext context, Value[] arguments)
    {
        var text = Text(arguments, 0);
        var startValue = Integer(arguments, 1);
        var count = Integer(arguments, 2);

        // Count in Unicode scalars, not UTF-16 code units.
        var scalars = text.EnumerateRunes().ToArray();
        if (startValue < 0 || count < 0 || startValue > scalars.Length || count > scalars.Length - startValue)
        {
            return ResultValue.Error(ErrorValues.Error(
                "out_of_range", "",
                $"invalid window: start {startValue}, length {count}, string of {scalars.Length} characters"));
        }

        var builder = new System.Text.StringBuilder((int)count);
        for (var index = 0; index < count; index++)
        {
            builder.Append(scalars[(int)startValue + index]);
        }

        return ResultValue.Ok(new StringValue(builder.ToString()));
    }

    private static string RemovePrefix(string text, string prefix) =>
        prefix.Length > 0 && text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;

    private static string RemoveSuffix(string text, string suffix) =>
        suffix.Length > 0 && text.EndsWith(suffix, StringComparison.Ordinal) ? text[..^suffix.Length] : text;

    private static Value ReplaceFirst(RuntimeContext context, Value[] arguments)
    {
        var text = Text(arguments, 0);
        var searchText = Text(arguments, 1);
        var replacementText = Text(arguments, 2);
        if (searchText.Length == 0)
        {
            throw new RuntimeFailure(ErrorCodes.Runtime0009);
        }

        var position = text.IndexOf(searchText, StringComparison.Ordinal);
        if (position < 0)
        {
            return new StringValue(text);
        }

        var result = string.Concat(text.AsSpan(0, position), replacementText, text.AsSpan(position + searchText.Length));
        SizeLimits.EnsureStringLength(result.Length);
        return new StringValue(result);
    }

    private static Value Length(RuntimeContext context, Value[] arguments)
    {
        var text = Text(arguments, 0);

        // ASCII fast path (common case: paths, configs, file lines): no surrogate,
        // so one UTF-16 unit is exactly one Unicode scalar. `Ascii.IsValid` is vectorized.
        if (Ascii.IsValid(text))
        {
            return IntegerValue.From(text.Length);
        }

        // Otherwise, exact counting by Unicode scalars.
        var scalarCount = 0L;
        foreach (var scalar in text.EnumerateRunes())
        {
            scalarCount++;
        }

        return IntegerValue.From(scalarCount);
    }

    private static Value Split(RuntimeContext context, Value[] arguments)
    {
        var separator = Text(arguments, 1);
        if (separator.Length == 0)
        {
            throw new RuntimeFailure(ErrorCodes.Runtime0008);
        }

        var parts = Text(arguments, 0).Split(separator, StringSplitOptions.None);
        SizeLimits.EnsureListLength(parts.Length);
        var items = new List<Value>(parts.Length);
        foreach (var part in parts)
        {
            items.Add(new StringValue(part));
        }

        return new ListValue(items);
    }

    private static Value Replace(RuntimeContext context, Value[] arguments)
    {
        var text = Text(arguments, 0);
        var searchText = Text(arguments, 1);
        var replacementText = Text(arguments, 2);
        if (searchText.Length == 0)
        {
            throw new RuntimeFailure(ErrorCodes.Runtime0009);
        }

        if (replacementText.Length > searchText.Length)
        {
            var occurrenceCount = CountOccurrences(text, searchText);
            SizeLimits.EnsureStringLength(text.Length + occurrenceCount * (replacementText.Length - searchText.Length));
        }

        return new StringValue(text.Replace(searchText, replacementText, StringComparison.Ordinal));
    }

    private static long CountOccurrences(string text, string searchText)
    {
        var occurrenceCount = 0L;
        var position = text.IndexOf(searchText, StringComparison.Ordinal);
        while (position >= 0)
        {
            occurrenceCount++;
            position = text.IndexOf(searchText, position + searchText.Length, StringComparison.Ordinal);
        }

        return occurrenceCount;
    }

    private static Value Matches(RuntimeContext context, Value[] arguments)
    {
        if (RegexEngine.TryFind(Text(arguments, 0), Text(arguments, 1), out var found, out var failureMessage))
        {
            return ResultValue.Ok(BooleanValue.From(found));
        }

        return ResultValue.Error(ErrorValues.Error("invalid_regex", "", $"invalid regular expression: {failureMessage}"));
    }

    private static Value Lines(RuntimeContext context, Value[] arguments)
    {
        var text = Text(arguments, 0);
        var items = new List<Value>();
        var lineStart = 0;
        for (var position = 0; position < text.Length; position++)
        {
            if (text[position] != '\n')
            {
                continue;
            }

            var lineEnd = position > lineStart && text[position - 1] == '\r' ? position - 1 : position;
            AddLine(items, text[lineStart..lineEnd]);
            lineStart = position + 1;
        }

        if (lineStart < text.Length)
        {
            AddLine(items, text[lineStart..]);
        }

        return new ListValue(items);
    }

    private static void AddLine(List<Value> items, string line)
    {
        SizeLimits.EnsureListLength((long)items.Count + 1);
        items.Add(new StringValue(line));
    }

    private static Value ParseInteger(RuntimeContext context, Value[] arguments)
    {
        var text = Text(arguments, 0);
        var digitsStart = text.Length > 0 && text[0] is '+' or '-' ? 1 : 0;
        var hasOnlyDigits = text.Length > digitsStart && text.AsSpan(digitsStart).IndexOfAnyExceptInRange('0', '9') < 0;
        if (!hasOnlyDigits)
        {
            return ResultValue.Error(ErrorValues.Error("invalid_integer", "", $"invalid integer: \"{text}\""));
        }

        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            return ResultValue.Error(ErrorValues.Error("out_of_range", "", $"integer outside the 64-bit range: \"{text}\""));
        }

        return ResultValue.Ok(IntegerValue.From(number));
    }

    private static Value ParseFloat(RuntimeContext context, Value[] arguments)
    {
        var text = Text(arguments, 0);
        if (!IsFloatText(text))
        {
            return ResultValue.Error(ErrorValues.Error("invalid_float", "", $"invalid float: \"{text}\""));
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
        {
            return ResultValue.Error(ErrorValues.Error("out_of_range", "", $"float outside the finite range: \"{text}\""));
        }

        if (number == 0.0 && HasNonZeroMantissaDigit(text))
        {
            return ResultValue.Error(ErrorValues.Error("invalid_float", "", $"invalid float: \"{text}\""));
        }

        return ResultValue.Ok(new FloatValue(number));
    }

    private static bool HasNonZeroMantissaDigit(string text)
    {
        var exponentMarker = text.AsSpan().IndexOfAny('e', 'E');
        var mantissa = exponentMarker < 0 ? text.AsSpan() : text.AsSpan(0, exponentMarker);
        return mantissa.IndexOfAnyInRange('1', '9') >= 0;
    }

    // One decimal grammar for parse_integer/parse_float: [+-]? digits (no underscore),
    // optional fractional digits, optional exponent digits. Leading zeros are accepted.
    private static bool IsFloatText(string text)
    {
        var position = text.Length > 0 && text[0] is '+' or '-' ? 1 : 0;
        var digitsStart = position;
        while (position < text.Length && char.IsAsciiDigit(text[position]))
        {
            position++;
        }

        if (position == digitsStart)
        {
            return false;
        }

        if (position < text.Length && text[position] == '.')
        {
            position++;
            var fractionStart = position;
            while (position < text.Length && char.IsAsciiDigit(text[position]))
            {
                position++;
            }

            if (position == fractionStart)
            {
                return false;
            }
        }

        if (position < text.Length && text[position] is 'e' or 'E')
        {
            position++;
            if (position < text.Length && text[position] is '+' or '-')
            {
                position++;
            }

            var exponentStart = position;
            while (position < text.Length && char.IsAsciiDigit(text[position]))
            {
                position++;
            }

            if (position == exponentStart)
            {
                return false;
            }
        }

        return position == text.Length;
    }

}
