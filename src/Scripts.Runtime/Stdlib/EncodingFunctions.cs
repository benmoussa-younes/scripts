using System.Text;
using static Scripts.Runtime.ArgumentAccess;

namespace Scripts.Runtime;

/// <summary>
/// Intrinsic namespace `encoding` (DESIGN §1.5): the base64 and hexadecimal text encodings. A string is UTF-8 text
/// and bytes are never values, so an encode takes the UTF-8 bytes and a decode returns a string only for strict UTF-8.
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
