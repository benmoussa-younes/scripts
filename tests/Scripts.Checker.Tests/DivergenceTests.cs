namespace Scripts.Checker.Tests;

public sealed class DivergenceTests
{
    [Theory]
    [InlineData("function ‸f() -> integer {\n    io.write_standard_output_line(\"a\")\n}")]
    [InlineData("function ‸f(flag: boolean) -> integer {\n    if flag {\n        return 1\n    }\n}")]
    [InlineData("function ‸f(flag: boolean) -> integer {\n    if flag {\n        return 1\n    } else if not flag {\n        return 2\n    }\n}")]
    [InlineData("function ‸f(flag: boolean) -> integer {\n    if flag {\n        return 1\n    } else {\n        io.write_standard_output_line(\"x\")\n    }\n}")]
    [InlineData("function ‸f() -> integer {\n    while true {\n        break\n    }\n}")]
    [InlineData("function ‸f(flag: boolean) -> integer {\n    while flag {\n        return 1\n    }\n}")]
    [InlineData("function ‸f() -> integer {\n    for item in [1] {\n        return item\n    }\n}")]
    [InlineData("function ‸f() -> integer {\n    while true {\n        for item in [1] {\n            break\n        }\n        if true {\n            break\n        }\n    }\n}")]
    [InlineData("function ‸f(n: integer) -> integer {\n    match n {\n        1 => io.write_standard_output_line(\"a\")\n        _ => {\n            return 2\n        }\n    }\n}")]
    [InlineData("function ‸f() -> list<integer> {\n    let xs = [1]\n}")]
    public void Missing_returns_are_reported_on_the_function_name(string marked)
    {
        Checking.AssertMarked(marked, "SCRIPTS-TYPE-0052");
    }

    [Theory]
    [InlineData("function f() -> integer {\n    return 1\n}")]
    [InlineData("function f(flag: boolean) -> integer {\n    if flag {\n        return 1\n    } else {\n        return 2\n    }\n}")]
    [InlineData("function f(n: integer) -> integer {\n    if n > 1 {\n        return 1\n    } else if n > 0 {\n        return 2\n    } else {\n        return 3\n    }\n}")]
    [InlineData("function f() -> integer {\n    process.exit_with_code(2)\n}")]
    [InlineData("function f() -> integer {\n    while true {\n    }\n}")]
    [InlineData("function f() -> integer {\n    while true {\n        if true {\n            return 1\n        }\n    }\n}")]
    [InlineData("function f() -> integer {\n    while true {\n        for item in [1] {\n            break\n        }\n    }\n}")]
    [InlineData("function f(n: integer) -> integer {\n    match n {\n        1 => process.exit_with_code(1)\n        _ => {\n            return 2\n        }\n    }\n}")]
    [InlineData("function f(flag: boolean) -> integer {\n    if flag {\n        process.exit_with_code(1)\n    } else {\n        return 2\n    }\n}")]
    [InlineData("function f() {\n    io.write_standard_output_line(\"a\")\n}")]
    [InlineData("function f() -> unit {\n    io.write_standard_output_line(\"a\")\n}")]
    [InlineData("function f() {\n    return\n}")]
    public void Diverging_bodies_need_no_final_return(string source)
    {
        Checking.AssertClean(source);
    }

    [Theory]
    [InlineData("function f() -> integer {\n    return 1\n    ‸io.write_standard_output_line(\"a\")\n}")]
    [InlineData("function f() -> integer {\n    process.exit_with_code(1)\n    ‸io.write_standard_output_line(\"a\")\n}")]
    [InlineData("function f(flag: boolean) -> integer {\n    if flag {\n        return 1\n    } else {\n        return 2\n    }\n    ‸io.write_standard_output_line(\"a\")\n}")]
    [InlineData("function f() {\n    while true {\n    }\n    ‸io.write_standard_output_line(\"a\")\n}")]
    [InlineData("function f() {\n    while true {\n        break\n        ‸io.write_standard_output_line(\"a\")\n    }\n}")]
    [InlineData("function f() {\n    for item in [1] {\n        continue\n        ‸io.write_standard_output_line(\"a\")\n    }\n}")]
    [InlineData("function f(n: integer) {\n    match n {\n        1 => process.exit_with_code(1)\n        _ => {\n            return\n        }\n    }\n    ‸io.write_standard_output_line(\"a\")\n}")]
    [InlineData("return\n‸io.write_standard_output_line(\"a\")")]
    [InlineData("process.exit_with_code(3)\n‸let x = 1")]
    [InlineData("function f() {\n    return\n    ‸let x = 1\n}")]
    public void Statements_after_a_divergent_statement_are_unreachable(string marked)
    {
        Checking.AssertMarked(marked, "SCRIPTS-TYPE-0053");
    }

