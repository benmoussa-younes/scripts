using System.Text.Json;
using Scripts.Core.Json;

namespace Scripts.Cli.Tests.Json;

public sealed class JsonWriterTests
{
    private static string WriteSingleString(string value)
    {
        var writer = new JsonWriter();
        writer.WriteString(value);
        return writer.ToString();
    }

    [Fact]
    public void Every_control_character_below_space_is_escaped_as_lowercase_unicode_escape()
    {
        for (var code = 0; code < 0x20; code++)
        {
            var character = (char)code;
            Assert.Equal($"\"\\u{code:x4}\"", WriteSingleString(character.ToString()));
        }
    }

    [Theory]
    [InlineData('\n', "\\u000a")]
    [InlineData('\r', "\\u000d")]
    [InlineData('\t', "\\u0009")]
    [InlineData('\b', "\\u0008")]
    [InlineData('\f', "\\u000c")]
    public void Short_escapes_are_never_used(char character, string expectedEscape)
    {
        Assert.Equal($"\"{expectedEscape}\"", WriteSingleString(character.ToString()));
    }

    [Fact]
    public void Delete_character_and_line_separators_are_escaped()
    {
        Assert.Equal("\"\\u007f\"", WriteSingleString("\u007f"));
        Assert.Equal("\"\\u2028\"", WriteSingleString("\u2028"));
        Assert.Equal("\"\\u2029\"", WriteSingleString("\u2029"));
    }

    [Fact]
    public void Quote_and_backslash_use_the_two_character_escapes_and_slash_is_left_alone()
    {
        Assert.Equal("\"a\\\"b\\\\c/d\"", WriteSingleString("a\"b\\c/d"));
    }

    [Fact]
    public void Non_ascii_and_astral_characters_are_written_unescaped()
    {
        Assert.Equal("\"é☃😀\u0085\u009f\"", WriteSingleString("é☃😀\u0085\u009f"));
    }

    [Fact]
    public void Lone_surrogates_become_replacement_characters()
    {
        (string Input, string ExpectedContent)[] cases =
        [
            ("\ud800", "\ufffd"),
            ("\udc00", "\ufffd"),
            ("a\ud83db", "a\ufffdb"),
            ("a\ude00\ud83d", "a\ufffd\ufffd"),
            ("\ud83d\ud83d\ude00", "\ufffd\ud83d\ude00"),
        ];
        foreach (var (input, expectedContent) in cases)
        {
            Assert.Equal($"\"{expectedContent}\"", WriteSingleString(input));
        }
    }

    [Fact]
    public void Written_strings_round_trip_through_a_standard_parser()
    {
        var allControlCharacters = string.Concat(Enumerable.Range(0, 0x80).Select(code => (char)code)) + "\u2028\u2029é😀\"\\";
        using var document = JsonDocument.Parse(WriteSingleString(allControlCharacters));
        Assert.Equal(allControlCharacters, document.RootElement.GetString());
    }

    [Fact]
    public void Property_names_are_escaped_like_strings()
    {
        var writer = new JsonWriter();
        writer.BeginObject();
        writer.WriteProperty("a\"\n", true);
        writer.EndObject();
        Assert.Equal("{\"a\\\"\\u000a\":true}", writer.ToString());
    }

    [Theory]
    [InlineData(0L, "0")]
    [InlineData(-1L, "-1")]
    [InlineData(long.MaxValue, "9223372036854775807")]
    [InlineData(long.MinValue, "-9223372036854775808")]
    public void Integers_are_written_exactly(long value, string expected)
    {
        var writer = new JsonWriter();
        writer.WriteInteger(value);
        Assert.Equal(expected, writer.ToString());
    }

