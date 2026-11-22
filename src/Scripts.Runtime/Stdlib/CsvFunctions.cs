using System.Text;
using static Scripts.Runtime.ArgumentAccess;

namespace Scripts.Runtime;

/// <summary>
/// Intrinsic namespace `csv` (RFC 4180): strict, minimal CSV parsing and encoding over UTF-8 strings.
/// </summary>
internal static class CsvFunctions
{
    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["csv.parse"] = Parse;
        table["csv.parse_row"] = ParseRow;
        table["csv.encode_row"] = EncodeRow;
        table["csv.encode"] = Encode;
    }

    private static Value Parse(RuntimeContext context, Value[] arguments)
    {
        var text = Text(arguments, 0);
        if (text.Length == 0)
        {
            return ResultValue.Ok(new ListValue([]));
        }

        var rows = new List<Value>();
        var position = 0;
        while (position < text.Length)
        {
            if (!TryParseRow(text, ref position, allowRowTerminator: true, out var fields, out var errorMessage))
            {
                return InvalidCsv(errorMessage!);
            }

            SizeLimits.EnsureListLength((long)rows.Count + 1);
            rows.Add(new ListValue(fields!));
        }

        return ResultValue.Ok(new ListValue(rows));
    }

    private static Value ParseRow(RuntimeContext context, Value[] arguments)
    {
        var line = Text(arguments, 0);
        if (line.Length == 0)
        {
            return ResultValue.Ok(new ListValue([]));
        }

        var position = 0;
        if (!TryParseRow(line, ref position, allowRowTerminator: false, out var fields, out var errorMessage))
        {
            return InvalidCsv(errorMessage!);
        }

        return ResultValue.Ok(new ListValue(fields!));
    }

    private static Value EncodeRow(RuntimeContext context, Value[] arguments)
    {
        var fields = List(arguments, 0).Items;
        var builder = new StringBuilder();
        AppendEncodedRow(builder, fields);
        return new StringValue(builder.ToString());
    }

    private static Value Encode(RuntimeContext context, Value[] arguments)
    {
        var rows = List(arguments, 0).Items;
        var builder = new StringBuilder();
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            if (rowIndex > 0)
            {
                SizeLimits.EnsureStringLength((long)builder.Length + 1);
                builder.Append('\n');
            }

            AppendEncodedRow(builder, ((ListValue)rows[rowIndex]).Items);
        }

        return new StringValue(builder.ToString());
    }

    private static void AppendEncodedRow(StringBuilder builder, List<Value> fields)
    {
        for (var index = 0; index < fields.Count; index++)
        {
            if (index > 0)
            {
                SizeLimits.EnsureStringLength((long)builder.Length + 1);
                builder.Append(',');
            }

            var field = ((StringValue)fields[index]).Text;
            // Without the quotes the row is empty text, which `csv.parse` reads as no field (the round trip loses it).
            var needsQuotes = field.IndexOfAny([',', '"', '\n', '\r']) >= 0
                || (field.Length == 0 && fields.Count == 1);
            if (!needsQuotes)
            {
                SizeLimits.EnsureStringLength((long)builder.Length + field.Length);
                builder.Append(field);
                continue;
            }

            var extraQuotes = 0;
            foreach (var ch in field)
            {
                if (ch == '"')
                {
                    extraQuotes++;
                }
            }

            SizeLimits.EnsureStringLength((long)builder.Length + field.Length + extraQuotes + 2);
            builder.Append('"');
            foreach (var ch in field)
            {
                if (ch == '"')
                {
                    builder.Append("\"\"");
                }
                else
                {
                    builder.Append(ch);
                }
            }

            builder.Append('"');
        }
    }

    private static bool TryParseRow(
        string text,
        ref int position,
        bool allowRowTerminator,
        out List<Value>? fields,
        out string? errorMessage)
    {
        fields = [];
        errorMessage = null;

        while (true)
        {
            if (!TryParseField(text, ref position, out var field, out errorMessage))
            {
                fields = null;
                return false;
            }

            SizeLimits.EnsureListLength((long)fields.Count + 1);
            fields.Add(new StringValue(field!));

            if (position >= text.Length)
            {
                return true;
            }

            var next = text[position];
            if (next == ',')
            {
                position++;
                continue;
            }

            if (next == '\n')
            {
                if (!allowRowTerminator)
                {
                    fields = null;
                    errorMessage = "unexpected line break in CSV row";
                    return false;
                }

                position++;
                return true;
            }

            if (next == '\r')
            {
                if (!allowRowTerminator)
                {
                    fields = null;
                    errorMessage = "unexpected line break in CSV row";
                    return false;
                }

                if (position + 1 >= text.Length || text[position + 1] != '\n')
                {
                    fields = null;
                    errorMessage = "unexpected carriage return without line feed in CSV";
                    return false;
                }

                position += 2;
                return true;
            }

            fields = null;
            errorMessage = "unexpected character in CSV";
            return false;
        }
    }

    private static bool TryParseField(string text, ref int position, out string? field, out string? errorMessage)
    {
        errorMessage = null;
        if (position < text.Length && text[position] == '"')
        {
            position++;
            var builder = new StringBuilder();
            while (position < text.Length)
            {
                var ch = text[position++];
                if (ch == '"')
                {
                    if (position < text.Length && text[position] == '"')
                    {
                        builder.Append('"');
                        position++;
                        continue;
                    }

                    if (position < text.Length && text[position] is not (',' or '\n' or '\r'))
                    {
                        field = null;
                        errorMessage = "unexpected character after closing quote in CSV field";
                        return false;
                    }

                    field = builder.ToString();
                    return true;
                }

                builder.Append(ch);
            }

            field = null;
            errorMessage = "unterminated quoted CSV field";
            return false;
        }

        var start = position;
        while (position < text.Length)
        {
            var ch = text[position];
            if (ch is ',' or '\n' or '\r')
            {
                break;
            }

            if (ch == '"')
            {
                field = null;
                errorMessage = "unexpected quote in unquoted CSV field";
                return false;
            }

            position++;
        }

        field = text[start..position];
        return true;
    }

    private static ResultValue InvalidCsv(string message) =>
        ResultValue.Error(ErrorValues.Error("invalid_argument", string.Empty, message));
}
