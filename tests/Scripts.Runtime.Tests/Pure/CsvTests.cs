using static Scripts.Runtime.Tests.Pure.PureTestSupport;

namespace Scripts.Runtime.Tests.Pure;

/// <summary>Intrinsic `csv` module (RFC 4180): `parse`, `parse_row`, `encode_row`, `encode`.</summary>
public class CsvTests
{
    private static List<List<string>> ToRows(Value payload)
    {
        var rows = new List<List<string>>();
        foreach (var row in ((ListValue)payload).Items)
        {
            rows.Add([.. ToTexts(row)]);
        }

        return rows;
    }

    [Fact]
    public void Parse_handles_empty_simple_crlf_escaped_quotes_and_multiline_fields()
    {
        Assert.Empty(ToRows(OkPayload(Call("csv.parse", Text(string.Empty)))));

        var simple = ToRows(OkPayload(Call("csv.parse", Text("a,b,c\n1,2,3\n"))));
        Assert.Equal([["a", "b", "c"], ["1", "2", "3"]], simple);

        var crlf = ToRows(OkPayload(Call("csv.parse", Text("a,b\r\n\"c,d\",\"e\"\"f\"\r\n"))));
        Assert.Equal([["a", "b"], ["c,d", "e\"f"]], crlf);

        var multiline = ToRows(OkPayload(Call("csv.parse", Text("id,note\n1,\"line 1\nline 2\"\n2,\"a\r\nb\""))));
        Assert.Equal([["id", "note"], ["1", "line 1\nline 2"], ["2", "a\r\nb"]], multiline);

        var emptyFields = ToRows(OkPayload(Call("csv.parse", Text(",\"\",\n"))));
        Assert.Equal([["", "", ""]], emptyFields);
    }

    [Theory]
    [InlineData("\"unterminated")]
    [InlineData("a,\"unterminated")]
    [InlineData("\"abc\"x,b")]
    [InlineData("a\"b,c")]
    [InlineData("a,b\rc,d")]
    public void Parse_rejects_malformed_csv_with_invalid_argument(string text) =>
        AssertValueError(Call("csv.parse", Text(text)), "invalid_argument");

    [Fact]
    public void Parse_row_parses_single_row_and_rejects_unescaped_newlines()
    {
        Assert.Empty(ToTexts(OkPayload(Call("csv.parse_row", Text(string.Empty)))));
        Assert.Equal(
            ["a", "b,c", "d\"e", "line1\nline2"],
            ToTexts(OkPayload(Call("csv.parse_row", Text("a,\"b,c\",\"d\"\"e\",\"line1\nline2\"")))));

        AssertValueError(Call("csv.parse_row", Text("a,b\nc,d")), "invalid_argument");
        AssertValueError(Call("csv.parse_row", Text("a,b\r\n")), "invalid_argument");
        AssertValueError(Call("csv.parse_row", Text("\"abc")), "invalid_argument");
        AssertValueError(Call("csv.parse_row", Text("\"abc\"x")), "invalid_argument");
    }

    [Fact]
    public void Encode_row_and_encode_quote_only_when_required_and_round_trip()
    {
        Assert.Equal(
            Text("a,\"b,c\",\"d\"\"e\",\"f\ng\",\"h\ri\""),
            Call("csv.encode_row", List(Text("a"), Text("b,c"), Text("d\"e"), Text("f\ng"), Text("h\ri"))));

        Assert.Equal(Text(string.Empty), Call("csv.encode", List()));

        var rows = List(
            List(Text("name"), Text("bio")),
            List(Text("Ada"), Text("likes, \"quotes\"\nand lines")));
        var encoded = ((StringValue)Call("csv.encode", rows)).Text;
        Assert.Equal("name,bio\nAda,\"likes, \"\"quotes\"\"\nand lines\"", encoded);
        Assert.DoesNotContain('\r', encoded);

        var decoded = ToRows(OkPayload(Call("csv.parse", Text(encoded))));
        Assert.Equal([["name", "bio"], ["Ada", "likes, \"quotes\"\nand lines"]], decoded);
    }
}
