// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Diagnostics.CodeAnalysis;

namespace Core.Results;

/// <summary>
/// Represents the outcome of an operation: a value on success or a <see cref="CoreError"/> on failure.
/// </summary>
/// <typeparam name="T">The type of the value produced on success.</typeparam>
public readonly struct Result<T>
{
    private readonly T? _value;
    private readonly CoreError? _error;

    private Result(T? value, CoreError? error)
    {
        _value = value;
        _error = error;
    }

    /// <summary>
    /// Gets a value indicating whether the operation succeeded.
    /// </summary>
    public bool IsSuccess => _error is null;

    /// <summary>
    /// Gets a value indicating whether the operation failed.
    /// </summary>
    public bool IsFailure => _error is not null;

    /// <summary>
    /// Gets the value produced on success.
    /// </summary>
    /// <exception cref="InvalidOperationException">The result is a failure.</exception>
    public T Value => _error is null
        ? _value!
        : throw new InvalidOperationException("A failed result has no value.");

    /// <summary>
    /// Gets the error produced on failure.
    /// </summary>
    /// <exception cref="InvalidOperationException">The result is a success.</exception>
    public CoreError Error => _error
        ?? throw new InvalidOperationException("A successful result has no error.");

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    /// <param name="value">The value produced on success.</param>
    /// <returns>A successful <see cref="Result{T}"/>.</returns>
    public static Result<T> Success(T value)
    {
        return new Result<T>(value, null);
    }

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    /// <param name="error">The error produced on failure.</param>
    /// <returns>A failed <see cref="Result{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static Result<T> Failure(CoreError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(default, error);
    }

    /// <summary>
    /// Attempts to get the value produced on success.
    /// </summary>
    /// <param name="value">The value when the result is a success; otherwise the default value.</param>
    /// <returns><see langword="true"/> when the result is a success; otherwise <see langword="false"/>.</returns>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        if (_error is null)
        {
            value = _value!;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Attempts to get the error produced on failure.
    /// </summary>
    /// <param name="error">The error when the result is a failure; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the result is a failure; otherwise <see langword="false"/>.</returns>
    public bool TryGetError([NotNullWhen(true)] out CoreError? error)
    {
        if (_error is not null)
        {
            error = _error;
            return true;
        }

        error = null;
        return false;
    }

    /// <summary>
    /// Projects the successful value with <paramref name="map"/>, propagating the error unchanged.
    /// </summary>
    /// <typeparam name="TResult">The projected value type.</typeparam>
    /// <param name="map">The projection applied to the successful value.</param>
    /// <returns>The projected result, or the original failure.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is <see langword="null"/>.</exception>
    public Result<TResult> Map<TResult>(Func<T, TResult> map)
    {
        ArgumentNullException.ThrowIfNull(map);

        return _error is null
            ? Result<TResult>.Success(map(_value!))
            : Result<TResult>.Failure(_error);
    }

    /// <summary>
    /// Projects the successful value with <paramref name="bind"/>, propagating the error unchanged.
    /// </summary>
    /// <typeparam name="TResult">The projected value type.</typeparam>
    /// <param name="bind">The projection applied to the successful value.</param>
    /// <returns>The projected result, or the original failure.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bind"/> is <see langword="null"/>.</exception>
    public Result<TResult> Bind<TResult>(Func<T, Result<TResult>> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);

        return _error is null
            ? bind(_value!)
            : Result<TResult>.Failure(_error);
    }

    /// <summary>
    /// Collapses the result into a single value.
    /// </summary>
    /// <typeparam name="TResult">The collapsed value type.</typeparam>
    /// <param name="onSuccess">The projection applied to the successful value.</param>
    /// <param name="onFailure">The projection applied to the error.</param>
    /// <returns>The collapsed value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="onSuccess"/> or <paramref name="onFailure"/> is <see langword="null"/>.</exception>
    public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<CoreError, TResult> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);

        return _error is null ? onSuccess(_value!) : onFailure(_error);
    }

    /// <summary>
    /// Returns the successful value, or <paramref name="fallback"/> on failure.
    /// </summary>
    /// <param name="fallback">The value returned on failure.</param>
    /// <returns>The successful value or <paramref name="fallback"/>.</returns>
    public T ValueOr(T fallback)
    {
        return _error is null ? _value! : fallback;
    }

    /// <summary>
    /// Returns the successful value, or the value produced by <paramref name="fallback"/> on failure.
    /// </summary>
    /// <param name="fallback">The projection applied to the error on failure.</param>
    /// <returns>The successful value or the fallback value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fallback"/> is <see langword="null"/>.</exception>
    public T ValueOr(Func<CoreError, T> fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);

        return _error is null ? _value! : fallback(_error);
    }
}