    [Theory]
    [InlineData("3.0")]
    [InlineData("-0.0")]
    [InlineData("0.5")]
    [InlineData("1.0E+20")]
    [InlineData("1.5E-7")]
    [InlineData("1.7976931348623157E+308")]
    [InlineData("5E-324")]
    [InlineData("12")]
    public void Rendered_float_text_in_the_design_format_is_valid_json(string renderedFloatText)
    {
        var writer = new JsonWriter();
        writer.BeginArray();
        writer.WriteRawFloatText(renderedFloatText);
        writer.EndArray();
        var document = writer.ToString();
        Assert.Equal($"[{renderedFloatText}]", document);
        using var parsed = JsonDocument.Parse(document);
        Assert.Equal(JsonValueKind.Number, parsed.RootElement[0].ValueKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("+1.0")]
    [InlineData(".5")]
    [InlineData("1.")]
    [InlineData("01.5")]
    [InlineData("1e")]
    [InlineData("1.0E+")]
    [InlineData("0x10")]
    [InlineData("1.0 ")]
    public void Invalid_float_text_is_rejected(string invalidText)
    {
        var writer = new JsonWriter();
        Assert.Throws<ArgumentException>(() => writer.WriteRawFloatText(invalidText));
    }

    [Fact]
    public void Booleans_and_null_are_written_as_literals()
    {
        var writer = new JsonWriter();
        writer.BeginArray();
        writer.WriteBoolean(true);
        writer.WriteBoolean(false);
        writer.WriteNull();
        writer.EndArray();
        Assert.Equal("[true,false,null]", writer.ToString());
    }

    [Fact]
    public void Object_keys_keep_the_order_written_by_the_caller_and_nesting_is_compact()
    {
        var writer = new JsonWriter();
        writer.BeginObject();
        writer.WriteProperty("zebra", 1L);
        writer.WriteProperty("alpha", "x");
        writer.WriteName("nested");
        writer.BeginObject();
        writer.WriteName("list");
        writer.BeginArray();
        writer.BeginObject();
        writer.EndObject();
        writer.BeginArray();
        writer.EndArray();
        writer.WriteInteger(7);
        writer.EndArray();
        writer.WriteProperty("missing", (string?)null);
        writer.WriteProperty("count", (long?)null);
        writer.WriteProperty("present", (long?)5);
        writer.EndObject();
        writer.WriteStringArrayProperty("names", ["a", "b"]);
        writer.WriteName("raw");
        writer.WriteRawJson("[1,2]");
        writer.EndObject();
        Assert.Equal(
            "{\"zebra\":1,\"alpha\":\"x\",\"nested\":{\"list\":[{},[],7],\"missing\":null,\"count\":null,\"present\":5},\"names\":[\"a\",\"b\"],\"raw\":[1,2]}",
            writer.ToString());
    }

    [Fact]
    public void Empty_containers_are_written()
    {
        var writer = new JsonWriter();
        writer.BeginObject();
        writer.WriteName("empty_array");
        writer.BeginArray();
        writer.EndArray();
        writer.WriteStringArrayProperty("empty_strings", []);
        writer.EndObject();
        Assert.Equal("{\"empty_array\":[],\"empty_strings\":[]}", writer.ToString());
    }

    [Fact]
    public void Structure_misuse_is_reported()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            var writer = new JsonWriter();
            writer.BeginObject();
            writer.WriteInteger(1);
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            var writer = new JsonWriter();
            writer.BeginArray();
            writer.WriteName("key");
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            var writer = new JsonWriter();
            writer.BeginObject();
            writer.WriteName("first");
            writer.WriteName("second");
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            var writer = new JsonWriter();
            writer.BeginObject();
            writer.WriteName("first");
            writer.EndObject();
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            var writer = new JsonWriter();
            writer.BeginArray();
            writer.EndObject();
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            var writer = new JsonWriter();
            writer.WriteNull();
            writer.WriteNull();
        });
        Assert.Throws<InvalidOperationException>(() => new JsonWriter().ToString());
        Assert.Throws<InvalidOperationException>(() =>
        {
            var writer = new JsonWriter();
            writer.BeginObject();
            _ = writer.ToString();
        });
    }
}
