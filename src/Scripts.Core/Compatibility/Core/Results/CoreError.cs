// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Results;

/// <summary>
/// Describes a business failure with a stable machine-readable <see cref="Code"/> and an
/// English <see cref="Message"/>.
/// </summary>
public sealed record CoreError
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CoreError"/> record.
    /// </summary>
    /// <param name="code">Stable, non-empty machine-readable identifier (for example <c>usage.unknown_command</c>).</param>
    /// <param name="message">Human-readable message; never localized.</param>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> or <paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="code"/> is empty or whitespace.</exception>
    public CoreError(string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(message);

        Code = code;
        Message = message;
    }

    /// <summary>
    /// Gets the stable machine-readable error code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the human-readable error message.
    /// </summary>
    public string Message { get; }
}
