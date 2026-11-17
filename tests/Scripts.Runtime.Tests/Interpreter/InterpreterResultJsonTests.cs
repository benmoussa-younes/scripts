using System.Text.Json;
using Scripts.Runtime.Interpreter;

namespace Scripts.Runtime.Tests.InterpreterRuns;

/// <summary>--json-out rendering of the top-level return value for every value shape (DESIGN 8.4).</summary>
public sealed class InterpreterResultJsonTests
{
    private static JsonDocument Parse(Value value) => JsonDocument.Parse(ResultJson.Render(value));

    private static ListValue ListOf(params Value[] items) => new([.. items]);

    private static MapValue MapOf(params (Value Key, Value Value)[] entries)
    {
        var map = new MapValue();
        foreach (var (key, value) in entries)
        {
            map.Set(key, value);
        }

        return map;
    }

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("", "\"\"")]
    [InlineData("quote\"back\\slash", "\"quote\\\"back\\\\slash\"")]
    [InlineData("line\nbreak\ttab", "\"line\\u000abreak\\u0009tab\"")]
    [InlineData("é😀", "\"é😀\"")]
    public void StringAndPathRenderAsJsonStrings(string text, string expectedJson)
    {
        Assert.Equal(expectedJson, ResultJson.Render(new StringValue(text)));
        Assert.Equal(expectedJson, ResultJson.Render(new PathValue(text)));
        using var document = Parse(new StringValue(text));
        Assert.Equal(text, document.RootElement.GetString());
    }

    [Fact]
    public void ControlAndSeparatorCharactersRoundTrip()
    {
        const string text = "\u0001\u007f\u2028\u2029end";

        using var document = Parse(new StringValue(text));

        Assert.Equal(text, document.RootElement.GetString());
        Assert.DoesNotContain('\u2028', ResultJson.Render(new StringValue(text)));
    }

    [Theory]
    [InlineData(0L, "0")]
    [InlineData(42L, "42")]
    [InlineData(-7L, "-7")]
    [InlineData(long.MaxValue, "9223372036854775807")]
    [InlineData(long.MinValue, "-9223372036854775808")]
    public void IntegersAreExact(long number, string expectedJson)
    {
        Assert.Equal(expectedJson, ResultJson.Render(new IntegerValue(number)));
        using var document = Parse(new IntegerValue(number));
        Assert.Equal(number, document.RootElement.GetInt64());
    }

    [Theory]
    [InlineData(3.0, "3.0")]
    [InlineData(1.5, "1.5")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(0.30000000000000004, "0.30000000000000004")]
    [InlineData(1e20, "1.0E+20")]
    [InlineData(1.5e-7, "1.5E-7")]
    [InlineData(123456789012345678.0, "1.2345678901234568E+17")]
    [InlineData(double.MaxValue, "1.7976931348623157E+308")]
    [InlineData(5e-324, "5.0E-324")]
    public void FloatsUseTheTextRenderingAndStayValidJson(double number, string expectedJson)
    {
        var rendered = ResultJson.Render(new FloatValue(number));

        Assert.Equal(expectedJson, rendered);
        using var document = JsonDocument.Parse(rendered);
        Assert.Equal(number, document.RootElement.GetDouble());
    }

    [Fact]
    public void BooleansAndUnit()
    {
        Assert.Equal("true", ResultJson.Render(BooleanValue.True));
        Assert.Equal("false", ResultJson.Render(BooleanValue.False));
        Assert.Equal("null", ResultJson.Render(UnitValue.Instance));
    }

    [Fact]
    public void ListsAndNestedLists()
    {
        var value = ListOf(new IntegerValue(1), ListOf(new StringValue("a"), ListOf()), new FloatValue(2.5));

        Assert.Equal("[1,[\"a\",[]],2.5]", ResultJson.Render(value));
        Assert.Equal("[]", ResultJson.Render(ListOf()));
    }

    [Fact]
    public void MapsConvertKeysToTextAndKeepInsertionOrder()
    {
        Assert.Equal(
            "{\"zeta\":1,\"alpha\":2}",
            ResultJson.Render(MapOf((new StringValue("zeta"), new IntegerValue(1)), (new StringValue("alpha"), new IntegerValue(2)))));
        Assert.Equal(
            "{\"10\":\"ten\",\"-2\":\"minus\"}",
            ResultJson.Render(MapOf((new IntegerValue(10), new StringValue("ten")), (new IntegerValue(-2), new StringValue("minus")))));
        Assert.Equal(
            "{\"/a/b\":true}",
            ResultJson.Render(MapOf((new PathValue("/a/b"), BooleanValue.True))));
        Assert.Equal(
            "{\"true\":1,\"false\":0}",
            ResultJson.Render(MapOf((BooleanValue.True, new IntegerValue(1)), (BooleanValue.False, new IntegerValue(0)))));
        Assert.Equal("{}", ResultJson.Render(new MapValue()));
    }

    [Fact]
    public void ResultsRenderAsOkOrErrorObjects()
    {
        Assert.Equal("{\"ok\":5}", ResultJson.Render(ResultValue.Ok(new IntegerValue(5))));
        Assert.Equal("{\"error\":\"x\"}", ResultJson.Render(ResultValue.Error(new StringValue("x"))));
        Assert.Equal("{\"ok\":null}", ResultJson.Render(ResultValue.Ok(UnitValue.Instance)));
        Assert.Equal(
            "[{\"ok\":[1]},{\"error\":{\"ok\":2.0}}]",
            ResultJson.Render(ListOf(
                ResultValue.Ok(ListOf(new IntegerValue(1))),
                ResultValue.Error(ResultValue.Ok(new FloatValue(2.0))))));
    }

    [Fact]
    public void RecordsAndErrorsRenderTheirFieldsInDeclarationOrder()
    {
        Assert.Equal(
            "{\"exit_code\":3,\"standard_output\":\"out\",\"standard_error\":\"err\",\"program\":\"prog\"}",
            ResultJson.Render(new RecordValue("process_result", new IntegerValue(3), new StringValue("out"), new StringValue("err"), new StringValue("prog"))));
        Assert.Equal(
            "{\"kind\":\"file\",\"size_bytes\":12,\"modified_unix_seconds\":1700000000,\"permissions\":493,\"owner_user_id\":1000,\"owner_group_id\":1000,\"device_major\":8,\"device_minor\":1,\"inode\":424242,\"link_count\":2,\"accessed_unix_seconds\":1700000001,\"changed_unix_seconds\":1700000002}",
            ResultJson.Render(new RecordValue(
                "path_status",
                new StringValue("file"),
                new IntegerValue(12),
                new IntegerValue(1_700_000_000),
                new IntegerValue(493),
                new IntegerValue(1000),
                new IntegerValue(1000),
                new IntegerValue(8),
                new IntegerValue(1),
                new IntegerValue(424242),
                new IntegerValue(2),
                new IntegerValue(1_700_000_001),
                new IntegerValue(1_700_000_002))));
        Assert.Equal(
            "{\"process_identifier\":77}",
            ResultJson.Render(new RecordValue("process_handle", new IntegerValue(77))));
        Assert.Equal(
            "{\"kind\":\"not_found\",\"message\":\"absent\",\"subject\":\"/x\"}",
            ResultJson.Render(ErrorValues.Error("not_found", "/x", "absent")));
        Assert.Equal(
            "{\"kind\":\"other\",\"message\":\"m\",\"subject\":\"p\"}",
            ResultJson.Render(ErrorValues.Error("other", "p", "m")));
        Assert.Equal(
            "{\"kind\":\"not_found\",\"message\":\"m\",\"subject\":\"N\"}",
            ResultJson.Render(ErrorValues.EnvironmentFailure("not_found", "N", "m")));
        Assert.Equal(
            "{\"kind\":\"invalid_integer\",\"message\":\"m\",\"subject\":\"\"}",
            ResultJson.Render(ErrorValues.Error("invalid_integer", "", "m")));
        Assert.Equal(
            "{\"kernel_name\":\"Linux\",\"host_name\":\"h\",\"kernel_release\":\"6\",\"machine_architecture\":\"x86_64\"}",
            ResultJson.Render(new RecordValue("system_information", new StringValue("Linux"), new StringValue("h"), new StringValue("6"), new StringValue("x86_64"))));
    }

    [Fact]
    public void ErrorRecordsFromTheStdlibRenderTheirKind()
    {
        var run = InterpreterHarness.Run("return \"x\".parse_integer()\n");

        using var document = JsonDocument.Parse(ResultJson.Render(run.Outcome.Result!));
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("invalid_integer", error.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrEmpty(error.GetProperty("message").GetString()));
    }

    [Fact]
    public void FunctionValuesHaveNoRendering()
    {
        var run = InterpreterHarness.Run("let twice = value => value * 2\nreturn twice\n");

        Assert.Throws<ArgumentException>(() => ResultJson.Render(run.Outcome.Result!));
    }

    [Fact]
    public void DeeplyNestedListsRenderWithoutFailure()
    {
        Value value = new IntegerValue(1);
        for (var level = 0; level < 500; level++)
        {
            value = ListOf(value);
        }

        using var document = JsonDocument.Parse(ResultJson.Render(value), new JsonDocumentOptions { MaxDepth = 1000 });

        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
    }

    [Theory]
    [InlineData("return \"text\"\n", "\"text\"")]
    [InlineData("return p\"/tmp/x\"\n", "\"/tmp/x\"")]
    [InlineData("return 6 * 7\n", "42")]
    [InlineData("return 1.5 * 2.0\n", "3.0")]
    [InlineData("return not true\n", "false")]
    [InlineData("return [1, 2, 3].transform(value => value * value)\n", "[1,4,9]")]
    [InlineData("return {\"b\": 1, \"a\": 2}\n", "{\"b\":1,\"a\":2}")]
    [InlineData("return {1: [\"x\"], 2: []}\n", "{\"1\":[\"x\"],\"2\":[]}")]
    [InlineData("let outcome: result<integer, string> = ok(7)\nreturn outcome\n", "{\"ok\":7}")]
    [InlineData("let outcome: result<integer, string> = error(\"bad\")\nreturn outcome\n", "{\"error\":\"bad\"}")]
    [InlineData("let outcome: result<unit, string> = ok()\nreturn outcome\n", "{\"ok\":null}")]
    [InlineData("return process.run(process.request(\"echo\", [\"a b\"]))\n", "{\"ok\":{\"exit_code\":0,\"standard_output\":\"a b\\u000a\",\"standard_error\":\"\",\"program\":\"echo\"}}")]
    [InlineData("return\n", "null")]
    public void ReturnedValuesRenderAsExpected(string program, string expectedJson)
    {
        var run = InterpreterHarness.Run(program);

        Assert.Equal(0, run.ExitCode);
        var rendered = ResultJson.Render(run.Outcome.Result!);
        using var document = JsonDocument.Parse(rendered);
        Assert.Equal(expectedJson, rendered);
    }
}
