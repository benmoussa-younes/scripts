using Scripts.Runtime.Interpreter;
using Scripts.Runtime.Tests.Pure;

namespace Scripts.Runtime.Tests.InterpreterRuns;

/// <summary>
/// Regressions for the three blockers of the adversarial audit of <c>json</c>:
/// the encoding must be byte-identical to <c>--json-out</c>'s <c>result</c>, a lone
/// surrogate must decode to a clean failure, and the depth boundary is 1000.
/// </summary>
public sealed class PureJsonFunctionsTests
{
    /// <summary>BLOCKER 1 — the same value must render identically through <c>json.encode</c> and <c>--json-out</c>.</summary>
    [Theory]
    [MemberData(nameof(EscapeHeavyValues))]
    public void Encode_is_byte_identical_to_json_out(Value value)
    {
        Assert.Equal(ResultJson.Render(value), PureTestSupport.OkText(PureTestSupport.Call("json.encode", value)));
    }

    /// <summary>BLOCKER 2 — a lone surrogate is <c>invalid_syntax</c>, never a crash; a valid pair still decodes.</summary>
    [Theory]
    [InlineData("\"\\ud800\"")]
    [InlineData("\"\\udc00\"")]
    [InlineData("\"\\ud800x\"")]
    [InlineData("\"a\\udc00b\"")]
    [InlineData("[1,\"\\ud800\"]")]
    [InlineData("{\"k\":\"\\ud800\"}")]
    public void Decode_reports_a_lone_surrogate_as_invalid_syntax(string json)
    {
        var failure = PureTestSupport.ErrorRecord(PureTestSupport.Call("json.decode", new StringValue(json)));

        Assert.Equal("invalid_syntax", ((StringValue)failure.Get("kind")).Text);
    }

    [Fact]
    public void Decode_still_accepts_a_valid_surrogate_pair()
    {
        var value = PureTestSupport.OkPayload(PureTestSupport.Call("json.decode", new StringValue("\"\\ud83d\\ude00\"")));

        Assert.Equal("😀", ((StringValue)PureTestSupport.OkPayload(PureTestSupport.Call("json_value.as_string", value))).Text);
    }

    [Fact]
    public void Decode_rejects_duplicate_object_keys()
    {
        var failure = PureTestSupport.ErrorRecord(PureTestSupport.Call("json.decode", new StringValue("{\"a\":1,\"a\":2}")));

        Assert.Equal("invalid_syntax", ((StringValue)failure.Get("kind")).Text);
    }

    [Fact]
    public void Decode_accepts_distinct_object_keys()
    {
        var value = PureTestSupport.OkPayload(PureTestSupport.Call("json.decode", new StringValue("{\"a\":1,\"b\":2}")));
        var encoded = PureTestSupport.OkText(PureTestSupport.Call("json.encode", value));

        Assert.Equal("{\"a\":1,\"b\":2}", encoded);
    }

    /// <summary>BLOCKER 3 — nesting 1000 is accepted, 1001 is <c>depth_exceeded</c>, on both decode and encode.</summary>
    [Fact]
    public void Decode_rejects_1001_deep_and_accepts_1000()
    {
        Assert.Equal("list", ((StringValue)PureTestSupport.Call("json_value.kind", Decoded(NestedJson(1_000)))).Text);

        var failure = PureTestSupport.ErrorRecord(PureTestSupport.Call("json.decode", new StringValue(NestedJson(1_001))));

        Assert.Equal("depth_exceeded", ((StringValue)failure.Get("kind")).Text);
    }

    [Fact]
    public void Encode_of_a_1000_deep_value_round_trips()
    {
        var encoded = PureTestSupport.OkText(PureTestSupport.Call("json.encode", Decoded(NestedJson(1_000))));

        Assert.Equal(NestedJson(1_000), encoded);
    }

    // --- constructors (SPEC-STDLIB json.null_value/from_*/array/object) ---

    [Fact]
    public void NullValue_has_null_kind_and_encodes_as_null()
    {
        var value = PureTestSupport.Call("json.null_value");

        Assert.Equal("null", KindOf(value));
        Assert.Equal("null", PureTestSupport.OkText(PureTestSupport.Call("json.encode", value)));
    }

    [Fact]
    public void FromString_has_string_kind_and_round_trips_through_as_string()
    {
        var value = PureTestSupport.Call("json.from_string", new StringValue("a\"b\\c"));

        Assert.Equal("string", KindOf(value));
        Assert.Equal("a\"b\\c", ((StringValue)PureTestSupport.OkPayload(PureTestSupport.Call("json_value.as_string", value))).Text);
        Assert.Equal("\"a\\\"b\\\\c\"", PureTestSupport.OkText(PureTestSupport.Call("json.encode", value)));
    }

