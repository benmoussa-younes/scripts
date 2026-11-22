namespace Scripts.Checker.Tests;

public sealed class MatchTests
{
    [Fact]
    public void Exhaustive_matches_are_accepted()
    {
        Checking.AssertClean("""
            let flag = "x".length() > 0
            match flag {
                true => io.write_standard_output_line("t")
                false => io.write_standard_output_line("f")
            }
            match flag {
                true | false => io.write_standard_output_line("both")
            }
            match flag {
                true => io.write_standard_output_line("t")
                _ => io.write_standard_output_line("other")
            }
            let n = "x".length()
            match n {
                1 | 2 => io.write_standard_output_line("small")
                -1 => io.write_standard_output_line("negative")
                other => io.write_standard_output_line("{other}")
            }
            match "x".trim_whitespace() {
                "a" => io.write_standard_output_line("a")
                _ => io.write_standard_output_line("other")
            }
            match "1".parse_integer() {
                ok(value) => io.write_standard_output_line("{value}")
                error(failure) => io.write_standard_output_line(failure.message)
            }
            match "1".parse_integer() {
                ok(_) => io.write_standard_output_line("ok")
                other => io.write_standard_output_line("{other.is_ok()}")
            }
            """);
    }

    [Fact]
    public void Missing_cases_are_reported_on_the_match_keyword()
    {
        Checking.AssertMarked("let flag = true\n‸match flag {\n    true => io.write_standard_output_line(\"t\")\n}", "SCRIPTS-TYPE-0026");
        Checking.AssertMarked("let flag = true\n‸match flag {\n    false => io.write_standard_output_line(\"f\")\n}", "SCRIPTS-TYPE-0026");
        Checking.AssertMarked("‸match 1 {\n    1 => io.write_standard_output_line(\"a\")\n}", "SCRIPTS-TYPE-0026");
        Checking.AssertMarked("‸match \"a\" {\n    \"a\" => io.write_standard_output_line(\"a\")\n}", "SCRIPTS-TYPE-0026");
        Checking.AssertMarked("‸match \"1\".parse_integer() {\n    ok(_) => io.write_standard_output_line(\"a\")\n}", "SCRIPTS-TYPE-0026");
        Checking.AssertMarked("‸match \"1\".parse_integer() {\n    error(_) => io.write_standard_output_line(\"a\")\n}", "SCRIPTS-TYPE-0026");
    }

