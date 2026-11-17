// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Globalization;

namespace Core.Serialization.Values;

/// <summary>
/// One transient data value: a small discriminated union over the scalar kinds a
/// <see cref="Tables.DataTable"/> column can hold, plus a <see cref="DataNode"/>
/// escape hatch for data that is not tabular. Storage is columnar, so a value is
/// never the storage unit.
/// </summary>
/// <remarks>
/// The layout is 24 bytes: one <see cref="long"/> for the value, one object
/// reference and the kind. Boolean, integer, number (as its bits), date, time and
/// timestamp live in the <see cref="long"/>; text, node and the rare decimal are
/// the reference. A timestamp packs its <see cref="DateTime.Kind"/> into the top
/// two bits above the ticks, because <see cref="DateTime.MaxValue"/>.Ticks needs
/// fewer than sixty-two bits. Keeping the struct to three machine words keeps a
/// columnar buffer cache-friendly.
/// </remarks>
public readonly struct DataValue : IEquatable<DataValue>
{
    private const long TimestampTicksMask = 0x3FFF_FFFF_FFFF_FFFFL;

    private readonly long _bits;
    private readonly object? _reference;
    private readonly DataKind _kind;

    private DataValue(DataKind kind, long bits, object? reference)
    {
        _kind = kind;
        _bits = bits;
        _reference = reference;
    }

    /// <summary>The empty value.</summary>
    public static DataValue Null => default;

    /// <summary>The kind of this value.</summary>
    public DataKind Kind => _kind;

    /// <summary>Whether this value is empty.</summary>
    public bool IsNull => _kind == DataKind.Null;

    /// <summary>Creates a boolean value.</summary>
    /// <param name="value">The boolean.</param>
    /// <returns>The value.</returns>
    public static DataValue From(bool value) => new(DataKind.Boolean, value ? 1L : 0L, null);

    /// <summary>Creates a 64-bit integer value.</summary>
    /// <param name="value">The integer.</param>
    /// <returns>The value.</returns>
    public static DataValue From(long value) => new(DataKind.Integer, value, null);

    /// <summary>Creates a double-precision number value.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The value.</returns>
    public static DataValue From(double value) => new(DataKind.Number, BitConverter.DoubleToInt64Bits(value), null);

    /// <summary>Creates an exact decimal value.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The value.</returns>
    public static DataValue From(decimal value) => new(DataKind.Decimal, 0L, value);

    /// <summary>Creates a text value.</summary>
    /// <param name="value">The text; must not be <see langword="null"/>.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static DataValue From(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new DataValue(DataKind.Text, 0L, value);
    }

    /// <summary>Creates a calendar-date value.</summary>
    /// <param name="value">The date.</param>
    /// <returns>The value.</returns>
    public static DataValue From(DateOnly value) => new(DataKind.Date, value.DayNumber, null);

    /// <summary>Creates a time-of-day value.</summary>
    /// <param name="value">The time.</param>
    /// <returns>The value.</returns>
    public static DataValue From(TimeOnly value) => new(DataKind.Time, value.Ticks, null);

    /// <summary>Creates a timestamp value, preserving the <see cref="DateTime.Kind"/>.</summary>
    /// <param name="value">The instant.</param>
    /// <returns>The value.</returns>
    public static DataValue From(DateTime value) =>
        new(DataKind.Timestamp, (long)((ulong)(uint)value.Kind << 62) | value.Ticks, null);

    /// <summary>Creates a value holding a nested node.</summary>
    /// <param name="value">The node; must not be <see langword="null"/>.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static DataValue FromNode(DataNode value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new DataValue(DataKind.Node, 0L, value);
    }

    /// <summary>Tries to read the value as a boolean.</summary>
    /// <param name="value">The boolean when this succeeds; otherwise <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the value is a boolean.</returns>
    public bool TryGetBoolean(out bool value)
    {
        value = _bits != 0;
        return _kind == DataKind.Boolean;
    }

    /// <summary>Tries to read the value as a 64-bit integer.</summary>
    /// <param name="value">The integer when this succeeds; otherwise zero.</param>
    /// <returns><see langword="true"/> when the value is an integer.</returns>
    public bool TryGetInteger(out long value)
    {
        value = _bits;
        return _kind == DataKind.Integer;
    }

    /// <summary>Tries to read the value as a double-precision number.</summary>
    /// <param name="value">The number when this succeeds; otherwise zero.</param>
    /// <returns><see langword="true"/> when the value is a number or an integer.</returns>
    public bool TryGetNumber(out double value)
    {
        switch (_kind)
        {
            case DataKind.Number:
                value = BitConverter.Int64BitsToDouble(_bits);
                return true;
            case DataKind.Integer:
                value = _bits;
                return true;
            default:
                value = 0d;
                return false;
        }
    }

    /// <summary>Tries to read the value as an exact decimal number.</summary>
    /// <param name="value">The number when this succeeds; otherwise zero.</param>
    /// <returns><see langword="true"/> when the value is a decimal or an integer.</returns>
    public bool TryGetDecimal(out decimal value)
    {
        switch (_kind)
        {
            case DataKind.Decimal:
                value = (decimal)_reference!;
                return true;
            case DataKind.Integer:
                value = _bits;
                return true;
            default:
                value = 0m;
                return false;
        }
    }

    /// <summary>Tries to read the value as text.</summary>
    /// <param name="value">The text when this succeeds; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the value is text.</returns>
    public bool TryGetText(out string? value)
    {
        value = _reference as string;
        return _kind == DataKind.Text;
    }

    /// <summary>Tries to read the value as a calendar date.</summary>
    /// <param name="value">The date when this succeeds; otherwise the default date.</param>
    /// <returns><see langword="true"/> when the value is a date.</returns>
    public bool TryGetDate(out DateOnly value)
    {
        if (_kind == DataKind.Date)
        {
            value = DateOnly.FromDayNumber((int)_bits);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Tries to read the value as a time of day.</summary>
    /// <param name="value">The time when this succeeds; otherwise the default time.</param>
    /// <returns><see langword="true"/> when the value is a time.</returns>
    public bool TryGetTime(out TimeOnly value)
    {
        if (_kind == DataKind.Time)
        {
            value = new TimeOnly(_bits);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Tries to read the value as an instant.</summary>
    /// <param name="value">The instant when this succeeds; otherwise the default instant.</param>
    /// <returns><see langword="true"/> when the value is a timestamp.</returns>
    public bool TryGetTimestamp(out DateTime value)
    {
        if (_kind == DataKind.Timestamp)
        {
            value = new DateTime(_bits & TimestampTicksMask, (DateTimeKind)((ulong)_bits >> 62));
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Tries to read the value as a nested node.</summary>
    /// <param name="value">The node when this succeeds; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the value is a node.</returns>
    public bool TryGetNode(out DataNode? value)
    {
        value = _reference as DataNode;
        return _kind == DataKind.Node;
    }

    /// <summary>Formats the value the way a text-based writer emits it: invariant, no decoration.</summary>
    /// <returns>The text form; empty for <see cref="Null"/>.</returns>
    /// <exception cref="InvalidOperationException">The value is a nested node, which has no single text form.</exception>
    public string ToInvariantText()
    {
        string text = string.Empty;

        switch (_kind)
        {
            case DataKind.Boolean:
                text = _bits != 0 ? "true" : "false";
                break;
            case DataKind.Integer:
                text = _bits.ToString(CultureInfo.InvariantCulture);
                break;
            case DataKind.Number:
                text = BitConverter.Int64BitsToDouble(_bits).ToString("R", CultureInfo.InvariantCulture);
                break;
            case DataKind.Decimal:
                text = ((decimal)_reference!).ToString(CultureInfo.InvariantCulture);
                break;
            case DataKind.Text:
                text = _reference as string ?? string.Empty;
                break;
            case DataKind.Date:
                text = DateOnly.FromDayNumber((int)_bits).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;
            case DataKind.Time:
                text = new TimeOnly(_bits).ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
                break;
            case DataKind.Timestamp:
                text = new DateTime(_bits & TimestampTicksMask, (DateTimeKind)((ulong)_bits >> 62))
                    .ToString("O", CultureInfo.InvariantCulture);
                break;
            case DataKind.Node:
                throw new InvalidOperationException("A nested node has no single invariant text form.");
        }

        return text;
    }

    /// <inheritdoc />
    public bool Equals(DataValue other)
    {
        bool equals = false;

        switch (_kind)
        {
            case DataKind.Null:
                equals = other._kind == DataKind.Null;
                break;
            case DataKind.Boolean:
            case DataKind.Integer:
            case DataKind.Date:
            case DataKind.Time:
            case DataKind.Timestamp:
                equals = other._kind == _kind && _bits == other._bits;
                break;
            case DataKind.Number:
                equals = other._kind == DataKind.Number && _bits == other._bits;
                break;
            case DataKind.Decimal:
                equals = other._kind == DataKind.Decimal && (decimal)_reference! == (decimal)other._reference!;
                break;
            case DataKind.Text:
                equals = other._kind == DataKind.Text && string.Equals(_reference as string, other._reference as string, StringComparison.Ordinal);
                break;
            case DataKind.Node:
                equals = other._kind == DataKind.Node && ReferenceEquals(_reference, other._reference);
                break;
        }

        return equals;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DataValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        int hash = 0;

        switch (_kind)
        {
            case DataKind.Null:
                hash = 0;
                break;
            case DataKind.Boolean:
            case DataKind.Integer:
            case DataKind.Number:
            case DataKind.Date:
            case DataKind.Time:
            case DataKind.Timestamp:
                hash = HashCode.Combine(_kind, _bits);
                break;
            case DataKind.Decimal:
                hash = HashCode.Combine(_kind, (decimal)_reference!);
                break;
            case DataKind.Text:
                hash = HashCode.Combine(_kind, _reference as string);
                break;
            case DataKind.Node:
                hash = HashCode.Combine(_kind, _reference);
                break;
        }

        return hash;
    }

    /// <summary>Compares two values.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public static bool operator ==(DataValue left, DataValue right) => left.Equals(right);

    /// <summary>Compares two values.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    public static bool operator !=(DataValue left, DataValue right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => _kind == DataKind.Node ? "Node" : ToInvariantText();
}
