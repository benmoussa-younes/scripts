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

    [Theory]
    [InlineData("", "")]
    [InlineData("ABCxyz0129-_.~", "ABCxyz0129-_.~")]
    [InlineData("hello world", "hello%20world")]
    [InlineData("a+b=c&d/e?f#g%h", "a%2Bb%3Dc%26d%2Fe%3Ff%23g%25h")]
    [InlineData("café", "caf%C3%A9")]
    [InlineData("\U0001F600", "%F0%9F%98%80")]
    public void Url_encode_follows_rfc3986_with_uppercase_hex_and_never_uses_plus(string text, string expected) =>
        Assert.Equal(Text(expected), Call("encoding.url_encode", Text(text)));

    [Theory]
    [InlineData("", "")]
    [InlineData("hello%20world", "hello world")]
    [InlineData("a+b", "a+b")]
    [InlineData("caf%c3%a9", "café")]
    [InlineData("caf%C3%A9", "café")]
    [InlineData("%F0%9F%98%80", "\U0001F600")]
    [InlineData("%EF%BB%BFa", "\uFEFFa")]
    public void Url_decode_decodes_both_cases_keeps_plus_and_preserves_bom(string text, string expected) =>
        Assert.Equal(expected, OkText(Call("encoding.url_decode", Text(text))));

    [Theory]
    [InlineData("%")]
    [InlineData("%2")]
    [InlineData("abc%2")]
    [InlineData("%ZZ")]
    [InlineData("%2G")]
    public void Url_decode_rejects_malformed_percent_sequences(string text) =>
        AssertValueError(Call("encoding.url_decode", Text(text)), "invalid_argument");

    [Theory]
    [InlineData("%FF")]
    [InlineData("%C0%AF")]
    [InlineData("%ED%A0%80")]
    public void Url_decode_rejects_invalid_utf8_bytes(string text) =>
        AssertValueError(Call("encoding.url_decode", Text(text)), "invalid_encoding");
}