    [Fact]
    public void The_missing_part_is_named_in_the_message()
    {
        var result = Checking.Check("let flag = true\nmatch flag {\n    true => io.write_standard_output_line(\"t\")\n}");
        Assert.Contains("false", Assert.Single(result.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_closed_kind_set_is_exhaustive_without_a_wildcard()
    {
        Checking.AssertClean("""
            let status = filesystem.get_path_status(p"/tmp")?
            match status.kind {
                "file" => io.write_standard_output_line("f")
                "directory" => io.write_standard_output_line("d")
                "symlink" => io.write_standard_output_line("l")
                "other" => io.write_standard_output_line("o")
            }
            """);
    }

    [Fact]
    public void A_closed_kind_set_covers_alternatives()
    {
        Checking.AssertClean("""
            let status = filesystem.get_path_status(p"/tmp")?
            match status.kind {
                "file" | "directory" => io.write_standard_output_line("dir")
                "symlink" => io.write_standard_output_line("link")
                "other" => io.write_standard_output_line("other")
            }
            """);
    }

    [Fact]
    public void A_missing_value_of_a_closed_kind_set_is_named()
    {
        var result = Checking.Check("""
            let status = filesystem.get_path_status(p"/tmp")?
            match status.kind {
                "file" => io.write_standard_output_line("f")
                "directory" => io.write_standard_output_line("d")
                "other" => io.write_standard_output_line("o")
            }
            """);
        Assert.Contains("symlink", Assert.Single(result.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_outside_a_closed_kind_set_is_still_rejected()
    {
        // The typo is both outside the closed set (0058) and unreachable once the four values are covered (0027):
        // the closed set does not weaken the existing typo and unreachable-arm checks.
        var result = Checking.Check("""
            let status = filesystem.get_path_status(p"/tmp")?
            match status.kind {
                "file" => io.write_standard_output_line("f")
                "directory" => io.write_standard_output_line("d")
                "symlink" => io.write_standard_output_line("l")
                "other" => io.write_standard_output_line("o")
                "fiel" => io.write_standard_output_line("typo")
            }
            """);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "SCRIPTS-TYPE-0058");
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "SCRIPTS-TYPE-0027");
        Assert.All(result.Errors, diagnostic =>
        {
            Assert.Equal((7, 5), (diagnostic.Start.Line, diagnostic.Start.Column));
        });
    }

    [Fact]
    public void Arms_after_a_catch_all_or_repeating_a_pattern_are_unreachable()
    {
        Checking.AssertMarked("match 1 {\n    _ => io.write_standard_output_line(\"a\")\n    ‸1 => io.write_standard_output_line(\"b\")\n}", "SCRIPTS-TYPE-0027");
        Checking.AssertMarked("match 1 {\n    other => io.write_standard_output_line(\"a\")\n    ‸_ => io.write_standard_output_line(\"b\")\n}", "SCRIPTS-TYPE-0027");
        Checking.AssertMarked("match 1 {\n    1 => io.write_standard_output_line(\"a\")\n    ‸1 => io.write_standard_output_line(\"b\")\n    _ => io.write_standard_output_line(\"c\")\n}", "SCRIPTS-TYPE-0027");
        Checking.AssertMarked("match \"a\" {\n    \"a\" => io.write_standard_output_line(\"a\")\n    ‸\"a\" => io.write_standard_output_line(\"b\")\n    _ => io.write_standard_output_line(\"c\")\n}", "SCRIPTS-TYPE-0027");
        Checking.AssertMarked("match 1 {\n    1 | ‸1 => io.write_standard_output_line(\"a\")\n    _ => io.write_standard_output_line(\"c\")\n}", "SCRIPTS-TYPE-0027");
        Checking.AssertMarked("match 1 {\n    1 | 2 => io.write_standard_output_line(\"a\")\n    ‸2 | 1 => io.write_standard_output_line(\"b\")\n    _ => io.write_standard_output_line(\"c\")\n}", "SCRIPTS-TYPE-0027");
        Checking.AssertMarked("match 1 {\n    1 | _ | ‸2 => io.write_standard_output_line(\"a\")\n}", "SCRIPTS-TYPE-0027");
        Checking.AssertMarked("let flag = true\nmatch flag {\n    true => io.write_standard_output_line(\"t\")\n    false => io.write_standard_output_line(\"f\")\n    ‸_ => io.write_standard_output_line(\"x\")\n}", "SCRIPTS-TYPE-0027");
        Checking.AssertMarked("let flag = true\nmatch flag {\n    true => io.write_standard_output_line(\"t\")\n    ‸true => io.write_standard_output_line(\"x\")\n    false => io.write_standard_output_line(\"f\")\n}", "SCRIPTS-TYPE-0027");
        Checking.AssertMarked("match \"1\".parse_integer() {\n    ok(_) => io.write_standard_output_line(\"a\")\n    ‸ok(_) => io.write_standard_output_line(\"b\")\n    error(_) => io.write_standard_output_line(\"c\")\n}", "SCRIPTS-TYPE-0027");
        Checking.AssertMarked("match \"1\".parse_integer() {\n    ok(_) => io.write_standard_output_line(\"a\")\n    error(_) => io.write_standard_output_line(\"c\")\n    ‸other => io.write_standard_output_line(\"{other.is_ok()}\")\n}", "SCRIPTS-TYPE-0027");
    }

    [Fact]
    public void Unsupported_scrutinee_types_are_rejected_without_checking_patterns()
    {
        Checking.AssertMarked("match ‸1.5 {\n    _ => io.write_standard_output_line(\"a\")\n}", "SCRIPTS-TYPE-0024");
        Checking.AssertMarked("match ‸p\"a\" {\n    1 => io.write_standard_output_line(\"a\")\n}", "SCRIPTS-TYPE-0024");
        Checking.AssertMarked("match ‸[1] {\n    _ => io.write_standard_output_line(\"a\")\n}", "SCRIPTS-TYPE-0024");
        Checking.AssertMarked("match ‸{\"a\": 1} {\n    other => io.write_standard_output_line(\"a\")\n}", "SCRIPTS-TYPE-0024");
    }

    [Fact]
    public void Pattern_types_must_match_the_scrutinee()
    {
        Checking.AssertMarked("match 1 {\n    ‸\"a\" => io.write_standard_output_line(\"a\")\n    _ => io.write_standard_output_line(\"b\")\n}", "SCRIPTS-TYPE-0025");
        Checking.AssertMarked("match \"a\" {\n    ‸1 => io.write_standard_output_line(\"a\")\n    _ => io.write_standard_output_line(\"b\")\n}", "SCRIPTS-TYPE-0025");
        Checking.AssertMarked("match 1 {\n    ‸true => io.write_standard_output_line(\"a\")\n    _ => io.write_standard_output_line(\"b\")\n}", "SCRIPTS-TYPE-0025");
        Checking.AssertMarked("match 1 {\n    ‸ok(_) => io.write_standard_output_line(\"a\")\n    _ => io.write_standard_output_line(\"b\")\n}", "SCRIPTS-TYPE-0025");
        Checking.AssertMarked("match \"1\".parse_integer() {\n    ‸1 => io.write_standard_output_line(\"a\")\n    _ => io.write_standard_output_line(\"b\")\n}", "SCRIPTS-TYPE-0025");
        Checking.AssertMarked("match 1 {\n    1 | ‸\"a\" => io.write_standard_output_line(\"a\")\n    _ => io.write_standard_output_line(\"b\")\n}", "SCRIPTS-TYPE-0025");
    }

    [Fact]
    public void Arm_types_must_agree_in_expression_position()
    {
        Checking.AssertMarked("let r = match 1 {\n    1 => \"a\"\n    _ => ‸2\n}", "SCRIPTS-TYPE-0028");
        Checking.AssertMarked("let r: string = match 1 {\n    1 => \"a\"\n    _ => ‸2\n}", "SCRIPTS-TYPE-0012");
        Assert.Equal("string", Checking.TypeOfLastLet("let _v = match 1 {\n    1 => \"a\"\n    _ => \"b\"\n}").ToString());
        Assert.Equal("integer", Checking.TypeOfLastLet("let _v = match \"1\".parse_integer() {\n    ok(n) => n\n    error(_) => process.exit_with_code(1)\n}").ToString());
        Assert.Equal("integer", Checking.TypeOfLastLet("let _v = match \"1\".parse_integer() {\n    ok(n) => n\n    error(e) => process.exit_with_message(2, \"bad: {e.kind}\")\n}").ToString());
    }

    [Fact]
    public void Expression_matches_take_the_expected_type_for_literals()
    {
        Checking.AssertClean("let xs: list<integer> = match 1 {\n    1 => []\n    _ => [2]\n}\nio.write_standard_output_line(\"{xs.length()}\")");
    }

    [Fact]
    public void Expression_matches_anchor_empty_literals_on_a_self_typed_arm()
    {
        // Without an annotation the first self-typed arm fixes the type, like a list element (DESIGN 3.3).
        Assert.Equal("list<integer>", Checking.TypeOfLastLet("let _xs = match 1 {\n    1 => []\n    _ => [2]\n}").ToString());
        Assert.Equal("list<integer>", Checking.TypeOfLastLet("let _xs = match 1 {\n    1 => [2]\n    _ => []\n}").ToString());
        Assert.Equal("list<integer>", Checking.TypeOfLastLet("let _xs = match 1 {\n    1 => process.exit_with_code(1)\n    _ => [2]\n}").ToString());
    }

    [Fact]
    public void Statement_arms_must_be_unit_or_never()
    {
        Checking.AssertMarked("match 1 {\n    1 => ‸\"a\".trim_whitespace()\n    _ => io.write_standard_output_line(\"b\")\n}", "SCRIPTS-TYPE-0048");
        Checking.AssertClean("match 1 {\n    1 => process.exit_with_code(1)\n    _ => {\n        io.write_standard_output_line(\"b\")\n    }\n}");
    }

    [Fact]
    public void Block_arms_of_an_expression_match_take_the_value_of_their_last_line()
    {
        Assert.Equal("string", Checking.TypeOfLastLet("let _v = match 1 {\n    1 => {\n        let n = 2\n        \"{n}\"\n    }\n    _ => \"b\"\n}").ToString());
        Assert.Equal("integer", Checking.TypeOfLastLet("let _v = match 1 {\n    1 => {\n        let k = 3\n        match k {\n            3 => 4\n            _ => 5\n        }\n    }\n    _ => 0\n}").ToString());
        Checking.AssertClean("function f(items: list<integer>) -> integer {\n    for item in items {\n        let half = match item {\n            0 => {\n                continue\n            }\n            1 => {\n                return -1\n            }\n            _ => item / 2\n        }\n        return half\n    }\n    return 0\n}");
        Checking.AssertClean("let _f = (n: integer) => match n {\n    0 => {\n        for i in [1] {\n            if i > 0 {\n                break\n            }\n        }\n        1\n    }\n    _ => n\n}");
        Checking.AssertMarked("let _v = match 1 {\n    1 => {\n        \"a\"\n    }\n    _ => ‸2\n}", "SCRIPTS-TYPE-0028");
    }

    [Fact]
    public void A_block_arm_without_a_final_value_is_TYPE_0072()
    {
        Checking.AssertMarked("let _v = match 1 {\n    1 => {\n        let _a = 1\n    ‸}\n    _ => 2\n}", "SCRIPTS-TYPE-0072");
        Checking.AssertMarked("let _v = match 1 {\n    1 => { ‸}\n    _ => 2\n}", "SCRIPTS-TYPE-0072");
        Checking.AssertMarked("let mutable a = 0\nlet _v = match 1 {\n    1 => {\n        a = 1\n    ‸}\n    _ => a\n}", "SCRIPTS-TYPE-0072");
        Checking.AssertMarked("function f() -> integer {\n    let v = match 1 {\n        0 => {\n            return 3\n            ‸4\n        }\n        _ => 2\n    }\n    return v\n}", "SCRIPTS-TYPE-0053");
        Checking.AssertMarked("let _v = match 1 {\n    1 => {\n        ‸break\n    }\n    _ => 2\n}", "SCRIPTS-TYPE-0050");
    }

    [Fact]
    public void Return_break_and_continue_cannot_leave_a_lambda_through_a_block_arm()
    {
        Checking.AssertMarked("for _i in [1] {\n    let _f = (n: integer) => match n {\n        0 => {\n            ‸continue\n        }\n        _ => n\n    }\n}", "SCRIPTS-TYPE-0073");
        Checking.AssertMarked("let _f = (n: integer) => match n {\n    0 => {\n        ‸return 1\n    }\n    _ => n\n}", "SCRIPTS-TYPE-0073");
    }

    [Fact]
    public void Patterns_bind_values_with_the_payload_types()
    {
        Assert.Equal("integer", Checking.TypeOfLastLet("let _v = match \"1\".parse_integer() {\n    ok(n) => n + 1\n    error(_) => 0\n}").ToString());
        Assert.Equal("string", Checking.TypeOfLastLet("let _v = match \"1\".parse_integer() {\n    ok(_) => \"fine\"\n    error(failure) => failure.kind\n}").ToString());
        Checking.AssertMarked("let _v = match \"1\".parse_integer() {\n    ok(n) => n.‸length()\n    error(_) => 0\n}", "SCRIPTS-TYPE-0008");
    }

    [Fact]
    public void Unused_result_bindings_of_patterns_are_reported()
    {
        Checking.AssertMarked("let outer: result<result<integer, string>, string> = ok(ok(1))\nmatch outer {\n    ok(‸inner) => io.write_standard_output_line(\"a\")\n    error(_) => io.write_standard_output_line(\"b\")\n}", "SCRIPTS-TYPE-0049");
    }
}
