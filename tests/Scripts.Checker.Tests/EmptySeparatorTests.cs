namespace Scripts.Checker.Tests;

/// <summary>SCRIPTS-TYPE-0065: an empty literal separator of split/split_regex is a static error.</summary>
public sealed class EmptySeparatorTests
{
    [Theory]
    [InlineData("let parts = \"abc\".split(‸\"\")\nlet _ = parts")]
    [InlineData("let matched = \"abc\".split_regex(‸\"\")\nlet _ = matched?")]
    [InlineData("let parts = \"abc\".split(‸r\"\")\nlet _ = parts")]
    [InlineData("let parts = \"abc\".split(‸r\"\"\"\"\"\")\nlet _ = parts")]
    public void An_empty_literal_separator_is_rejected_on_the_literal(string source) =>
        Checking.AssertMarked(source, "SCRIPTS-TYPE-0065");

    [Theory]
    [InlineData("split")]
    [InlineData("split_regex")]
    public void The_message_names_the_method_as_written(string method)
    {
        var propagate = method == "split_regex" ? "?" : string.Empty;
        var result = Checking.AssertMarked($"let parts = \"abc\".{method}(‸\"\")\nlet _ = parts{propagate}", "SCRIPTS-TYPE-0065");
        Assert.Contains($"`{method}`", result.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pattern_that_is_not_a_literal_separator_is_accepted() =>
        Checking.AssertClean("let parts = \"abc\".split_regex(r\"[0-9]+\")\nlet _ = parts?");

    [Fact]
    public void An_empty_literal_receiver_is_not_a_separator() =>
        Checking.AssertClean("let pieces = \"\".lines()\nlet found = \"\".contains(\"x\")\nio.write_standard_output_line(\"{pieces.length()} {found}\")");

    [Theory]
    [InlineData("let parts = \"abc\".split(\",\")\nlet _ = parts")]
    [InlineData("let parts = \"abc\".split(r\",\")\nlet _ = parts")]
    [InlineData("let separator = \"\"\nlet parts = \"abc\".split(separator)\nlet _ = parts")]
    [InlineData("let separator: string = \"\"\nlet parts = \"abc\".split(separator)\nlet _ = parts")]
    [InlineData("let parts = \"abc\".split(\",\" + \"\")\nlet _ = parts")]
    [InlineData("let parts = [\"a\"].join(\"\")\nio.write_standard_output_line(parts)")]
    [InlineData("let found = \"abc\".contains(\"\")\nio.write_standard_output_line(\"{found}\")")]
    [InlineData("let text = \"abc\".replace(\"\", \"x\")\nio.write_standard_output_line(text)")]
    public void An_empty_separator_that_is_not_a_literal_is_accepted(string source)
    {
        var result = Checking.AssertClean(source);
        Assert.DoesNotContain(result.Errors, error => error.Code == "SCRIPTS-TYPE-0065");
    }

    [Fact]
    public void A_wrong_separator_arity_reports_only_the_arity_error()
    {
        Assert.Equal(
            ["SCRIPTS-TYPE-0013 1:25"],
            Checking.ErrorPositions(Checking.Check("let parts = \"abc\".split()\nlet _ = parts")));
        Assert.Equal(
            ["SCRIPTS-TYPE-0013 1:29"],
            Checking.ErrorPositions(Checking.Check("let parts = \"abc\".split(\"\", \"\")\nlet _ = parts")));
    }
}