    [Fact]
    public void FromInteger_has_integer_kind_and_round_trips_through_as_integer()
    {
        var value = PureTestSupport.Call("json.from_integer", new IntegerValue(42));

        Assert.Equal("integer", KindOf(value));
        Assert.Equal(42, ((IntegerValue)PureTestSupport.OkPayload(PureTestSupport.Call("json_value.as_integer", value))).Number);
        Assert.Equal("42", PureTestSupport.OkText(PureTestSupport.Call("json.encode", value)));
    }

    [Fact]
    public void FromFloat_has_float_kind_and_round_trips_through_as_float()
    {
        var value = PureTestSupport.Call("json.from_float", new FloatValue(2.5));

        Assert.Equal("float", KindOf(value));
        Assert.Equal(2.5, ((FloatValue)PureTestSupport.OkPayload(PureTestSupport.Call("json_value.as_float", value))).Number);
        Assert.Equal("2.5", PureTestSupport.OkText(PureTestSupport.Call("json.encode", value)));
    }

    [Fact]
    public void FromBoolean_has_boolean_kind_and_round_trips_through_as_boolean()
    {
        var value = PureTestSupport.Call("json.from_boolean", BooleanValue.From(true));

        Assert.Equal("boolean", KindOf(value));
        Assert.True(((BooleanValue)PureTestSupport.OkPayload(PureTestSupport.Call("json_value.as_boolean", value))).Flag);
        Assert.Equal("true", PureTestSupport.OkText(PureTestSupport.Call("json.encode", value)));
    }

    [Fact]
    public void FromFloat_renders_the_canonical_float_text_including_negative_zero()
    {
        Assert.Equal("-0.0", EncodeFloat(-0.0));
        Assert.Equal("0.0", EncodeFloat(0.0));
        Assert.Equal("3.0", EncodeFloat(3.0));
        Assert.Equal("1.0E+20", EncodeFloat(1.0e20));
        Assert.Equal("1.0E-20", EncodeFloat(1.0e-20));
    }

    [Fact]
    public void FromInteger_renders_the_64_bit_extremes_exactly()
    {
        Assert.Equal("-9223372036854775808", EncodeInteger(long.MinValue));
        Assert.Equal("9223372036854775807", EncodeInteger(long.MaxValue));
    }

    [Fact]
    public void FromString_escapes_quotes_backslashes_controls_and_keeps_unicode_raw()
    {
        Assert.Equal("\"a\\\"b\"", EncodeString("a\"b"));
        Assert.Equal("\"a\\\\b\"", EncodeString("a\\b"));
        Assert.Equal("\"tab:\\u0009 lf:\\u000a cr:\\u000d bs:\\u0008 ff:\\u000c nul:\\u0000 esc:\\u001b\"", EncodeString("tab:\t lf:\n cr:\r bs:\b ff:\f nul:\0 esc:\u001b"));
        Assert.Equal("\"café😀\"", EncodeString("café😀"));
        Assert.Equal("\"ls:\\u2028 ps:\\u2029\"", EncodeString("ls:\u2028 ps:\u2029"));
    }

    [Fact]
    public void Array_preserves_order_and_empty_list_gives_empty_array()
    {
        var value = JsonArray(JsonInteger(3), JsonString("x"), JsonBoolean(false));

        Assert.Equal("list", KindOf(value));
        Assert.Equal("[3,\"x\",false]", PureTestSupport.OkText(PureTestSupport.Call("json.encode", value)));
        Assert.Equal("[]", PureTestSupport.OkText(PureTestSupport.Call("json.encode", JsonArray())));
    }

    [Fact]
    public void Object_preserves_insertion_order_and_empty_map_gives_empty_object()
    {
        var value = JsonObject(("b", JsonInteger(2)), ("a", JsonInteger(1)));

        Assert.Equal("map", KindOf(value));
        Assert.Equal("{\"b\":2,\"a\":1}", PureTestSupport.OkText(PureTestSupport.Call("json.encode", value)));
        Assert.Equal("{}", PureTestSupport.OkText(PureTestSupport.Call("json.encode", JsonObject())));
    }

    [Fact]
    public void Object_keys_use_bcs_escaping_for_quotes_backslashes_controls_and_unicode()
    {
        var value = JsonObject(("a\"b", JsonInteger(1)), ("c\\d", JsonInteger(2)), ("e\nf", JsonInteger(3)), ("café😀", JsonInteger(4)));

        Assert.Equal(
            "{\"a\\\"b\":1,\"c\\\\d\":2,\"e\\u000af\":3,\"café😀\":4}",
            PureTestSupport.OkText(PureTestSupport.Call("json.encode", value)));
    }

