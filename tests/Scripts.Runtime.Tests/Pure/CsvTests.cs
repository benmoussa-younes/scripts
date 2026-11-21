using System.Text;
using static Scripts.Runtime.Tests.Pure.PureTestSupport;

namespace Scripts.Runtime.Tests.Pure;

/// <summary>Intrinsic `csv` module (RFC 4180): `parse`, `parse_row`, `encode_row`, `encode`.</summary>
public class CsvTests
{
    // Characters whose CSV meaning differs from their text (separator, quote, line breaks, escape) plus
    // non-ASCII and a NUL; a deterministic generator keeps the campaign reproducible from its seed.
    private static readonly string[] FieldAlphabet =
        ["a", "b", "Z", "0", "9", " ", ",", "\"", "\n", "\r", "\t", "%", "#", ";", "é", "\U0001F600", "\0"];

    private static string RandomText(SeededRandom random, int maximumLength)
    {
        var builder = new StringBuilder();
        var length = random.Next(maximumLength);
        for (var index = 0; index < length; index++)
        {
            builder.Append(random.NextItem(FieldAlphabet));
        }

        return builder.ToString();
    }

    private static List<List<string>> RandomRows(SeededRandom random)
    {
        var rows = new List<List<string>>();
        var rowCount = 1 + random.Next(4);
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var row = new List<string>();
            var fieldCount = 1 + random.Next(4);
            for (var fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
            {
                row.Add(RandomText(random, 6));
            }

            rows.Add(row);
        }

        return rows;
    }

    private static ListValue RowsValue(List<List<string>> rows) =>
        new([.. rows.Select(row => (Value)new ListValue([.. row.Select(field => (Value)new StringValue(field))]))]);

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

    [Fact]
    public void A_lone_empty_field_is_quoted_so_the_row_is_not_empty_text()
    {
        Assert.Equal(Text("\"\""), Call("csv.encode_row", List(Text(string.Empty))));
        Assert.Equal(Text("\"\""), Call("csv.encode", List(List(Text(string.Empty)))));
        Assert.Equal([string.Empty], ToTexts(OkPayload(Call("csv.parse_row", Text("\"\"")))));
        Assert.Equal([[string.Empty]], ToRows(OkPayload(Call("csv.parse", Text("\"\"")))));
    }

    [Fact]
    public void A_row_without_a_field_does_not_survive_the_documented_round_trip()
    {
        Assert.Equal(Text(string.Empty), Call("csv.encode", List(List())));
        Assert.Empty(ToRows(OkPayload(Call("csv.parse", Text(string.Empty)))));
    }

    [Fact]
    public void Encode_then_parse_preserves_every_row_with_a_field()
    {
        var random = new SeededRandom(20261121);
        for (var iteration = 0; iteration < 300; iteration++)
        {
            var rows = RandomRows(random);
            var encoded = ((StringValue)Call("csv.encode", RowsValue(rows))).Text;
            Assert.Equal(rows, ToRows(OkPayload(Call("csv.parse", Text(encoded)))));
        }
    }

    [Fact]
    public void Encode_row_then_parse_row_preserves_every_field_list_with_a_field()
    {
        var random = new SeededRandom(77041);
        for (var iteration = 0; iteration < 300; iteration++)
        {
            var row = RandomRows(random)[0];
            var encoded = ((StringValue)Call("csv.encode_row", TextList([.. row]))).Text;
            Assert.Equal<string>(row, ToTexts(OkPayload(Call("csv.parse_row", Text(encoded)))));
        }
    }

    [Fact]
    public void Parse_then_encode_then_parse_is_stable_on_random_text()
    {
        var random = new SeededRandom(424242);
        for (var iteration = 0; iteration < 300; iteration++)
        {
            var parsed = Call("csv.parse", Text(RandomText(random, 12)));
            if (!((ResultValue)parsed).IsOk)
            {
                AssertValueError(parsed, "invalid_argument");
                continue;
            }

            var rows = ToRows(OkPayload(parsed));
            var encoded = ((StringValue)Call("csv.encode", RowsValue(rows))).Text;
            Assert.Equal(rows, ToRows(OkPayload(Call("csv.parse", Text(encoded)))));
        }
    }
}
