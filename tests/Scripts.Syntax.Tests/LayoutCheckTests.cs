namespace Scripts.Syntax.Tests;

/// <summary><c>scripts format --check</c> (P9c): the canonical layout rules, checked without rewriting tokens.</summary>
public sealed class LayoutCheckTests
{
    private const string Canonical = """"
        function twice(n: integer) -> integer {
            return n * 2
        }

        let mutable total = 0
        for n in [1, 2, 3] {
            total += twice(n)
        }
        let sum = [1, 2, 3]
            .transform(n => n * 2)
            .sum()
        let text = r"""
        line one
          line two
        """
        io.write_standard_output_line("{total} {sum} {text.length()}")
        """";

    [Fact]
    public void A_canonical_file_has_no_issue()
    {
        Assert.Empty(LayoutCheck.Check(Canonical + "\n"));
    }

    [Fact]
    public void Indentation_is_four_spaces_per_open_brace()
    {
        var issues = LayoutCheck.Check("function f() -> unit {\n   io.write_standard_output_line(\"x\")\n}\n");

        var issue = Assert.Single(issues);
        Assert.Equal((2, 1), (issue.Line, issue.Column));
        Assert.Equal("expected 4 spaces of indentation, found 3", issue.Message);
    }

    [Fact]
    public void Tabs_trailing_spaces_and_crlf_are_reported()
    {
        var issues = LayoutCheck.Check("let x = 1 \r\n\tlet y = 2\r\n");

        Assert.Contains(issues, issue => issue.Message == "trailing whitespace");
        Assert.Contains(issues, issue => issue.Message == "CRLF line ending: use LF");
        Assert.Contains(issues, issue => issue.Message == "tab in indentation: use spaces");
    }

    [Fact]
    public void A_tab_between_tokens_is_a_layout_violation()
    {
        var issue = Assert.Single(LayoutCheck.Check("let x =\t1\n"));

        Assert.Equal((1, 8, "tab: use spaces"), (issue.Line, issue.Column, issue.Message));
    }

    [Fact]
    public void A_tab_in_indentation_is_still_a_layout_violation()
    {
        var issues = LayoutCheck.Check("\tlet x = 1\n");

        Assert.Contains(issues, issue => issue.Message == "tab in indentation: use spaces");
    }

    [Fact]
    public void A_tab_inside_a_normal_string_is_data()
    {
        Assert.Empty(LayoutCheck.Check("let text = \"a\tb\"\n"));
    }

    [Fact]
    public void A_tab_inside_a_raw_string_is_data()
    {
        Assert.Empty(LayoutCheck.Check("let text = r\"a\tb\"\n"));
    }

    [Fact]
    public void A_tab_inside_a_path_literal_is_data()
    {
        Assert.Empty(LayoutCheck.Check("let target = p\"a\tb\"\n"));
    }

    [Fact]
    public void A_tab_inside_a_nested_string_in_an_interpolation_is_data()
    {
        Assert.Empty(LayoutCheck.Check("let text = \"{f(\"a\tb\")}\"\n"));
    }

    [Fact]
    public void A_tab_inside_a_nested_raw_string_in_an_interpolation_is_data()
    {
        Assert.Empty(LayoutCheck.Check("let text = \"{f(r\"a\tb\")}\"\n"));
    }

    [Fact]
    public void A_multi_line_raw_string_nested_in_an_interpolation_is_content()
    {
        const string source = "function identity(value: string) -> string {\n    return value\n}\n\nlet text = \"{identity(r\"\"\"  \r\nline with spaces  \r\n\t\"\"\")}\"\nio.write_standard_output_line(\"{text.length()}\")\n";

        Assert.Empty(LayoutCheck.Check(source));
    }

    [Fact]
    public void Trailing_spaces_inside_a_raw_string_are_content()
    {
        const string source = "let text = r\"\"\"\nline with spaces  \n\"\"\"\nio.write_standard_output_line(\"{text.length()}\")\n";

        Assert.Empty(LayoutCheck.Check(source));
    }

    [Fact]
    public void Trailing_spaces_on_a_raw_string_opening_line_are_content()
    {
        const string source = "let text = r\"\"\"  \nline\n\"\"\"\nio.write_standard_output_line(\"{text.length()}\")\n";

        Assert.Empty(LayoutCheck.Check(source));
    }

    [Fact]
    public void A_crlf_inside_a_raw_string_is_content()
    {
        const string source = "let text = r\"\"\"\r\nline one\r\n\"\"\"\nio.write_standard_output_line(\"{text.length()}\")\n";

        Assert.Empty(LayoutCheck.Check(source));
    }

    [Fact]
    public void An_indented_raw_string_closing_line_keeps_its_leading_whitespace()
    {
        const string source = "let text = r\"\"\"\nline one\n    \"\"\"\nio.write_standard_output_line(\"{text.length()}\")\n";

        Assert.Empty(LayoutCheck.Check(source));
    }

    [Fact]
    public void A_tab_in_an_indented_raw_string_closing_line_is_content()
    {
        const string source = "let text = r\"\"\"\nline one\n\t\"\"\"\nio.write_standard_output_line(\"{text.length()}\")\n";

        Assert.Empty(LayoutCheck.Check(source));
    }

    [Fact]
    public void Trailing_space_after_an_indented_raw_string_closing_quotes_is_layout()
    {
        const string source = "let text = r\"\"\"\nline one\n    \"\"\" \nio.write_standard_output_line(\"{text.length()}\")\n";

        var issue = Assert.Single(LayoutCheck.Check(source));
        Assert.Equal((3, 8, "trailing whitespace"), (issue.Line, issue.Column, issue.Message));
    }

