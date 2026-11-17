using Scripts.Core;

namespace Scripts.Syntax.Tests;

public sealed class SourceReadingTests
{
    private static SourceReadResult Read(params byte[] bytes) => SourceReading.Read("input.scs", bytes);

    private static byte[] Bytes(string text) => Parsing.Utf8(text);

    [Fact]
    public void Valid_utf8_is_decoded()
    {
        var result = Read(Bytes("let é = \"😀\"\n"));
        Assert.Null(result.Error);
        Assert.Equal("let é = \"😀\"\n", result.Source!.Text);
        Assert.Equal("input.scs", result.Source.Path);
    }

    [Fact]
    public void Empty_input_is_an_empty_source()
    {
        Assert.Equal(string.Empty, Read().Source!.Text);
        Assert.Empty(Parsing.Parse(Read().Source!.Text).Statements);
    }

    [Fact]
    public void A_byte_order_mark_is_skipped_and_not_counted_in_columns()
    {
        var result = Read([.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Bytes("let x = @")]);
        Assert.Equal("let x = @", result.Source!.Text);
        var parsed = Parser.Parse(result.Source, isModule: false);
        Assert.Equal((ErrorCodes.Parse0006, 1, 9), (parsed.Error!.Code, parsed.Error.Start.Line, parsed.Error.Start.Column));
    }

    [Fact]
    public void A_file_with_only_a_byte_order_mark_is_empty()
    {
        Assert.Equal(string.Empty, Read(0xEF, 0xBB, 0xBF).Source!.Text);
    }

    [Fact]
    public void A_byte_order_mark_followed_by_an_invalid_byte_reports_the_position_after_the_mark()
    {
        var error = Read(0xEF, 0xBB, 0xBF, (byte)'a', 0xFF).Error!;
        Assert.Equal((ErrorCodes.Parse0001, 1, 2), (error.Code, error.Start.Line, error.Start.Column));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF }, "FF", 1)]
    [InlineData(new byte[] { 0xC0, 0xAF }, "C0", 1)]
    [InlineData(new byte[] { 0xC3 }, "C3", 1)]
    [InlineData(new byte[] { 0xE2, 0x82 }, "E2", 1)]
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 }, "ED", 1)]
    [InlineData(new byte[] { 0xF5, 0x80, 0x80, 0x80 }, "F5", 1)]
    [InlineData(new byte[] { 0x80 }, "80", 1)]
    [InlineData(new byte[] { 0xF4, 0x90, 0x80, 0x80 }, "F4", 1)]
    [InlineData(new byte[] { (byte)'a', (byte)'b', 0xFE }, "FE", 3)]
    [InlineData(new byte[] { 0xC3, 0xA9, 0xFF }, "FF", 2)]
    [InlineData(new byte[] { 0xF0, 0x9F, 0x98, 0x80, 0xFF }, "FF", 2)]
    public void Invalid_utf8_is_reported_at_the_first_invalid_byte(byte[] bytes, string expectedByte, int expectedColumn)
    {
        var error = Read(bytes).Error!;
        Assert.Equal((ErrorCodes.Parse0001, 1, expectedColumn), (error.Code, error.Start.Line, error.Start.Column));
        Assert.Contains("0x" + expectedByte, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_utf8_position_counts_lines_and_columns()
    {
        var error = Read([.. Bytes("a\nbc\n  d"), 0xFF]).Error!;
        Assert.Equal((3, 4), (error.Start.Line, error.Start.Column));
    }

    [Fact]
    public void A_nul_character_is_rejected_at_its_position()
    {
        var error = Read([.. Bytes("a"), 0x00, .. Bytes("b")]).Error!;
        Assert.Equal((ErrorCodes.Parse0002, 1, 2), (error.Code, error.Start.Line, error.Start.Column));
    }

    [Fact]
    public void The_earlier_of_a_nul_and_an_invalid_byte_wins()
    {
        Assert.Equal(ErrorCodes.Parse0002, Read(0x00, 0xFF).Error!.Code);
        Assert.Equal(ErrorCodes.Parse0001, Read(0xFF, 0x00).Error!.Code);
    }

    [Fact]
    public void The_size_limit_is_four_mebibytes_inclusive()
    {
        var atLimit = new byte[Limits.MaximumSourceBytes];
        Array.Fill(atLimit, (byte)'#');
        Assert.Null(Read(atLimit).Error);
        var error = Read([.. atLimit, (byte)'#']).Error!;
        Assert.Equal((ErrorCodes.Parse0004, 1, 1), (error.Code, error.Start.Line, error.Start.Column));
        Assert.Contains("4194305", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_at_the_size_limit_parses()
    {
        var line = "let x = 1\n";
        var text = string.Concat(Enumerable.Repeat(line, Limits.MaximumSourceBytes / line.Length));
        var result = Read(Bytes(text));
        Assert.Null(result.Error);
        Assert.Equal(Limits.MaximumSourceBytes / line.Length, Parsing.Parse(result.Source!.Text).Statements.Count);
    }

    [Fact]
    public void Crlf_line_endings_are_preserved_and_positions_ignore_the_carriage_return()
    {
        var result = Read(Bytes("let x = 1\r\nlet y = @\r\n"));
        var error = Parser.Parse(result.Source!, isModule: false).Error!;
        Assert.Equal((2, 9), (error.Start.Line, error.Start.Column));
        Assert.Equal("let y = @", error.SourceLine);
    }

    [Fact]
    public void Supplementary_characters_count_as_one_column()
    {
        var error = Parser.Parse(Read(Bytes("let s = \"😀😀\" @")).Source!, isModule: false).Error!;
        Assert.Equal((1, 14), (error.Start.Line, error.Start.Column));
    }

    [Fact]
    public void Diagnostics_for_unreadable_sources_still_carry_a_file()
    {
        var error = Read(0xFF).Error!;
        Assert.Equal("input.scs", error.FilePath);
        Assert.Equal("input.scs", Read([.. new byte[Limits.MaximumSourceBytes], 0]).Error!.FilePath);
    }
}