    [Fact]
    public void Nested_containers_round_trip_with_the_accessors()
    {
        var inner = JsonArray(JsonInteger(1), JsonNull());
        var middle = JsonObject(("inner", inner), ("text", JsonString("v")));
        var outer = JsonArray(middle);

        Assert.Equal("[{\"inner\":[1,null],\"text\":\"v\"}]", PureTestSupport.OkText(PureTestSupport.Call("json.encode", outer)));

        var list = (ListValue)PureTestSupport.OkPayload(PureTestSupport.Call("json_value.as_list", outer));
        Assert.Single(list.Items);
        Assert.Equal("map", KindOf(list.Items[0]));

        var map = (MapValue)PureTestSupport.OkPayload(PureTestSupport.Call("json_value.as_map", list.Items[0]));
        Assert.Equal(2, map.Count);
        Assert.Equal("inner", ((StringValue)map.Keys[0]).Text);
        Assert.Equal("list", KindOf(map.Values[0]));
        Assert.Equal("[1,null]", PureTestSupport.OkText(PureTestSupport.Call("json.encode", map.Values[0])));
        Assert.Equal("v", ((StringValue)PureTestSupport.OkPayload(PureTestSupport.Call("json_value.as_string", map.Values[1]))).Text);
    }

    [Fact]
    public void Array_shares_immutable_child_nodes_so_later_list_mutation_is_not_observed()
    {
        var child = JsonInteger(1);
        var source = new ListValue([child]);
        var array = PureTestSupport.Call("json.array", source);

        source.Items.Add(JsonInteger(2));

        Assert.Equal("[1]", PureTestSupport.OkText(PureTestSupport.Call("json.encode", array)));
        Assert.Same(((JsonValue)child).Node, ((JsonValue)JsonArrayItem(array, 0)).Node);
    }

    [Fact]
    public void Object_shares_immutable_child_nodes_so_later_map_mutation_is_not_observed()
    {
        var child = JsonInteger(1);
        var source = StringMap(("k", child));
        var value = PureTestSupport.Call("json.object", source);

        source.Set(new StringValue("k"), JsonInteger(2));
        source.Set(new StringValue("other"), JsonInteger(3));

        Assert.Equal("{\"k\":1}", PureTestSupport.OkText(PureTestSupport.Call("json.encode", value)));
        Assert.Same(((JsonValue)child).Node, ((JsonValue)JsonObjectValue(value, 0)).Node);
    }

    [Fact]
    public void Constructed_nodes_are_value_independent_across_shallow_copies()
    {
        var original = JsonArray(JsonInteger(1));
        var copy = (Value)original.DeepCopy();

        Assert.Same(original, copy);
        Assert.Equal("[1]", PureTestSupport.OkText(PureTestSupport.Call("json.encode", copy)));
    }

    [Fact]
    public void Encoding_an_array_around_a_1000_deep_document_is_depth_exceeded_not_a_crash()
    {
        var deep = Decoded(NestedJson(1_000));
        var outer = JsonArray(deep);

        var failure = PureTestSupport.ErrorRecord(PureTestSupport.Call("json.encode", outer));

        Assert.Equal("depth_exceeded", ((StringValue)failure.Get("kind")).Text);
    }

    // --- BUG-004: a function nested in a result or a built-in record is not encodable ---

    [Fact]
    public void Encode_rejects_a_function_nested_in_an_ok_result()
    {
        var function = Function();

        var failure = PureTestSupport.ErrorRecord(PureTestSupport.Call("json.encode", ResultValue.Ok(function)));

        Assert.Equal("not_encodable", ((StringValue)failure.Get("kind")).Text);
    }

