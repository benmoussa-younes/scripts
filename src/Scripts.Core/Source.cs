using System.Text;

namespace Scripts.Core;

/// <summary>Byte range in the UTF-16 source text; End is exclusive.</summary>
public readonly record struct Span(int Start, int End)
{
    public int Length => End - Start;

    public static Span Between(Span first, Span last) => new(first.Start, last.End);
}

/// <summary>Source text with 1-based line/column lookup (columns count Unicode code points).</summary>
public sealed class SourceFile
{
    private readonly int[] lineStarts;

    public SourceFile(string path, string text)
    {
        Path = path;
        Text = text;
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }

        lineStarts = starts.ToArray();
    }

    public string Path { get; }

    public string Text { get; }

    public int LineCount => lineStarts.Length;

    public (int Line, int Column) GetPosition(int offset)
    {
        if (offset < 0)
        {
            offset = 0;
        }

        if (offset > Text.Length)
        {
            // S6: an offset one past the end of the text names the single code point that an end-of-file
            // diagnostic covers, so that `end_column` is `column + 1`.
            offset = Text.Length + 1;
        }

        var low = 0;
        var high = lineStarts.Length - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (lineStarts[middle] <= offset)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        var column = 1;
        var limit = Math.Min(offset, Text.Length);
        for (var index = lineStarts[low]; index < limit; index++)
        {
            if (char.IsHighSurrogate(Text[index]) && index + 1 < limit && char.IsLowSurrogate(Text[index + 1]))
            {
                index++;
            }

            column++;
        }

        if (offset > Text.Length)
        {
            column++;
        }

        return (low + 1, column);
    }

    /// <summary>Text of a 1-based line without its terminating newline (and without a trailing carriage return).</summary>
    public string GetLineText(int line)
    {
        if (line < 1 || line > lineStarts.Length)
        {
            return string.Empty;
        }

        var start = lineStarts[line - 1];
        var end = line < lineStarts.Length ? lineStarts[line] - 1 : Text.Length;
        if (end > start && Text[end - 1] == '\r')
        {
            end--;
        }

        return Text.Substring(start, Math.Max(0, end - start));
    }

    public static string DecodeStrictUtf8(byte[] bytes, out bool valid)
    {
        var encoding = new UTF8Encoding(false, true);
        try
        {
            var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            var text = encoding.GetString(bytes, offset, bytes.Length - offset);
            valid = true;
            return text;
        }
        catch (DecoderFallbackException)
        {
            valid = false;
            return string.Empty;
        }
    }
}
