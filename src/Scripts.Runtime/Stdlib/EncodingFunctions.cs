using System.Text;
using static Scripts.Runtime.ArgumentAccess;

namespace Scripts.Runtime;

/// <summary>
/// Intrinsic namespace `encoding` (DESIGN §1.5): base64, hexadecimal and RFC 3986 percent encodings. A string is UTF-8
/// text and bytes are never values, so an encode takes the UTF-8 bytes and a decode returns a string only for strict UTF-8.
/// </summary>
internal static class EncodingFunctions
{
    /// <summary>Strict UTF-8: no BOM handling, a malformed sequence throws instead of producing U+FFFD.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["encoding.base64_encode"] = Base64Encode;
        table["encoding.base64_decode"] = Base64Decode;
        table["encoding.hex_encode"] = HexEncode;
        table["encoding.hex_decode"] = HexDecode;
        table["encoding.url_encode"] = UrlEncode;
        table["encoding.url_decode"] = UrlDecode;
    }

    private static Value Base64Encode(RuntimeContext context, Value[] arguments)
    {
        var bytes = Encoding.UTF8.GetBytes(Text(arguments, 0));
        SizeLimits.EnsureStringLength(4L * ((bytes.Length + 2) / 3));
        return new StringValue(Convert.ToBase64String(bytes));
    }

    private static Value Base64Decode(RuntimeContext context, Value[] arguments)
    {
        var text = Text(arguments, 0);
        if (!TryDecodeBase64(text, out var bytes))
        {
            return ResultValue.Error(ErrorValues.Error(
                "invalid_argument",
                string.Empty,
                "expected standard base64 with padding and canonical trailing bits"));
        }

        return DecodeUtf8(bytes, string.Empty);
    }

    private static Value HexEncode(RuntimeContext context, Value[] arguments)
    {
        var bytes = Encoding.UTF8.GetBytes(Text(arguments, 0));
        SizeLimits.EnsureStringLength(2L * bytes.Length);
        return new StringValue(Convert.ToHexStringLower(bytes));
    }

    private static Value HexDecode(RuntimeContext context, Value[] arguments)
    {
        var text = Text(arguments, 0);
        if (!TryDecodeHex(text, out var bytes))
        {
            var reason = text.Length % 2 != 0
                ? "expected an even number of hexadecimal digits"
                : "expected hexadecimal digits, two per byte";
            return ResultValue.Error(ErrorValues.Error(
                "invalid_argument",
                string.Empty,
                reason));
        }

        return DecodeUtf8(bytes, string.Empty);
    }

    private static Value UrlEncode(RuntimeContext context, Value[] arguments)
    {
        const string UpperHex = "0123456789ABCDEF";
        var bytes = Encoding.UTF8.GetBytes(Text(arguments, 0));
        long length = 0;
        foreach (var b in bytes)
        {
            length += IsRfc3986Unreserved(b) ? 1 : 3;
        }

        SizeLimits.EnsureStringLength(length);
        var chars = new char[(int)length];
        var pos = 0;
        foreach (var b in bytes)
        {
            if (IsRfc3986Unreserved(b))
            {
                chars[pos++] = (char)b;
            }
            else
            {
                chars[pos++] = '%';
                chars[pos++] = UpperHex[b >> 4];
                chars[pos++] = UpperHex[b & 0xF];
            }
        }

        return new StringValue(new string(chars));
    }

    private static Value UrlDecode(RuntimeContext context, Value[] arguments)
    {
        var text = Text(arguments, 0);
        if (text.Length == 0)
        {
            return ResultValue.Ok(new StringValue(string.Empty));
        }

        using var buffer = new MemoryStream(text.Length);
        var position = 0;
        while (position < text.Length)
        {
            var percent = text.IndexOf('%', position);
            if (percent < 0)
            {
                WriteUtf8Span(buffer, text.AsSpan(position));
                break;
            }

            if (percent > position)
            {
                WriteUtf8Span(buffer, text.AsSpan(position, percent - position));
            }

            if (percent + 2 >= text.Length
                || !TryHexDigit(text[percent + 1], out var high)
                || !TryHexDigit(text[percent + 2], out var low))
            {
                return ResultValue.Error(ErrorValues.Error(
                    "invalid_argument",
                    string.Empty,
                    "expected a percent sign followed by two hexadecimal digits"));
            }

            buffer.WriteByte((byte)((high << 4) | low));
            position = percent + 3;
        }

        return DecodeUtf8(buffer.ToArray(), string.Empty);
    }

    private static bool IsRfc3986Unreserved(byte value) =>
        value is (>= (byte)'A' and <= (byte)'Z')
            or (>= (byte)'a' and <= (byte)'z')
            or (>= (byte)'0' and <= (byte)'9')
            or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~';

    private static void WriteUtf8Span(MemoryStream buffer, ReadOnlySpan<char> span)
    {
        var byteCount = Encoding.UTF8.GetByteCount(span);
        var encoded = new byte[byteCount];
        Encoding.UTF8.GetBytes(span, encoded);
        buffer.Write(encoded, 0, encoded.Length);
    }

    /// <summary>
    /// Standard base64 with mandatory padding, no whitespace, only the standard alphabet, and canonical trailing bits
    /// (the bytes must re-encode to exactly the input). <see cref="Convert.FromBase64String"/> then only performs the
    /// bit-level decoding.
    /// </summary>
    private static bool TryDecodeBase64(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length % 4 != 0)
        {
            return false;
        }

        var padding = 0;
        if (text.Length > 0 && text[^1] == '=')
        {
            padding = 1;
            if (text.Length > 1 && text[^2] == '=')
            {
                padding = 2;
            }
        }

        for (var position = 0; position < text.Length - padding; position++)
        {
            if (!IsBase64Character(text[position]))
            {
                return false;
            }
        }

        for (var position = text.Length - padding; position < text.Length; position++)
        {
            if (text[position] != '=')
            {
                return false;
            }
        }

        try
        {
            bytes = Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return false;
        }

        return string.Equals(Convert.ToBase64String(bytes), text, StringComparison.Ordinal);
    }

    private static bool IsBase64Character(char character) =>
        character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '+'
            or '/';

    /// <summary>Even length and hexadecimal digits only; both cases are accepted, the encoder writes lowercase.</summary>
    private static bool TryDecodeHex(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length % 2 != 0)
        {
            return false;
        }

        bytes = new byte[text.Length / 2];
        for (var position = 0; position < bytes.Length; position++)
        {
            if (!TryHexDigit(text[position * 2], out var high) || !TryHexDigit(text[(position * 2) + 1], out var low))
            {
                bytes = [];
                return false;
            }

            bytes[position] = (byte)((high << 4) | low);
        }

        return true;
    }

    private static bool TryHexDigit(char character, out int value)
    {
        switch (character)
        {
            case >= '0' and <= '9':
                value = character - '0';
                return true;
            case >= 'a' and <= 'f':
                value = character - 'a' + 10;
                return true;
            case >= 'A' and <= 'F':
                value = character - 'A' + 10;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static Value DecodeUtf8(byte[] bytes, string subject)
    {
        try
        {
            return ResultValue.Ok(new StringValue(StrictUtf8.GetString(bytes)));
        }
        catch (DecoderFallbackException)
        {
            return ResultValue.Error(ErrorValues.Error(
                "invalid_encoding",
                subject,
                "the decoded bytes are not valid UTF-8"));
        }
    }
}
