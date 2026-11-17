using static Scripts.Runtime.Tests.Pure.PureTestSupport;

namespace Scripts.Runtime.Tests.Pure;

/// <summary>`encoding` (DESIGN §1.5): RFC 4648 base64 vectors, lowercase hex vectors, strict decodes.</summary>
public class EncodingTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "Zg==")]
    [InlineData("fo", "Zm8=")]
    [InlineData("foo", "Zm9v")]
    [InlineData("foob", "Zm9vYg==")]
    [InlineData("fooba", "Zm9vYmE=")]
    [InlineData("foobar", "Zm9vYmFy")]
    [InlineData("café", "Y2Fmw6k=")]
    [InlineData("😀", "8J+YgA==")]
    public void Base64_encode_uses_the_RFC_4648_alphabet_with_padding(string text, string expected) =>
        Assert.Equal(Text(expected), Call("encoding.base64_encode", Text(text)));

    [Theory]
    [InlineData("", "")]
    [InlineData("Zg==", "f")]
    [InlineData("Zm8=", "fo")]
    [InlineData("Zm9v", "foo")]
    [InlineData("Zm9vYg==", "foob")]
    [InlineData("Zm9vYmE=", "fooba")]
    [InlineData("Zm9vYmFy", "foobar")]
    [InlineData("Y2Fmw6k=", "café")]
    [InlineData("8J+YgA==", "😀")]
    public void Base64_decode_returns_the_RFC_4648_plaintext(string text, string expected) =>
        Assert.Equal(expected, OkText(Call("encoding.base64_decode", Text(text))));

    [Theory]
    [InlineData("a")]
    [InlineData("abc")]
    [InlineData("aGVsbG8")]
    [InlineData("aGVs bG8=")]
    [InlineData("aGVsbG8_=")]
    [InlineData("====")]
    [InlineData("A===")]
    [InlineData("Zh==")]
    [InlineData("Zm9=")]
    public void Base64_decode_rejects_malformed_and_non_canonical_input(string text) =>
        AssertValueError(Call("encoding.base64_decode", Text(text)), "invalid_argument");

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "66")]
    [InlineData("fo", "666f")]
    [InlineData("foo", "666f6f")]
    [InlineData("foob", "666f6f62")]
    [InlineData("fooba", "666f6f6261")]
    [InlineData("foobar", "666f6f626172")]
    [InlineData("A", "41")]
    [InlineData("café", "636166c3a9")]
    [InlineData("😀", "f09f9880")]
    public void Hex_encode_uses_lowercase_two_digits_per_utf8_byte(string text, string expected) =>
        Assert.Equal(Text(expected), Call("encoding.hex_encode", Text(text)));

    [Theory]
    [InlineData("", "")]
    [InlineData("41", "A")]
    [InlineData("4a", "J")]
    [InlineData("4A", "J")]
    [InlineData("666f6f626172", "foobar")]
    [InlineData("636166c3a9", "café")]
    public void Hex_decode_accepts_upper_and_lower_case(string text, string expected) =>
        Assert.Equal(expected, OkText(Call("encoding.hex_decode", Text(text))));

    [Theory]
    [InlineData("4")]
    [InlineData("abc")]
    [InlineData("0g")]
    [InlineData("zz")]
    [InlineData("0 1")]
    public void Hex_decode_rejects_odd_length_and_non_hexadecimal_digits(string text) =>
        AssertValueError(Call("encoding.hex_decode", Text(text)), "invalid_argument");

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("café")]
    [InlineData("😀")]
    [InlineData("a\0b")]
    public void Base64_and_hex_decode_round_trip_utf8_text(string text)
    {
        Assert.Equal(text, OkText(Call("encoding.base64_decode", Call("encoding.base64_encode", Text(text)))));
        Assert.Equal(text, OkText(Call("encoding.hex_decode", Call("encoding.hex_encode", Text(text)))));
    }

    [Theory]
    [InlineData("/w==")]
    [InlineData("//8=")]
    public void Base64_decode_rejects_bytes_that_are_not_utf8(string text) =>
        AssertValueError(Call("encoding.base64_decode", Text(text)), "invalid_encoding");

    [Theory]
    [InlineData("ff")]
    [InlineData("c0af")]
    [InlineData("eda080")]
    public void Hex_decode_rejects_bytes_that_are_not_utf8(string text) =>
        AssertValueError(Call("encoding.hex_decode", Text(text)), "invalid_encoding");

    [Theory]
    [InlineData("encoding.base64_decode", "aGVsbG8")]
    [InlineData("encoding.base64_decode", "/w==")]
    [InlineData("encoding.hex_decode", "zz")]
    [InlineData("encoding.hex_decode", "ff")]
    public void Decode_errors_do_not_echo_the_input(string function, string text)
    {
        var error = ErrorRecord(Call(function, Text(text)));
        Assert.Equal(string.Empty, error.GetText("subject"));
    }
}
