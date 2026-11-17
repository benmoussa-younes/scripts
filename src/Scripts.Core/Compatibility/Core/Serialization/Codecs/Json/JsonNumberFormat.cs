// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace Core.Serialization.Codecs.Json;

/// <summary>
/// The canonical float renderer used by the JSON writer: the caller supplies the
/// language's float text (<c>FloatFormatter.Render</c> in Scripts), so <c>json.encode</c>
/// is byte-identical to <c>run --json-out</c>.
/// </summary>
public readonly struct JsonNumberFormat
{
    private readonly Func<double, string>? _render;

    private JsonNumberFormat(Func<double, string> render) => _render = render;

    /// <summary>Creates a format from a renderer.</summary>
    public static JsonNumberFormat From(Func<double, string> render)
    {
        ArgumentNullException.ThrowIfNull(render);
        return new JsonNumberFormat(render);
    }

    /// <summary>Renders one finite double.</summary>
    public string Render(double value) =>
        _render is null ? value.ToString("R", CultureInfo.InvariantCulture) : _render(value);
}
