using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Scripts.Runtime.Stdlib;

/// <summary>
/// Intrinsic namespace `time` (SPEC-STDLIB §7.10d): the wall clock, the monotonic clock and a sleep that observes
/// the run's interruption. The live clocks, sleep and the local formatter are flagged deterministic=false (DESIGN §0.6).
/// </summary>
internal static class TimeFunctions
{
    /// <summary>
    /// <see cref="WaitHandle.WaitAny(WaitHandle[], TimeSpan)"/> takes a millisecond count that must fit in an
    /// <see cref="int"/>; a longer sleep is split into chunks of at most this size and re-evaluated.
    /// </summary>
    private const long MaximumWaitChunkMilliseconds = int.MaxValue - 1;

    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["time.unix_seconds"] = (_, _) => IntegerValue.From(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        table["time.monotonic_milliseconds"] = (_, _) => IntegerValue.From(MonotonicMilliseconds());
        table["time.sleep_milliseconds"] = SleepMilliseconds;
        table["time.to_utc_iso8601"] = ToUtcIso8601;
        table["time.to_local_iso8601"] = ToLocalIso8601;
        table["time.parse_iso8601"] = ParseIso8601;
    }

    private static Value ToUtcIso8601(RuntimeContext context, Value[] arguments)
    {
        var seconds = SystemValues.IntegerAt(arguments, 0);
        try
        {
            var instant = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return ResultValue.Ok(new StringValue(instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
        }
        catch (ArgumentOutOfRangeException)
        {
            return ResultValue.Error(ErrorValues.Error("out_of_range", SystemValues.Invariant(seconds), "the timestamp is outside the representable range"));
        }
    }

    private static Value ToLocalIso8601(RuntimeContext context, Value[] arguments)
    {
        var seconds = SystemValues.IntegerAt(arguments, 0);
        try
        {
            var instant = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
            return ResultValue.Ok(new StringValue(instant.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)));
        }
        catch (ArgumentOutOfRangeException)
        {
            return ResultValue.Error(ErrorValues.Error("out_of_range", SystemValues.Invariant(seconds), "the timestamp is outside the representable range"));
        }
        catch (TimeZoneNotFoundException)
        {
            return ResultValue.Error(ErrorValues.Error("other", string.Empty, "the local time zone is unavailable"));
        }
        catch (InvalidTimeZoneException)
        {
            return ResultValue.Error(ErrorValues.Error("other", string.Empty, "the local time zone is invalid"));
        }
    }

    /// <summary>
    /// Strict ISO 8601 shape: date, time, an optional fractional part of one to seven digits, and an
    /// explicit `Z` or `±HH:MM` offset. `DateTimeOffset` alone accepts `+0100` and `+1:00`.
    /// </summary>
    private static readonly Regex Iso8601Shape = new(
        @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})\z",
        RegexOptions.CultureInvariant);

    private static Value ParseIso8601(RuntimeContext context, Value[] arguments)
    {
        var text = SystemValues.TextAt(arguments, 0);
        var shape = Iso8601Shape.Match(text);
        if (!shape.Success)
        {
            return ResultValue.Error(ErrorValues.Error("invalid_argument", text, "expected an ISO 8601 timestamp with Z or ±HH:MM"));
        }

        // `DateTimeOffset` rejects an offset beyond ±14:00; say so instead of blaming the shape.
        var offset = shape.Groups[2].Value;
        if (offset != "Z")
        {
            var offsetHours = ((offset[1] - '0') * 10) + (offset[2] - '0');
            var offsetMinutes = ((offset[4] - '0') * 10) + (offset[5] - '0');
            if (offsetHours > 14 || (offsetHours == 14 && offsetMinutes != 0))
            {
                return ResultValue.Error(ErrorValues.Error("invalid_argument", text, "the offset must be within ±14:00"));
            }
        }

        string[] formats =
        [
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        ];
        if (!DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return ResultValue.Error(ErrorValues.Error("invalid_argument", text, "expected an ISO 8601 timestamp with Z or ±HH:MM"));
        }

        return ResultValue.Ok(IntegerValue.From(parsed.ToUnixTimeSeconds()));
    }

    private static long MonotonicMilliseconds() =>
        ScaleToMilliseconds(Stopwatch.GetTimestamp(), Stopwatch.Frequency);

    /// <summary>
    /// Converts a stopwatch timestamp to whole milliseconds (floored). The raw tick count is never multiplied by
    /// 1000: on a 1 GHz clock that overflows after about 292 years. The quotient and the remainder are scaled apart
    /// instead, which also keeps the result non-decreasing in the timestamp.
    /// </summary>
    internal static long ScaleToMilliseconds(long timestamp, long frequency)
    {
        var seconds = timestamp / frequency;
        var remainder = timestamp % frequency;
        return (seconds * 1000) + (remainder * 1000 / frequency);
    }

    /// <summary>
    /// time.sleep_milliseconds: a negative duration is invalid_argument before any wait, 0 returns immediately,
    /// otherwise waits at least the duration. Interruption throws <see cref="InterruptionTermination"/> (never
    /// swallowed) so the run ends through SCRIPTS-RUNTIME-0018.
    /// </summary>
    private static Value SleepMilliseconds(RuntimeContext context, Value[] arguments)
    {
        var durationMilliseconds = SystemValues.IntegerAt(arguments, 0);
        if (durationMilliseconds < 0)
        {
            return ResultValue.Error(ErrorValues.Error(
                "invalid_argument",
                SystemValues.Invariant(durationMilliseconds),
                "the duration must not be negative"));
        }

        if (durationMilliseconds == 0)
        {
            return SystemValues.Success();
        }

        Sleep(context, durationMilliseconds);
        return SystemValues.Success();
    }

    private static void Sleep(RuntimeContext context, long durationMilliseconds)
    {
        context.ThrowIfInterrupted();
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            // A signal that woke the wait is raised here, before any further sleep.
            context.ThrowIfInterrupted();

            var elapsedMilliseconds = ScaleToMilliseconds(Stopwatch.GetTimestamp() - started, Stopwatch.Frequency);
            var remainingMilliseconds = durationMilliseconds - elapsedMilliseconds;
            if (remainingMilliseconds <= 0)
            {
                return;
            }

            // The chunk stays within int.MaxValue - 1 ms, so neither TimeSpan nor WaitAny overflows even for
            // long.MaxValue: a huge duration is waited in bounded chunks and remains interruptible.
            var chunk = TimeSpan.FromMilliseconds(Math.Min(remainingMilliseconds, MaximumWaitChunkMilliseconds));
            if (context.Interruption.IsInterrupted)
            {
                // The interruption is pending but ThrowIfInterrupted() did not throw: the monitor is suspended
                // while deferred cleanup runs. Its wait handle is already signalled, so waiting on it would spin;
                // wait on the clock instead and let the cleanup action finish normally.
                Thread.Sleep(chunk);
                continue;
            }

            WaitHandle.WaitAny([context.Interruption.WaitHandle], chunk);
        }
    }
}
