using System.Globalization;
using System.Text;

namespace Scripts.Core.Json;

/// <summary>
/// Hand-written JSON builder (compact, keys in the order the caller writes them). Strings are escaped per DESIGN 8.3:
/// <c>\"</c> and <c>\\</c>, <c>\u00xx</c> (lowercase hex, never the short forms) for U+0000-U+001F and U+007F,
/// <c>\u2028</c>/<c>\u2029</c>, lone surrogates replaced by U+FFFD; every other character is written as is.
/// </summary>
public sealed class JsonWriter
{
    private readonly StringBuilder output = new();
    private readonly Stack<Container> containers = new();
    private bool rootWritten;
    private bool nameWritten;

    public void BeginObject()
    {
        BeginValue();
        output.Append('{');
        containers.Push(new Container(isObject: true));
    }

    public void EndObject()
    {
        if (containers.Count == 0 || !containers.Peek().IsObject || nameWritten)
        {
            throw new InvalidOperationException("EndObject without an open object or after a name without a value.");
        }

        containers.Pop();
        output.Append('}');
    }

    public void BeginArray()
    {
        BeginValue();
        output.Append('[');
        containers.Push(new Container(isObject: false));
    }

    public void EndArray()
    {
        if (containers.Count == 0 || containers.Peek().IsObject)
        {
            throw new InvalidOperationException("EndArray without an open array.");
        }

        containers.Pop();
        output.Append(']');
    }

    public void WriteName(string name)
    {
        if (containers.Count == 0 || !containers.Peek().IsObject || nameWritten)
        {
            throw new InvalidOperationException("A name is only allowed inside an object, before its value.");
        }

        var container = containers.Peek();
        if (container.HasEntries)
        {
            output.Append(',');
        }

        container.HasEntries = true;
        AppendQuoted(name);
        output.Append(':');
        nameWritten = true;
    }

    public void WriteString(string value)
    {
        BeginValue();
        AppendQuoted(value);
    }

    public void WriteInteger(long value)
    {
        BeginValue();
        output.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    public void WriteBoolean(bool value)
    {
        BeginValue();
        output.Append(value ? "true" : "false");
    }

    public void WriteNull()
    {
        BeginValue();
        output.Append("null");
    }

    /// <summary>Writes an already rendered finite float (DESIGN 5.5, e.g. <c>3.0</c>, <c>-0.0</c>, <c>1.0E+20</c>); the text must be a valid JSON number.</summary>
    public void WriteRawFloatText(string renderedFloatText)
    {
        if (!IsValidJsonNumber(renderedFloatText))
        {
            throw new ArgumentException($"invalid float text for JSON: '{renderedFloatText}'", nameof(renderedFloatText));
        }

        BeginValue();
        output.Append(renderedFloatText);
    }

    /// <summary>Inserts an already rendered, well-formed JSON value; the caller guarantees its validity.</summary>
    public void WriteRawJson(string jsonFragment)
    {
        if (jsonFragment.Length == 0)
        {
            throw new ArgumentException("empty JSON fragment.", nameof(jsonFragment));
        }

        BeginValue();
        output.Append(jsonFragment);
    }

    public void WriteProperty(string name, string? value)
    {
        WriteName(name);
        if (value is null)
        {
            WriteNull();
        }
        else
        {
            WriteString(value);
        }
    }

    public void WriteProperty(string name, long value)
    {
        WriteName(name);
        WriteInteger(value);
    }

    public void WriteProperty(string name, long? value)
    {
        WriteName(name);
        if (value is null)
        {
            WriteNull();
        }
        else
        {
            WriteInteger(value.Value);
        }
    }

    public void WriteProperty(string name, bool value)
    {
        WriteName(name);
        WriteBoolean(value);
    }

    public void WriteStringArrayProperty(string name, IEnumerable<string> values)
    {
        WriteName(name);
        BeginArray();
        foreach (var value in values)
        {
            WriteString(value);
        }

        EndArray();
    }

    /// <summary>Returns the finished document; fails if a container is still open or nothing was written.</summary>
    public override string ToString()
    {
        if (containers.Count > 0 || !rootWritten)
        {
            throw new InvalidOperationException("incomplete JSON document.");
        }

        return output.ToString();
    }

    private void BeginValue()
    {
        if (containers.Count == 0)
        {
            if (rootWritten)
            {
                throw new InvalidOperationException("a JSON document has only one root value.");
            }

            rootWritten = true;
            return;
        }

        var container = containers.Peek();
        if (container.IsObject)
        {
            if (!nameWritten)
            {
                throw new InvalidOperationException("An object value MUST be preceded by its name.");
            }

            nameWritten = false;
            return;
        }

        if (container.HasEntries)
        {
            output.Append(',');
        }

        container.HasEntries = true;
    }

    private void AppendQuoted(string text)
    {
        output.Append('"');
        for (var charIndex = 0; charIndex < text.Length; charIndex++)
        {
            var current = text[charIndex];
            if (current == '"')
            {
                output.Append("\\\"");
            }
            else if (current == '\\')
            {
                output.Append("\\\\");
            }
            else if (current < 0x20 || current == 0x7F || current == '\u2028' || current == '\u2029')
            {
                AppendUnicodeEscape(current);
            }
            else if (char.IsHighSurrogate(current) && charIndex + 1 < text.Length && char.IsLowSurrogate(text[charIndex + 1]))
            {
                output.Append(current).Append(text[charIndex + 1]);
                charIndex++;
            }
            else if (char.IsSurrogate(current))
            {
                output.Append('\uFFFD');
            }
            else
            {
                output.Append(current);
            }
        }

        output.Append('"');
    }

    private void AppendUnicodeEscape(char character)
    {
        output.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
    }

    private static bool IsValidJsonNumber(string text)
    {
        var position = 0;
        if (position < text.Length && text[position] == '-')
        {
            position++;
        }

        if (position >= text.Length)
        {
            return false;
        }

        if (text[position] == '0')
        {
            position++;
        }
        else if (text[position] is >= '1' and <= '9')
        {
            SkipDigits(text, ref position);
        }
        else
        {
            return false;
        }

        if (position < text.Length && text[position] == '.')
        {
            position++;
            if (!SkipDigits(text, ref position))
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

            if (!SkipDigits(text, ref position))
            {
                return false;
            }
        }

        return position == text.Length;
    }

    private static bool SkipDigits(string text, ref int position)
    {
        var start = position;
        while (position < text.Length && text[position] is >= '0' and <= '9')
        {
            position++;
        }

        return position > start;
    }

    private sealed class Container(bool isObject)
    {
        public bool IsObject { get; } = isObject;

        public bool HasEntries { get; set; }
    }
}
