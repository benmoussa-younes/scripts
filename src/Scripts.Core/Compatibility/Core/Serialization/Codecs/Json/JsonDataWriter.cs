// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Core.Results;
using Core.Serialization.Values;
using Scripts.Core;
using Scripts.Core.Json;

namespace Core.Serialization.Codecs.Json;

/// <summary>
/// Node-only JSON writer for Scripts: encodes a <see cref="DataNode"/> tree with the
/// existing <see cref="JsonWriter"/> (the same escaping as <c>run --json-out</c>).
/// It deliberately does not carry the document/table codecs of Core.
/// </summary>
public static class JsonDataWriter
{
    /// <summary>Encodes a node tree to UTF-8 JSON bytes.</summary>
    public static Result<byte[]> WriteNodeToUtf8Bytes(
        DataNode node,
        JsonNumberFormat numberFormat,
        JsonEscaping escaping)
    {
        ArgumentNullException.ThrowIfNull(node);

        var writer = new JsonWriter();
        try
        {
            if (!Write(writer, node, numberFormat, 1))
            {
                return Failure(CoreErrorCodes.SerializationDepthExceeded, "the value nests beyond the maximum depth.");
            }

            return Result<byte[]>.Success(Encoding.UTF8.GetBytes(writer.ToString()));
        }
        catch (OutOfMemoryException)
        {
            return Failure(CoreErrorCodes.SerializationTooLarge, "the JSON text is too large.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Failure(CoreErrorCodes.SerializationInvalidDocument, exception.Message);
        }
    }

    private static bool Write(JsonWriter writer, DataNode node, JsonNumberFormat numberFormat, int depth)
    {
        if (depth > Limits.MaximumValueDepth)
        {
            return false;
        }

        switch (node.Kind)
        {
            case DataNodeKind.Scalar:
                return WriteScalar(writer, node.Value, numberFormat, depth);
            case DataNodeKind.List:
                writer.BeginArray();
                foreach (var item in node.Items)
                {
                    if (!Write(writer, item, numberFormat, depth + 1))
                    {
                        return false;
                    }
                }

                writer.EndArray();
                return true;
            case DataNodeKind.Map:
                writer.BeginObject();
                foreach (var property in node.Properties)
                {
                    writer.WriteName(property.Name);
                    if (!Write(writer, property.Value, numberFormat, depth + 1))
                    {
                        return false;
                    }
                }

                writer.EndObject();
                return true;
            default:
                return false;
        }
    }

    private static bool WriteScalar(JsonWriter writer, DataValue value, JsonNumberFormat numberFormat, int depth)
    {
        switch (value.Kind)
        {
            case DataKind.Null:
                writer.WriteNull();
                return true;
            case DataKind.Boolean:
                value.TryGetBoolean(out var flag);
                writer.WriteBoolean(flag);
                return true;
            case DataKind.Integer:
                value.TryGetInteger(out var integer);
                writer.WriteInteger(integer);
                return true;
            case DataKind.Number:
                value.TryGetNumber(out var number);
                writer.WriteRawFloatText(numberFormat.Render(number));
                return true;
            case DataKind.Decimal:
                writer.WriteRawFloatText(value.ToInvariantText());
                return true;
            case DataKind.Text:
                value.TryGetText(out var text);
                writer.WriteString(text ?? string.Empty);
                return true;
            case DataKind.Date:
            case DataKind.Time:
            case DataKind.Timestamp:
                writer.WriteString(value.ToInvariantText());
                return true;
            case DataKind.Node:
                if (!value.TryGetNode(out var node) || node is null)
                {
                    return false;
                }

                return Write(writer, node, numberFormat, depth + 1);
            default:
                return false;
        }
    }

    private static Result<byte[]> Failure(string code, string message) =>
        Result<byte[]>.Failure(new CoreError(code, message));
}
