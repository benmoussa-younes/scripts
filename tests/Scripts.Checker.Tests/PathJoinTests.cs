using Scripts.Core;

namespace Scripts.Checker.Tests;

/// <summary>SCRIPTS-TYPE-0069: a literal absolute or empty right operand of `/` on a path is rejected statically.</summary>
public sealed class PathJoinTests
{
    [Theory]
    [InlineData("let joined = p\"/srv\" / ‸\"/etc/passwd\"")]
    [InlineData("let joined = p\"/srv\" / ‸\"\"")]
    [InlineData("let joined = p\"/srv\" / ‸p\"/etc\"")]
    [InlineData("let joined = p\"/srv\" / ‸p\"\"")]
    [InlineData("let joined = p\"\" / ‸\"/etc\"")]
    public void Absolute_or_empty_literal_right_operand_is_rejected(string marked)
    {
        var result = Checking.AssertMarked(marked, ErrorCodes.Type0069);
        var diagnostic = Assert.Single(result.Errors);
        Assert.StartsWith("the right operand of `/` is `", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_message_names_the_offending_literal()
    {
        var absolute = Assert.Single(Checking.Check("let joined = p\"/srv\" / \"/etc/passwd\"").Errors);
        Assert.Equal("the right operand of `/` is `/etc/passwd`: a literal segment must be relative and non-empty", absolute.Message);

        var empty = Assert.Single(Checking.Check("let joined = p\"/srv\" / \"\"").Errors);
        Assert.Equal("the right operand of `/` is `\"\"`: a literal segment must be relative and non-empty", empty.Message);
    }

    [Theory]
    [InlineData("let joined = p\"/srv\" / \"root\"")]
    [InlineData("let joined = p\"/srv/\" / \"root\"")]
    [InlineData("let joined = p\"\" / \"x\"")]
    [InlineData("let joined = p\"/srv\" / p\"root\"")]
    [InlineData("let segment = \"root\"\nlet joined = p\"/srv\" / segment")]
    [InlineData("let segment = \"/etc\"\nlet joined = p\"/srv\" / segment")]
    [InlineData("let segment = \"\"\nlet joined = p\"/srv\" / segment")]
    public void Relative_base_and_computed_segments_stay_accepted_at_check_time(string source)
    {
        Checking.AssertClean(source);
    }

    [Theory]
    [InlineData("let mutable base = p\"/srv\"\nbase /= ‸\"/etc\"")]
    [InlineData("let mutable base = p\"/srv\"\nbase /= ‸p\"/etc\"")]
    [InlineData("let mutable base = p\"/srv\"\nbase /= ‸\"\"")]
    [InlineData("let mutable base = p\"/srv\"\nbase /= ‸p\"\"")]
    public void Compound_divide_assignment_rejects_an_absolute_or_empty_literal(string marked)
    {
        Checking.AssertMarked(marked, ErrorCodes.Type0069);
    }

    [Theory]
    [InlineData("let mutable base = p\"/srv\"\nbase /= \"root\"")]
    [InlineData("let mutable base = p\"/srv\"\nbase /= p\"root\"")]
    [InlineData("let mutable base = p\"/srv\"\nlet segment = \"/etc\"\nbase /= segment")]
    [InlineData("let mutable base = p\"/srv\"\nlet segment = \"\"\nbase /= segment")]
    public void Compound_divide_assignment_accepts_relative_or_computed_segments(string source)
    {
        Checking.AssertClean(source);
    }

    [Fact]
    public void Compound_divide_assignment_on_an_immutable_target_reports_only_type_0039()
    {
        var result = Checking.AssertErrors("let p = p\"/srv\"\np /= \"/etc\"", "SCRIPTS-TYPE-0039 2:1");
        Assert.Single(result.Errors);
    }

    [Fact]
    public void A_control_character_in_the_literal_is_rendered_as_replacement_character()
    {
        var result = Checking.AssertMarked("let joined = p\"/srv\" / ‸\"/etc\\0x\"", ErrorCodes.Type0069);
        var diagnostic = Assert.Single(result.Errors);
        Assert.Equal(
            "the right operand of `/` is `/etc\uFFFDx`: a literal segment must be relative and non-empty",
            diagnostic.Message);
    }

    [Fact]
    public void A_very_long_literal_is_truncated_but_keeps_its_beginning()
    {
        var segment = "/" + new string('a', 1000);
        var diagnostic = Assert.Single(Checking.Check($"let joined = p\"/srv\" / \"{segment}\"").Errors);
        Assert.Contains("/" + new string('a', 20), diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("…", diagnostic.Message, StringComparison.Ordinal);
        Assert.True(diagnostic.Message.Length < 400, $"message length {diagnostic.Message.Length}");
    }
}
