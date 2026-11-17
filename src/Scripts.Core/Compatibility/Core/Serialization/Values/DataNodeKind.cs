// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Serialization.Values;

/// <summary>
/// The shape of a <see cref="DataNode"/>: a map, a list or a scalar.
/// </summary>
public enum DataNodeKind
{
    /// <summary>An ordered set of named properties.</summary>
    Map,

    /// <summary>An ordered sequence of nodes.</summary>
    List,

    /// <summary>One <see cref="DataValue"/>.</summary>
    Scalar,
}
