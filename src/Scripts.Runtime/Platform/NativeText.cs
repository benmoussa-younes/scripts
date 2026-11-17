using System.Text;

namespace Scripts.Runtime.Platform;

internal static class NativeText
{
    private static readonly UTF8Encoding LenientUtf8 = new(false, false);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] ToNullTerminated(string text)
    {
        var bytes = new byte[LenientUtf8.GetByteCount(text) + 1];
        LenientUtf8.GetBytes(text, bytes);
        return bytes;
    }

    /// <summary>Copies raw bytes (already a name) and appends the NUL terminator.</summary>
    public static byte[] ToNullTerminated(ReadOnlySpan<byte> bytes)
    {
        var result = new byte[bytes.Length + 1];
        bytes.CopyTo(result);
        return result;
    }

    public static byte[] ToBytes(string text) => LenientUtf8.GetBytes(text);

    public static string DecodeLenient(ReadOnlySpan<byte> bytes) => LenientUtf8.GetString(bytes);

    public static bool TryDecodeStrict(ReadOnlySpan<byte> bytes, out string text)
    {
        try
        {
            text = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    public static ReadOnlySpan<byte> UntilNul(ReadOnlySpan<byte> bytes)
    {
        var terminator = bytes.IndexOf((byte)0);
        return terminator < 0 ? bytes : bytes[..terminator];
    }
}

/// <summary>Lexical path helpers (the language never normalizes paths).</summary>
internal static class PathText
{
    public static bool ContainsNul(string text) => text.Contains('\0', StringComparison.Ordinal);

    /// <summary>Raw lexical join for internal callers: no segment validation (empty child → base, rooted child → child). The `/` operator's fail-closed guard is in ArithmeticOperations.JoinPath.</summary>
    public static string Join(string basePath, string childSegment)
    {
        if (childSegment.Length == 0)
        {
            return basePath;
        }

        if (childSegment[0] == '/' || basePath.Length == 0)
        {
            return childSegment;
        }

        return basePath[^1] == '/' ? basePath + childSegment : basePath + "/" + childSegment;
    }

    /// <summary>Removes trailing '/' characters, keeping a single "/" for a root path.</summary>
    public static string TrimTrailingSlashes(string path)
    {
        var end = path.Length;
        while (end > 1 && path[end - 1] == '/')
        {
            end--;
        }

        return path[..end];
    }

    public static string LastComponent(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }
}
