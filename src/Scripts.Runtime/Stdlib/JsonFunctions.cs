using System.Text;
using System.Text.Json;
using Core.Results;
using global::Core.Serialization.Codecs.Json;
using global::Core.Serialization.Values;
using Scripts.Core;
using CoreDataKind = global::Core.Serialization.Values.DataKind;

namespace Scripts.Runtime;

/// <summary>
/// The <c>json</c> functions (DESIGN §7.10/§7.10b), implemented on
/// <c>global::Core.Serialization</c> (<c>DataNode</c> ⇄ <c>JsonDataWriter</c>/
/// <c>JsonDataReader</c>). A scripts value is mapped to a <see cref="DataNode"/> tree,
/// which the library emits with scripts's canonical float text; a decoded tree is
/// wrapped in the opaque <see cref="JsonValue"/>. Failures are the unified
/// <c>error</c> with JSON <c>kind</c>s (no <c>JsonError</c> type).
/// </summary>
internal static class JsonFunctions
{
    /// <summary>
    /// Registers encode/decode, the seven constructors and the <c>json_value</c> accessors.
    /// </summary>
    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["json.encode"] = Encode;
        table["json.encode_pretty"] = EncodePretty;
        table["json.decode"] = Decode;
        table["json.null_value"] = NullValue;
        table["json.from_string"] = FromString;
        table["json.from_integer"] = FromInteger;
        table["json.from_float"] = FromFloat;
        table["json.from_boolean"] = FromBoolean;
        table["json.array"] = Array;
        table["json.object"] = Object;
        table["json_value.kind"] = Kind;
        table["json_value.as_string"] = AsString;
        table["json_value.as_integer"] = AsInteger;
        table["json_value.as_float"] = AsFloat;
        table["json_value.as_boolean"] = AsBoolean;
        table["json_value.as_list"] = AsList;
        table["json_value.as_map"] = AsMap;
    }

    // --- json constructors ---

    /// <summary>
    /// Builds the JSON null value (SPEC-STDLIB <c>json.null_value</c>): a scalar
    /// node holding the empty <see cref="DataValue"/>, so <c>kind()</c> is
    /// <c>"null"</c> and encoding renders <c>null</c>.
    /// </summary>
    private static Value NullValue(RuntimeContext context, Value[] arguments) =>
        new JsonValue(Scalar(DataValue.Null));

    /// <summary>
    /// Wraps <paramref name="text"/> as a JSON string node without parsing it
    /// (SPEC-STDLIB <c>json.from_string</c>). Encoding applies scripts's escaping.
    /// </summary>
    private static Value FromString(RuntimeContext context, Value[] arguments) =>
        new JsonValue(Scalar(DataValue.From(ArgumentAccess.Text(arguments, 0))));

    /// <summary>Wraps an exact signed 64-bit integer as a JSON integer node (SPEC-STDLIB <c>json.from_integer</c>).</summary>
    private static Value FromInteger(RuntimeContext context, Value[] arguments) =>
        new JsonValue(Scalar(DataValue.From(ArgumentAccess.Integer(arguments, 0))));

    /// <summary>
    /// Wraps a finite scripts float as a JSON number node (SPEC-STDLIB
    /// <c>json.from_float</c>); encoding uses the canonical float text of DESIGN §5.5
    /// (including <c>-0.0</c>).
    /// </summary>
    private static Value FromFloat(RuntimeContext context, Value[] arguments) =>
        new JsonValue(Scalar(DataValue.From(ArgumentAccess.Float(arguments, 0))));

    /// <summary>Wraps a boolean as a JSON boolean node (SPEC-STDLIB <c>json.from_boolean</c>).</summary>
    private static Value FromBoolean(RuntimeContext context, Value[] arguments) =>
        new JsonValue(Scalar(DataValue.From(((BooleanValue)arguments[0]).Flag)));

    /// <summary>
    /// Builds a JSON array from <c>list&lt;json_value&gt;</c>, preserving list order
    /// (SPEC-STDLIB <c>json.array</c>). Each child node is shared as an immutable
    /// <see cref="DataNode"/>: the <see cref="JsonValue"/> wrapper is immutable and
    /// <c>DataNode</c> has no mutation API, so value semantics hold without a copy.
    /// </summary>
    private static Value Array(RuntimeContext context, Value[] arguments)
    {
        var list = ArgumentAccess.List(arguments, 0);
        var items = new List<DataNode>(list.Items.Count);
        foreach (var item in list.Items)
        {
            items.Add(JsonNode(item));
        }

        return new JsonValue(DataNode.List(items));
    }

    /// <summary>
    /// Builds a JSON object from <c>map&lt;string, json_value&gt;</c>, preserving the
    /// map's insertion order (SPEC-STDLIB <c>json.object</c>). Keys are rendered by
    /// <see cref="TextRenderer.Render"/> (strings pass through unchanged); a map
    /// cannot carry duplicate keys.
    /// </summary>
    private static Value Object(RuntimeContext context, Value[] arguments)
    {
        var map = ArgumentAccess.Map(arguments, 0);
        var properties = new List<DataProperty>(map.Count);
        for (var position = 0; position < map.Count; position++)
        {
            properties.Add(new DataProperty(TextRenderer.Render(map.Keys[position]), JsonNode(map.Values[position])));
        }

        return new JsonValue(DataNode.Map(properties));
    }

    private static DataNode JsonNode(Value value) => ((JsonValue)value).Node;

    // --- encode ---

    private static Value Encode(RuntimeContext context, Value[] arguments)
    {
        if (!TryToNode(arguments[0], out var node))
        {
            return Error("not_encodable", string.Empty, "this value has no JSON form");
        }

        // The engine is asked for scripts's own escaping (DESIGN 8.3: raw UTF-8 for
        // non-ASCII, lowercase \uXXXX for control characters), so that encode is
        // byte-identical to the `result` field of `run --json-out`, which uses the
        // scripts JsonWriter. The number rendering is scripts's canonical float text (5.5).
        var result = JsonDataWriter.WriteNodeToUtf8Bytes(
            node!, JsonNumberFormat.From(FloatFormatter.Render), JsonEscaping.Utf8Raw);
        if (!result.IsSuccess)
        {
            return EncodeFailure(result.Error);
        }

        // encode is a producer of text like format/join/replace: the 2^28 limit applies here too (P2).
        var text = Encoding.UTF8.GetString(result.Value);
        SizeLimits.EnsureStringLength(text.Length);
        return ResultValue.Ok(new StringValue(text));
    }

    private static Value EncodePretty(RuntimeContext context, Value[] arguments)
    {
        if (!TryToNode(arguments[0], out var node))
        {
            return Error("not_encodable", string.Empty, "this value has no JSON form");
        }

        var result = JsonDataWriter.WriteNodeToUtf8Bytes(
            node!, JsonNumberFormat.From(FloatFormatter.Render), JsonEscaping.Utf8Raw);
        if (!result.IsSuccess)
        {
            return EncodeFailure(result.Error);
        }

        var compact = Encoding.UTF8.GetString(result.Value);
        var pretty = PrettyJson(compact);
        SizeLimits.EnsureStringLength(pretty.Length);
        return ResultValue.Ok(new StringValue(pretty));
    }

    /// <summary>
    /// Two-space indentation over the already-encoded compact JSON: the tokens, numbers and escaping are
    /// unchanged, only the whitespace between them is inserted. Empty containers stay `{}`/`[]`.
    /// </summary>
    private static string PrettyJson(string compact)
    {
        var builder = new StringBuilder(compact.Length + (compact.Length / 4));
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = 0; index < compact.Length; index++)
        {
            var character = compact[index];
            if (inString)
            {
                builder.Append(character);
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    builder.Append(character);
                    break;
                case '{':
                case '[':
                    builder.Append(character);
                    var closing = character == '{' ? '}' : ']';
                    if (NextSignificant(compact, index + 1) == closing)
                    {
                        builder.Append(closing);
                        index++;
                        break;
                    }

                    depth++;
                    builder.Append('\n');
                    builder.Append(' ', depth * 2);
                    break;
                case '}':
                case ']':
                    depth--;
                    builder.Append('\n');
                    builder.Append(' ', depth * 2);
                    builder.Append(character);
                    break;
                case ',':
                    builder.Append(',');
                    builder.Append('\n');
                    builder.Append(' ', depth * 2);
                    break;
                case ':':
                    builder.Append(": ");
                    break;
                case ' ':
                case '\n':
                case '\r':
                case '\t':
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    private static char NextSignificant(string text, int start)
    {
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] is not (' ' or '\n' or '\r' or '\t'))
            {
                return text[index];
            }
        }

        return '\0';
    }

    /// <summary>
    /// Maps a writer failure to a JSON <c>kind</c> with a message: nesting past
    /// the writer's limit is <c>depth_exceeded</c> (not a crash), anything else is
    /// <c>not_encodable</c>. The library's message is dropped (English BCL text).
    /// </summary>
    private static ResultValue EncodeFailure(CoreError error) =>
        error.Code == CoreErrorCodes.SerializationDepthExceeded
            ? Error("depth_exceeded", string.Empty, "the value nests containers beyond the limit of 1000")
            : error.Code == CoreErrorCodes.SerializationTooLarge
              || (error.Code == CoreErrorCodes.SerializationInvalidDocument
                  && error.Message.Contains("too large", StringComparison.OrdinalIgnoreCase))
                // The BCL writer rejects a single token beyond its own bound before scripts's 2^28 check can run:
                // report a size failure, not "no JSON form" (audit R4).
                ? Error("too_large", string.Empty, "the JSON text exceeds the writer's single-value limit")
                : Error("not_encodable", string.Empty, "this value has no JSON form");

    private static bool TryToNode(Value value, out DataNode? node)
    {
        switch (value)
        {
            case StringValue text:
                node = Scalar(DataValue.From(text.Text));
                return true;
            case PathValue path:
                node = Scalar(DataValue.From(path.Text));
                return true;
            case IntegerValue integer:
                node = Scalar(DataValue.From(integer.Number));
                return true;
            case FloatValue number:
                node = Scalar(DataValue.From(number.Number));
                return true;
            case BooleanValue boolean:
                node = Scalar(DataValue.From(boolean.Flag));
                return true;
            case UnitValue:
                node = Scalar(DataValue.Null);
                return true;
            case ListValue list:
                var items = new List<DataNode>(list.Items.Count);
                foreach (var item in list.Items)
                {
                    if (!TryToNode(item, out var itemNode))
                    {
                        node = null;
                        return false;
                    }

                    items.Add(itemNode!);
                }

                node = DataNode.List(items);
                return true;
            case MapValue map:
                var properties = new List<DataProperty>(map.Count);
                for (var position = 0; position < map.Count; position++)
                {
                    if (!TryToNode(map.Values[position], out var propertyNode))
                    {
                        node = null;
                        return false;
                    }

                    properties.Add(new DataProperty(TextRenderer.Render(map.Keys[position]), propertyNode!));
                }

                node = DataNode.Map(properties);
                return true;
            case ResultValue result:
                if (!TryToNode(result.Payload, out var payloadNode))
                {
                    node = null;
                    return false;
                }

                node = DataNode.Map([new DataProperty(result.IsOk ? "ok" : "error", payloadNode!)]);
                return true;
            case JsonValue json:
                node = json.Node;
                return true;
            case RecordValue record:
                var fields = new List<DataProperty>();
                foreach (var (fieldName, _) in BuiltinTypes.FieldsOf(record.TypeName))
                {
                    if (!TryToNode(record.Get(fieldName), out var fieldNode))
                    {
                        node = null;
                        return false;
                    }

                    fields.Add(new DataProperty(fieldName, fieldNode!));
                }

                node = DataNode.Map(fields);
                return true;
            default:
                node = null;
                return false;
        }
    }

    private static DataNode Scalar(DataValue value) => DataNode.Scalar(value);

    // --- decode ---

    private static Value Decode(RuntimeContext context, Value[] arguments)
    {
        var utf8 = Encoding.UTF8.GetBytes(ArgumentAccess.Text(arguments, 0));

        // The library refuses non-finite doubles and carries out-of-range numbers as
        // text; a numeric token that fits neither the int64 nor the finite-double
        // range must be out_of_range (never a silent float), so it is checked on the
        // raw token before the structural read.
        if (FindOutOfRangeNumber(utf8))
        {
            return Error("out_of_range", string.Empty, "a number is outside the range of 64-bit integers or finite floats");
        }

        var result = JsonDataReader.ReadNode(utf8, Limits.MaximumValueDepth);
        if (result.IsFailure)
        {
            // Classify on the library's stable code, not its English message: nesting
            // past the maximum is depth_exceeded, anything else is invalid_syntax.
            return result.Error.Code == CoreErrorCodes.SerializationDepthExceeded
                ? Error("depth_exceeded", string.Empty, $"the nesting exceeds the maximum depth of {Limits.MaximumValueDepth}")
                : Error("invalid_syntax", string.Empty, "the text is not a valid JSON document");
        }

        return ResultValue.Ok(new JsonValue(result.Value));
    }

    /// <summary>
    /// True when a numeric token has no scripts representation: an integer-shaped token
    /// (no <c>.</c>/<c>e</c>/<c>E</c>) that overflows <see cref="long"/>, or any token that
    /// is not a finite <see cref="double"/>. Classifying on the raw token is what makes
    /// <c>9223372036854775808</c> an <c>out_of_range</c> rather than a silent float, and
    /// <c>1e400</c> an <c>out_of_range</c> rather than <c>∞</c>.
    /// </summary>
    private static bool FindOutOfRangeNumber(byte[] utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = Limits.MaximumValueDepth });

            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.Number)
                {
                    continue;
                }

                var token = reader.ValueSpan;
                var isFloatShaped = false;
                foreach (var value in token)
                {
                    if (value is (byte)'.' or (byte)'e' or (byte)'E')
                    {
                        isFloatShaped = true;
                        break;
                    }
                }

                if (!isFloatShaped)
                {
                    if (!reader.TryGetInt64(out _))
                    {
                        return true;
                    }

                    continue;
                }

                if (!reader.TryGetDouble(out var number) || !double.IsFinite(number))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // A syntax error is reported by the structural read; here it is not a range error.
        }

        return false;
    }

    // --- json_value accessors ---

    private static Value Kind(RuntimeContext context, Value[] arguments) =>
        new StringValue(KindOf(Json(arguments, 0).Node));

    private static string KindOf(DataNode node) => node.Kind switch
    {
        DataNodeKind.List => "list",
        DataNodeKind.Map => "map",
        _ => node.Value.Kind switch
        {
            CoreDataKind.Text => "string",
            CoreDataKind.Integer => "integer",
            CoreDataKind.Number => "float",
            CoreDataKind.Boolean => "boolean",
            _ => "null",
        },
    };

    private static Value AsString(RuntimeContext context, Value[] arguments) =>
        Json(arguments, 0).Node is { Kind: DataNodeKind.Scalar, Value.Kind: CoreDataKind.Text } scalar
            ? ResultValue.Ok(new StringValue(scalar.Value.ToInvariantText()))
            : WrongKind("string");

    private static Value AsInteger(RuntimeContext context, Value[] arguments) =>
        Json(arguments, 0).Node is { Kind: DataNodeKind.Scalar, Value.Kind: CoreDataKind.Integer } scalar
            ? ResultValue.Ok(IntegerValue.From(scalar.Value.TryGetInteger(out var number) ? number : 0))
            : WrongKind("integer");

    private static Value AsFloat(RuntimeContext context, Value[] arguments) =>
        Json(arguments, 0).Node is { Kind: DataNodeKind.Scalar, Value.Kind: CoreDataKind.Number } scalar
            ? ResultValue.Ok(new FloatValue(scalar.Value.TryGetNumber(out var number) ? number : 0))
            : WrongKind("float");

    private static Value AsBoolean(RuntimeContext context, Value[] arguments) =>
        Json(arguments, 0).Node is { Kind: DataNodeKind.Scalar, Value.Kind: CoreDataKind.Boolean } scalar
            ? ResultValue.Ok(BooleanValue.From(scalar.Value.TryGetBoolean(out var flag) && flag))
            : WrongKind("boolean");

    /// <summary>
    /// Materialises a JSON array as <c>list&lt;json_value&gt;</c> (SPEC-STDLIB 7.10b). Each element is a fresh
    /// <see cref="JsonValue"/> wrapping the (immutable) child node, so the result is value-independent and
    /// no new type is introduced; enumeration then uses the existing list operations.
    /// </summary>
    private static Value AsList(RuntimeContext context, Value[] arguments)
    {
        var node = Json(arguments, 0).Node;
        if (node.Kind != DataNodeKind.List)
        {
            return WrongKind("list");
        }

        var items = new List<Value>(node.Items.Count);
        foreach (var item in node.Items)
        {
            items.Add(new JsonValue(item));
        }

        return ResultValue.Ok(new ListValue(items));
    }

    /// <summary>
    /// Materialises a JSON object as <c>map&lt;string, json_value&gt;</c>, preserving the decoded insertion
    /// order (SPEC-STDLIB 7.10b). Keys are the raw property names; values are fresh <see cref="JsonValue"/>s.
    /// </summary>
    private static Value AsMap(RuntimeContext context, Value[] arguments)
    {
        var node = Json(arguments, 0).Node;
        if (node.Kind != DataNodeKind.Map)
        {
            return WrongKind("map");
        }

        var map = new MapValue();
        foreach (var property in node.Properties)
        {
            map.Set(new StringValue(property.Name), new JsonValue(property.Value));
        }

        return ResultValue.Ok(map);
    }

    private static JsonValue Json(Value[] arguments, int position) => (JsonValue)arguments[position];

    private static ResultValue WrongKind(string expected) => Error("not_encodable", string.Empty, $"the value is not of type {expected}");

    private static ResultValue Error(string kind, string subject, string message) =>
        ResultValue.Error(ErrorValues.Error(kind, subject, message));
}
