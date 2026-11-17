// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Core.Serialization.Codecs.Json;

/// <summary>
/// The string escaping mode of the JSON writer. <see cref="Utf8Raw"/> is the
/// Scripts mode: raw UTF-8 for non-ASCII and lowercase <c>\uXXXX</c> for controls,
/// exactly what <c>Scripts.Core.Json.JsonWriter</c> already emits.
/// </summary>
public readonly struct JsonEscaping
{
    /// <summary>Raw UTF-8, lowercase control escapes.</summary>
    public static JsonEscaping Utf8Raw => default;
}
