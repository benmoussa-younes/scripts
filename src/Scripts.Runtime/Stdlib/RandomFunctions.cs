using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using static Scripts.Runtime.ArgumentAccess;

namespace Scripts.Runtime;

/// <summary>
/// Intrinsic namespace `random` (DESIGN §1.5): bytes and integers from the operating system's CSPRNG. Every entry is
/// flagged deterministic=false; the integer draw is an unbiased rejection sample over 64 bits.
/// </summary>
internal static class RandomFunctions
{
    /// <summary>Largest accepted byte count (1 MiB), the same bound the catalogue documents.</summary>
    internal const long MaximumByteCount = 1024 * 1024;

    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["random.bytes_hex"] = BytesHex;
        table["random.bytes_base64"] = BytesBase64;
        table["random.integer_below"] = IntegerBelow;
    }

    private static Value BytesHex(RuntimeContext context, Value[] arguments)
    {
        var byteCount = Integer(arguments, 0);
        if (!TryByteCount(byteCount, out var count))
        {
            return InvalidByteCount(byteCount);
        }

        if (count == 0)
        {
            return ResultValue.Ok(new StringValue(string.Empty));
        }

        var bytes = new byte[count];
        RandomNumberGenerator.Fill(bytes);
        return ResultValue.Ok(new StringValue(Convert.ToHexStringLower(bytes)));
    }

    private static Value BytesBase64(RuntimeContext context, Value[] arguments)
    {
        var byteCount = Integer(arguments, 0);
        if (!TryByteCount(byteCount, out var count))
        {
            return InvalidByteCount(byteCount);
        }

        if (count == 0)
        {
            return ResultValue.Ok(new StringValue(string.Empty));
        }

        var bytes = new byte[count];
        RandomNumberGenerator.Fill(bytes);
        return ResultValue.Ok(new StringValue(Convert.ToBase64String(bytes)));
    }

    private static Value IntegerBelow(RuntimeContext context, Value[] arguments)
    {
        var upperBound = Integer(arguments, 0);
        if (upperBound < 1)
        {
            return ResultValue.Error(ErrorValues.Error(
                "invalid_argument",
                upperBound.ToString(CultureInfo.InvariantCulture),
                "upper_bound must be between 1 and 9223372036854775807"));
        }

        var bound = (ulong)upperBound;
        // 2^64 modulo `bound`: rejecting candidates below this threshold leaves a multiple of `bound` values.
        var threshold = unchecked(0UL - bound) % bound;
        Span<byte> buffer = stackalloc byte[8];
        while (true)
        {
            RandomNumberGenerator.Fill(buffer);
            var candidate = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
            if (candidate >= threshold)
            {
                return ResultValue.Ok(IntegerValue.From((long)(candidate % bound)));
            }
        }
    }

    private static bool TryByteCount(long byteCount, out int count)
    {
        count = 0;
        if (byteCount is < 0 or > MaximumByteCount)
        {
            return false;
        }

        count = (int)byteCount;
        return true;
    }

    private static Value InvalidByteCount(long byteCount) =>
        ResultValue.Error(ErrorValues.Error(
            "invalid_argument",
            byteCount.ToString(CultureInfo.InvariantCulture),
            "byte_count must be between 0 and 1048576"));
}