    [Fact]
    public void Encode_rejects_a_function_nested_in_an_error_result()
    {
        var function = Function();

        var failure = PureTestSupport.ErrorRecord(PureTestSupport.Call("json.encode", ResultValue.Error(function)));

        Assert.Equal("not_encodable", ((StringValue)failure.Get("kind")).Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EncodePretty_rejects_a_function_nested_in_a_result(bool isOk)
    {
        var function = Function();
        var result = isOk ? ResultValue.Ok(function) : ResultValue.Error(function);

        var failure = PureTestSupport.ErrorRecord(PureTestSupport.Call("json.encode_pretty", result));

        Assert.Equal("not_encodable", ((StringValue)failure.Get("kind")).Text);
    }

    [Fact]
    public void Encode_rejects_a_function_nested_in_a_list()
    {
        var failure = PureTestSupport.ErrorRecord(
            PureTestSupport.Call("json.encode", new ListValue([Function()])));

        Assert.Equal("not_encodable", ((StringValue)failure.Get("kind")).Text);
    }

    [Fact]
    public void Encode_still_encodes_a_result_of_a_normal_value()
    {
        Assert.Equal(
            "{\"ok\":1}",
            PureTestSupport.OkText(PureTestSupport.Call("json.encode", ResultValue.Ok(PureTestSupport.Integer(1)))));
        Assert.Equal(
            "{\"error\":\"x\"}",
            PureTestSupport.OkText(PureTestSupport.Call("json.encode", ResultValue.Error(new StringValue("x")))));
    }

    [Fact]
    public void Encode_rejects_a_function_in_a_builtin_record_field()
    {
        var record = new RecordValue(
            "process_result",
            PureTestSupport.Integer(0),
            Function(),
            new StringValue(string.Empty),
            new StringValue("program"));

        var failure = PureTestSupport.ErrorRecord(PureTestSupport.Call("json.encode", record));

        Assert.Equal("not_encodable", ((StringValue)failure.Get("kind")).Text);
    }

    public static TheoryData<Value> EscapeHeavyValues()
    {
        var strings = new[]
        {
            "plain ascii",
            "café",
            "a\"b",
            "a\\b",
            "tab:\t lf:\n cr:\r bs:\b ff:\f nul:\0 esc:\u001b",
            "del:\u007f",
            "c1:\u0085",
            "ls:\u2028 ps:\u2029",
            "😀🎉",
            "e\u0301a\u0308",
            "hé😀\t\"\\\0",
        };

        var data = new TheoryData<Value>();

        foreach (var text in strings)
        {
            data.Add(new StringValue(text));
            data.Add(new PathValue(text));
        }

        data.Add(new ListValue([.. strings.Select(text => (Value)new StringValue(text))]));

        var map = new MapValue();
        for (var index = 0; index < strings.Length; index++)
        {
            map.Set(new StringValue($"k{index}"), new StringValue(strings[index]));
        }

        data.Add(map);

        // A non-ASCII key exercises the property-name escaping path too.
        var accented = new MapValue();
        accented.Set(new StringValue("café"), new StringValue("café"));
        data.Add(accented);

        return data;
    }

    private static string KindOf(Value value) =>
        ((StringValue)PureTestSupport.Call("json_value.kind", value)).Text;

    private static Value Function() => PureTestSupport.Function(0, _ => PureTestSupport.Integer(1));

    private static string EncodeFloat(double number) =>
        PureTestSupport.OkText(PureTestSupport.Call("json.encode", JsonFloat(number)));

    private static string EncodeInteger(long number) =>
        PureTestSupport.OkText(PureTestSupport.Call("json.encode", JsonInteger(number)));

    private static string EncodeString(string text) =>
        PureTestSupport.OkText(PureTestSupport.Call("json.encode", JsonString(text)));

    private static Value JsonNull() => PureTestSupport.Call("json.null_value");

    private static Value JsonString(string text) =>
        PureTestSupport.Call("json.from_string", new StringValue(text));

    private static Value JsonInteger(long number) =>
        PureTestSupport.Call("json.from_integer", new IntegerValue(number));

    private static Value JsonFloat(double number) =>
        PureTestSupport.Call("json.from_float", new FloatValue(number));

    private static Value JsonBoolean(bool flag) =>
        PureTestSupport.Call("json.from_boolean", BooleanValue.From(flag));

    private static Value JsonArray(params Value[] items) =>
        PureTestSupport.Call("json.array", new ListValue([.. items]));

    private static Value JsonObject(params (string Key, Value Value)[] fields) =>
        PureTestSupport.Call("json.object", StringMap(fields));

    private static MapValue StringMap(params (string Key, Value Value)[] fields)
    {
        var map = new MapValue();
        foreach (var (key, value) in fields)
        {
            map.Set(new StringValue(key), value);
        }

        return map;
    }

    private static Value JsonArrayItem(Value array, int index) =>
        ((ListValue)PureTestSupport.OkPayload(PureTestSupport.Call("json_value.as_list", array))).Items[index];

    private static Value JsonObjectValue(Value value, int index) =>
        ((MapValue)PureTestSupport.OkPayload(PureTestSupport.Call("json_value.as_map", value))).Values[index];

    private static Value Decoded(string json) =>
        PureTestSupport.OkPayload(PureTestSupport.Call("json.decode", new StringValue(json)));

    private static string NestedJson(int depth) => new string('[', depth) + new string(']', depth);
}