    [Fact]
    public void Only_the_first_unreachable_statement_is_reported_and_functions_are_never_unreachable()
    {
        Checking.AssertMarked("function f() {\n    return\n    ‸io.write_standard_output_line(\"a\")\n    io.write_standard_output_line(\"b\")\n    function inner() {\n    }\n}", "SCRIPTS-TYPE-0053");
        Checking.AssertClean("function f() {\n    return\n    function inner() {\n    }\n}");
        Checking.AssertClean("return\nfunction later() {\n}");
    }

    [Fact]
    public void Return_values_are_checked_against_the_declared_type()
    {
        Checking.AssertMarked("function f() -> integer {\n    return ‸\"a\"\n}", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("function f() -> integer {\n    ‸return\n}", "SCRIPTS-TYPE-0051");
        Checking.AssertMarked("function f() {\n    return ‸1\n}", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("function f() -> list<integer> {\n    return [‸\"a\"]\n}", "SCRIPTS-TYPE-0012");
        Checking.AssertClean("function f() -> list<integer> {\n    return []\n}");
        Checking.AssertClean("function f() -> result<unit, string> {\n    return ok()\n}");
        Checking.AssertClean("function f() -> result<integer, string> {\n    return error(\"x\")\n}");
    }

    [Fact]
    public void Top_level_return_rules()
    {
        Checking.AssertClean("return");
        Checking.AssertClean("return 1");
        Checking.AssertClean("let value: result<integer, string> = error(\"x\")\nreturn value");
        Checking.AssertMarked("function g() {\n}\nreturn ‸g", "SCRIPTS-TYPE-0054");
        Checking.AssertMarked("return ‸(x: integer) => x", "SCRIPTS-TYPE-0054");
        Checking.AssertMarked("return ‸io.write_standard_output_line(\"a\")", "SCRIPTS-TYPE-0034");
        Checking.AssertMarked("return ‸[]", "SCRIPTS-TYPE-0029");
    }

    [Fact]
    public void A_top_level_return_rejects_a_function_nested_in_a_container()
    {
        Checking.AssertMarked("let f = () => 1\nreturn ‸[f]", "SCRIPTS-TYPE-0054");
        Checking.AssertMarked("let f = () => 1\nreturn ‸{\"handler\": f}", "SCRIPTS-TYPE-0054");
        Checking.AssertMarked("let f = () => 1\nreturn ‸[[f]]", "SCRIPTS-TYPE-0054");
        Checking.AssertMarked(
            "let f = () => 1\nlet nested: list<result<function() -> integer, error>> = [ok(f)]\nreturn ‸nested",
            "SCRIPTS-TYPE-0054");
        Checking.AssertMarked(
            "let lock = filesystem.acquire_file_lock(p\"/tmp/scripts-spec-lock\", 0)?\nreturn ‸lock",
            "SCRIPTS-TYPE-0054");
        Checking.AssertMarked(
            "let lock = filesystem.acquire_file_lock(p\"/tmp/scripts-spec-lock\", 0)?\nreturn ‸[lock]",
            "SCRIPTS-TYPE-0054");
    }

    [Fact]
    public void Serializable_top_level_returns_are_still_accepted()
    {
        Checking.AssertClean("return [1, 2]");
        Checking.AssertClean("return {\"a\": 1}");
        Checking.AssertClean("let value: result<integer, error> = ok(1)\nreturn value");
    }

    [Fact]
    public void Unit_functions_declared_with_a_result_return_type_need_an_explicit_return()
    {
        Checking.AssertMarked("function ‸f() -> result<unit, string> {\n    io.write_standard_output_line(\"a\")\n}", "SCRIPTS-TYPE-0052");
    }
}
