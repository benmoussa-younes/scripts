// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Serialization.Values;

/// <summary>
/// The kind of a <see cref="DataValue"/>, a column of a
/// <see cref="Tables.DataTable"/> or a field of a
/// <see cref="Tables.DataSchema"/>.
/// </summary>
/// <remarks>
/// <see cref="Null"/> is the empty value and the <see cref="DataValue"/>
/// default; a column never has the <see cref="Null"/> kind, and
/// <see cref="Node"/> is the escape hatch for data that is not tabular.
/// </remarks>
public enum DataKind
{
    /// <summary>No value, or an untyped field.</summary>
    Null,

    /// <summary>A boolean.</summary>
    Boolean,

    /// <summary>A signed 64-bit integer.</summary>
    Integer,

    /// <summary>An IEEE 754 double-precision number.</summary>
    Number,

    /// <summary>An exact decimal number.</summary>
    Decimal,

    /// <summary>Text.</summary>
    Text,

    /// <summary>A calendar date without a time or time zone.</summary>
    Date,

    /// <summary>A time of day without a date or time zone.</summary>
    Time,

    /// <summary>An instant, preserving <see cref="System.DateTime.Kind"/>.</summary>
    Timestamp,

    /// <summary>A nested <see cref="DataNode"/>.</summary>
    Node,
}