    [Fact]
    public void A_crlf_after_the_raw_string_closing_quotes_is_layout()
    {
        const string source = "let text = r\"\"\"\nline one\n\"\"\"\r\nio.write_standard_output_line(\"{text.length()}\")\n";

        var issue = Assert.Single(LayoutCheck.Check(source));
        Assert.Equal((3, 4, "CRLF line ending: use LF"), (issue.Line, issue.Column, issue.Message));
    }

    [Fact]
    public void Trailing_spaces_after_the_raw_string_closing_quotes_are_layout()
    {
        const string source = "let text = r\"\"\"\nline one\n\"\"\"  \nio.write_standard_output_line(\"{text.length()}\")\n";

        var issue = Assert.Single(LayoutCheck.Check(source));
        Assert.Equal((3, 5, "trailing whitespace"), (issue.Line, issue.Column, issue.Message));
    }

    [Fact]
    public void Trailing_spaces_after_a_single_line_raw_string_are_layout()
    {
        var issue = Assert.Single(LayoutCheck.Check("let text = r\"a\" \n"));

        Assert.Equal((1, 16, "trailing whitespace"), (issue.Line, issue.Column, issue.Message));
    }

    [Fact]
    public void A_crlf_after_a_single_line_raw_string_is_layout()
    {
        var issue = Assert.Single(LayoutCheck.Check("let text = r\"a\"\r\n"));

        Assert.Equal((1, 16, "CRLF line ending: use LF"), (issue.Line, issue.Column, issue.Message));
    }

    [Fact]
    public void A_tab_in_a_comment_is_layout()
    {
        var issue = Assert.Single(LayoutCheck.Check("# a\tcomment\n"));

        Assert.Equal((1, 4, "tab: use spaces"), (issue.Line, issue.Column, issue.Message));
    }

    [Fact]
    public void Trailing_space_and_crlf_in_a_comment_are_layout()
    {
        var issues = LayoutCheck.Check("# comment \r\n");

        Assert.Contains(issues, issue => issue.Message == "trailing whitespace");
        Assert.Contains(issues, issue => issue.Message == "CRLF line ending: use LF");
    }

    [Fact]
    public void A_tab_inside_an_interpolation_expression_is_layout()
    {
        var issue = Assert.Single(LayoutCheck.Check("let text = \"{a\t+ b}\"\n"));

        Assert.Equal((1, 15, "tab: use spaces"), (issue.Line, issue.Column, issue.Message));
    }

    [Fact]
    public void A_missing_final_newline_and_blank_line_rules_are_reported()
    {
        Assert.Contains(LayoutCheck.Check("let x = 1"), issue => issue.Message == "missing final newline");
        Assert.Contains(LayoutCheck.Check("\nlet x = 1\n"), issue => issue.Message == "blank line at the start of the file");
        Assert.Contains(LayoutCheck.Check("let x = 1\n\n\nlet y = 2\n"), issue => issue.Message == "more than one consecutive blank line");
        Assert.Contains(LayoutCheck.Check("let x = 1\n\n"), issue => issue.Message == "blank line at the end of the file");
    }

    [Fact]
    public void Continuations_and_method_chains_keep_the_author_indentation()
    {
        const string source = """
            let sum = [1, 2, 3]
                .transform(n => n * 2)
                .sum()
            let value = compute(
                    1,
                2)
            io.write_standard_output_line("{sum}{value}")
            """;

        Assert.Empty(LayoutCheck.Check(source + "\n"));
    }

    [Fact]
    public void A_multiline_raw_string_is_content_not_layout()
    {
        const string source = "let text = r\"\"\"\n  two spaces\n\ttab\n\"\"\"\nio.write_standard_output_line(\"{text.length()}\")\n";

        Assert.Empty(LayoutCheck.Check(source));
    }

    [Fact]
    public void Columns_count_code_points_not_utf16_units()
    {
        var emoji = Assert.Single(LayoutCheck.Check("let a = \"😀\" \n"));
        Assert.Equal((1, 12, "trailing whitespace"), (emoji.Line, emoji.Column, emoji.Message));

        var tab = Assert.Single(LayoutCheck.Check("let a = \"😀\"\tlet b = 1\n"));
        Assert.Equal((1, 12, "tab: use spaces"), (tab.Line, tab.Column, tab.Message));

        var missing = Assert.Single(LayoutCheck.Check("let a = \"😀\""));
        Assert.Equal((1, 12, "missing final newline"), (missing.Line, missing.Column, missing.Message));

        var crlf = Assert.Single(LayoutCheck.Check("let a = \"😀\"\r\n"));
        Assert.Equal((1, 12, "CRLF line ending: use LF"), (crlf.Line, crlf.Column, crlf.Message));
    }

    [Fact]
    public void Ascii_columns_stay_unchanged()
    {
        var trailing = Assert.Single(LayoutCheck.Check("let x = 1 \n"));
        Assert.Equal((1, 10, "trailing whitespace"), (trailing.Line, trailing.Column, trailing.Message));

        var crlf = Assert.Single(LayoutCheck.Check("let x = 1\r\n"));
        Assert.Equal((1, 10, "CRLF line ending: use LF"), (crlf.Line, crlf.Column, crlf.Message));

        var missing = Assert.Single(LayoutCheck.Check("let x = 1"));
        Assert.Equal((1, 10, "missing final newline"), (missing.Line, missing.Column, missing.Message));
    }

    [Fact]
    public void A_lexical_error_stops_the_scan_with_one_issue()
    {
        var issues = LayoutCheck.Check("let text = \"unterminated\n");

        var issue = Assert.Single(issues);
        Assert.Equal("the source cannot be tokenized", issue.Message);
    }
}
