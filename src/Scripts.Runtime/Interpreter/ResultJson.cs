using Scripts.Core;
using Scripts.Core.Json;

namespace Scripts.Runtime.Interpreter;

/// <summary>JSON rendering of the top-level <c>return</c> value for <c>--json-out</c> (DESIGN 8.4).</summary>
public static class ResultJson
{
    public static string Render(Value value)
    {
        var writer = new JsonWriter();
        Write(writer, value);
        return writer.ToString();
    }

    private static void Write(JsonWriter writer, Value value)
    {
        switch (value)
        {
            case StringValue text:
                writer.WriteString(text.Text);
                break;
            case PathValue path:
                writer.WriteString(path.Text);
                break;
            case IntegerValue integer:
                writer.WriteInteger(integer.Number);
                break;
            case FloatValue number:
                writer.WriteRawFloatText(FloatFormatter.Render(number.Number));
                break;
            case BooleanValue boolean:
                writer.WriteBoolean(boolean.Flag);
                break;
            case UnitValue:
                writer.WriteNull();
                break;
            case ListValue list:
                WriteList(writer, list);
                break;
            case MapValue map:
                WriteMap(writer, map);
                break;
            case ResultValue result:
                writer.BeginObject();
                writer.WriteName(result.IsOk ? "ok" : "error");
                Write(writer, result.Payload);
                writer.EndObject();
                break;
            case RecordValue record:
                WriteRecord(writer, record);
                break;
            case JsonValue json:
                WriteJsonNode(writer, json.Node);
                break;
            default:
                throw new ArgumentException($"A value of type {value.GetType().Name} has no JSON rendering", nameof(value));
        }
    }

    private static void WriteList(JsonWriter writer, ListValue list)
    {
        writer.BeginArray();
        foreach (var item in list.Items)
        {
            Write(writer, item);
        }

        writer.EndArray();
    }

    private static void WriteMap(JsonWriter writer, MapValue map)
    {
        writer.BeginObject();
        for (var position = 0; position < map.Count; position++)
        {
            writer.WriteName(TextRenderer.Render(map.Keys[position]));
            Write(writer, map.Values[position]);
        }

        writer.EndObject();
    }

    private static void WriteRecord(JsonWriter writer, RecordValue record)
    {
        writer.BeginObject();
        foreach (var (fieldName, _) in BuiltinTypes.FieldsOf(record.TypeName))
        {
            writer.WriteName(fieldName);
            Write(writer, record.Get(fieldName));
        }

        writer.EndObject();
    }

    /// <summary>A decoded JSON node re-renders with scripts's canonical text (DESIGN 8.4 / 5.5).</summary>
    private static void WriteJsonNode(JsonWriter writer, global::Core.Serialization.Values.DataNode node)
    {
        switch (node.Kind)
        {
            case global::Core.Serialization.Values.DataNodeKind.List:
                writer.BeginArray();
                foreach (var item in node.Items)
                {
                    WriteJsonNode(writer, item);
                }

                writer.EndArray();
                break;
            case global::Core.Serialization.Values.DataNodeKind.Map:
                writer.BeginObject();
                foreach (var property in node.Properties)
                {
                    writer.WriteName(property.Name);
                    WriteJsonNode(writer, property.Value);
                }

                writer.EndObject();
                break;
            default:
                WriteJsonScalar(writer, node.Value);
                break;
        }
    }

    private static void WriteJsonScalar(JsonWriter writer, global::Core.Serialization.Values.DataValue value)
    {
        switch (value.Kind)
        {
            case global::Core.Serialization.Values.DataKind.Text:
                writer.WriteString(value.ToInvariantText());
                break;
            case global::Core.Serialization.Values.DataKind.Integer:
                writer.WriteInteger(value.TryGetInteger(out var integer) ? integer : 0);
                break;
            case global::Core.Serialization.Values.DataKind.Number:
                writer.WriteRawFloatText(FloatFormatter.Render(value.TryGetNumber(out var number) ? number : 0));
                break;
            case global::Core.Serialization.Values.DataKind.Boolean:
                writer.WriteBoolean(value.TryGetBoolean(out var flag) && flag);
                break;
            default:
                writer.WriteNull();
                break;
        }
    }
}
