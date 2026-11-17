// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Core.Results;
using Core.Serialization.Values;

namespace Core.Serialization.Codecs.Json;

/// <summary>
/// Node-only JSON reader for Scripts: decodes UTF-8 JSON into a
/// <see cref="DataNode"/> tree with <see cref="Utf8JsonReader"/>. It deliberately
/// does not carry the document/table codecs of Core.
/// </summary>
public static class JsonDataReader
{
    /// <summary>Decodes one JSON document into a node tree.</summary>
    public static Result<DataNode> ReadNode(ReadOnlySpan<byte> utf8, int maxDepth)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
            {
                MaxDepth = maxDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });

            if (!reader.Read())
            {
                return Failure(CoreErrorCodes.SerializationInvalidDocument, "the text is empty.");
            }

            var node = ReadValue(ref reader, maxDepth, 1);
            if (reader.Read())
            {
                return Failure(CoreErrorCodes.SerializationInvalidDocument, "the text has trailing content.");
            }

            return Result<DataNode>.Success(node);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            var depth = exception.Message.Contains("depth", StringComparison.OrdinalIgnoreCase);
            return Failure(
                depth ? CoreErrorCodes.SerializationDepthExceeded : CoreErrorCodes.SerializationInvalidDocument,
                exception.Message);
        }
    }

    private static DataNode ReadValue(ref Utf8JsonReader reader, int maxDepth, int depth)
    {
        if (depth > maxDepth)
        {
            throw new JsonException("the value nests beyond the maximum depth.");
        }

        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                var properties = new List<DataProperty>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        throw new JsonException("expected a property name.");
                    }

                    var name = reader.GetString()!;
                    if (!names.Add(name))
                    {
                        throw new JsonException($"duplicate object key `{name}`.");
                    }

                    if (!reader.Read())
                    {
                        throw new JsonException("expected a property value.");
                    }

                    properties.Add(new DataProperty(name, ReadValue(ref reader, maxDepth, depth + 1)));
                }

                return DataNode.Map(properties);
            case JsonTokenType.StartArray:
                var items = new List<DataNode>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    items.Add(ReadValue(ref reader, maxDepth, depth + 1));
                }

                return DataNode.List(items);
            case JsonTokenType.String:
                return DataNode.Scalar(DataValue.From(reader.GetString()!));
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var integer))
                {
                    return DataNode.Scalar(DataValue.From(integer));
                }

                if (reader.TryGetDouble(out var number) && double.IsFinite(number))
                {
                    return DataNode.Scalar(DataValue.From(number));
                }

                throw new JsonException("a number is outside the range of 64-bit integers or finite floats.");
            case JsonTokenType.True:
                return DataNode.Scalar(DataValue.From(true));
            case JsonTokenType.False:
                return DataNode.Scalar(DataValue.From(false));
            case JsonTokenType.Null:
                return DataNode.Scalar(DataValue.Null);
            default:
                throw new JsonException($"unexpected token {reader.TokenType}.");
        }
    }

    private static Result<DataNode> Failure(string code, string message) =>
        Result<DataNode>.Failure(new CoreError(code, message));
}
