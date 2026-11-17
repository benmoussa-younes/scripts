// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Serialization.Values;

/// <summary>
/// One named property of a <see cref="DataNode"/> map: a name and its value
/// node, in source order.
/// </summary>
public readonly struct DataProperty : IEquatable<DataProperty>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DataProperty"/> struct.
    /// </summary>
    /// <param name="name">The property name; must not be <see langword="null"/>.</param>
    /// <param name="value">The value node; must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    public DataProperty(string name, DataNode value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        Name = name;
        Value = value;
    }

    /// <summary>Gets the property name.</summary>
    public string Name { get; }

    /// <summary>Gets the value node.</summary>
    public DataNode Value { get; }

    /// <inheritdoc />
    public bool Equals(DataProperty other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal) && ReferenceEquals(Value, other.Value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DataProperty other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Name, Value);

    /// <summary>Compares two properties.</summary>
    /// <param name="left">The first property.</param>
    /// <param name="right">The second property.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public static bool operator ==(DataProperty left, DataProperty right) => left.Equals(right);

    /// <summary>Compares two properties.</summary>
    /// <param name="left">The first property.</param>
    /// <param name="right">The second property.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    public static bool operator !=(DataProperty left, DataProperty right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => $"{Name}: {Value}";
}
