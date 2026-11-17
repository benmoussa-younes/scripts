// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Serialization.Values;

/// <summary>
/// A recursive data node: a scalar, an ordered list of nodes, or an ordered map
/// of named properties. It is the escape hatch for data that does not fit a
/// columnar <see cref="Tables.DataTable"/> — nested JSON objects, TOML inline
/// tables, arrays of mixed shape — and is reached through a
/// <see cref="DataValue"/> of kind <see cref="DataKind.Node"/>.
/// </summary>
public sealed class DataNode
{
    private readonly DataValue _value;
    private readonly IReadOnlyList<DataNode>? _items;
    private readonly IReadOnlyList<DataProperty>? _properties;

    private DataNode(
        DataNodeKind kind,
        DataValue value,
        IReadOnlyList<DataNode>? items,
        IReadOnlyList<DataProperty>? properties)
    {
        Kind = kind;
        _value = value;
        _items = items;
        _properties = properties;
    }

    /// <summary>Gets the node shape.</summary>
    public DataNodeKind Kind { get; }

    /// <summary>Gets the scalar value.</summary>
    /// <exception cref="InvalidOperationException">The node is not a scalar.</exception>
    public DataValue Value => Kind == DataNodeKind.Scalar
        ? _value
        : throw new InvalidOperationException($"A {Kind} node has no scalar value.");

    /// <summary>Gets the list items.</summary>
    /// <exception cref="InvalidOperationException">The node is not a list.</exception>
    public IReadOnlyList<DataNode> Items => Kind == DataNodeKind.List
        ? _items!
        : throw new InvalidOperationException($"A {Kind} node has no items.");

    /// <summary>Gets the map properties, in source order.</summary>
    /// <exception cref="InvalidOperationException">The node is not a map.</exception>
    public IReadOnlyList<DataProperty> Properties => Kind == DataNodeKind.Map
        ? _properties!
        : throw new InvalidOperationException($"A {Kind} node has no properties.");

    /// <summary>Creates a scalar node.</summary>
    /// <param name="value">The scalar value.</param>
    /// <returns>The node.</returns>
    public static DataNode Scalar(DataValue value) => new(DataNodeKind.Scalar, value, null, null);

    /// <summary>Creates a list node.</summary>
    /// <param name="items">The items; must not be <see langword="null"/>.</param>
    /// <returns>The node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    public static DataNode List(IEnumerable<DataNode> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        return new DataNode(DataNodeKind.List, default, [.. items], null);
    }

    /// <summary>Creates a map node.</summary>
    /// <param name="properties">The properties, in order; must not be <see langword="null"/>.</param>
    /// <returns>The node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="properties"/> is <see langword="null"/>.</exception>
    public static DataNode Map(IEnumerable<DataProperty> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        return new DataNode(DataNodeKind.Map, default, null, [.. properties]);
    }

    /// <summary>Looks a property up by name; the first match wins.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="value">The value node when the property is present.</param>
    /// <returns><see langword="true"/> when the property is present.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public bool TryGetProperty(string name, out DataNode? value)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (Kind == DataNodeKind.Map)
        {
            foreach (var property in _properties!)
            {
                if (string.Equals(property.Name, name, StringComparison.Ordinal))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = null;
        return false;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        string text = string.Empty;

        switch (Kind)
        {
            case DataNodeKind.Scalar:
                text = _value.ToString();
                break;
            case DataNodeKind.List:
                text = $"List({_items!.Count})";
                break;
            case DataNodeKind.Map:
                text = $"Map({_properties!.Count})";
                break;
        }

        return text;
    }
}
