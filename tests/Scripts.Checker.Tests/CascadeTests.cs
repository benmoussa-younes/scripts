namespace Scripts.Checker.Tests;

public sealed class CascadeTests
{
    [Fact]
    public void An_unknown_name_poisons_only_its_own_expression()
    {
        Checking.AssertMarked("let a = ‸missing()\nlet b = a + 1\nio.write_standard_output_line(\"{b}\")", "SCRIPTS-TYPE-0001");
        Checking.AssertMarked("let a = ‸undefined + 1\nlet b = a * 2\nio.write_standard_output_line(\"{b}\")", "SCRIPTS-TYPE-0001");
        Checking.AssertMarked("let xs = [‸missing_name, 1]\nlet n = xs.length()\nio.write_standard_output_line(\"{n}\")", "SCRIPTS-TYPE-0001");
        Checking.AssertMarked("let r = ‸missing_name.parse_integer()\nlet b = r.is_ok()\nio.write_standard_output_line(\"{b}\")", "SCRIPTS-TYPE-0001");
        Checking.AssertMarked("let r = ‸missing()", "SCRIPTS-TYPE-0001");
        Checking.AssertMarked("let v = ‸missing()?", "SCRIPTS-TYPE-0001");
    }

    [Fact]
    public void Poisoned_values_do_not_trigger_control_flow_errors()
    {
        Checking.AssertMarked("match ‸missing {\n    1 => io.write_standard_output_line(\"a\")\n}", "SCRIPTS-TYPE-0001");
        Checking.AssertMarked("if ‸missing {\n}", "SCRIPTS-TYPE-0001");
        Checking.AssertMarked("for item in ‸missing {\n    io.write_standard_output_line(\"{item}\")\n}", "SCRIPTS-TYPE-0001");
        Checking.AssertMarked("let n: integer = ‸missing\nio.write_standard_output_line(\"{n}\")", "SCRIPTS-TYPE-0001");
        Checking.AssertMarked("function f() -> integer {\n    return ‸missing\n}", "SCRIPTS-TYPE-0001");
    }

    [Fact]
    public void Failed_declarations_do_not_break_their_uses()
    {
        Checking.AssertMarked("let f = ‸x => x\nlet y = f(1)", "SCRIPTS-TYPE-0030");
        Checking.AssertMarked("function f(a: ‸Unknown) -> integer {\n    return a\n}\nlet n = f(1)\nio.write_standard_output_line(\"{n}\")", "SCRIPTS-TYPE-0036");
        Checking.AssertMarked("let n = to_float(1, ‸2)\nlet m = n + 1.5\nio.write_standard_output_line(\"{m}\")", "SCRIPTS-TYPE-0013");
        Checking.AssertMarked("let m: map<‸float, integer> = {}\nlet n = m.length()\nio.write_standard_output_line(\"{n}\")", "SCRIPTS-TYPE-0035");
        Checking.AssertMarked("let ‸environment = 1\nio.write_standard_output_line(\"{environment}\")", "SCRIPTS-TYPE-0004");
        Checking.AssertMarked("let ‸assert = 1\nio.write_standard_output_line(\"{assert}\")", "SCRIPTS-TYPE-0005");
    }

    [Fact]
    public void Failed_operator_results_do_not_cascade()
    {
        // S19: after an operator error the result is `<error>`, so a later use adds no second diagnostic.
        Checking.AssertMarked("let a = 1 ‸< \"a\"\nlet b = a + 1\nio.write_standard_output_line(\"{b}\")", "SCRIPTS-TYPE-0016");
        Checking.AssertMarked("let a = 1 ‸< \"a\"\nlet b: integer = a\nio.write_standard_output_line(\"{b}\")", "SCRIPTS-TYPE-0016");
        Checking.AssertMarked("let a = 1 ‸< \"a\"\nlet b = a and true\nio.write_standard_output_line(\"{b}\")", "SCRIPTS-TYPE-0016");
        Checking.AssertMarked("let x = \"a\"\nlet b = ‸not x\nlet c = b + 1\nio.write_standard_output_line(\"{c}\")", "SCRIPTS-TYPE-0017");
        Checking.AssertMarked("let x = \"a\"\nlet b = ‸not x\nlet c: integer = b\nio.write_standard_output_line(\"{c}\")", "SCRIPTS-TYPE-0017");
        Checking.AssertMarked("let b = 1 ‸and true\nlet c = b + 1\nio.write_standard_output_line(\"{c}\")", "SCRIPTS-TYPE-0015");
    }

    [Fact]
    public void A_wildcard_parameter_is_anonymous()
    {
        // S20: `_` is a valid parameter of a function and of a lambda; it is unreadable and unshadowable.
        Checking.AssertClean("function f(_: integer) -> integer {\n    return 1\n}\nio.write_standard_output_line(\"{f(1)}\")");
        Checking.AssertClean("function f(_: integer, _: integer) -> integer {\n    return 1\n}\nio.write_standard_output_line(\"{f(1, 2)}\")");
        Checking.AssertClean("function f(_: integer) -> integer {\n    return 1\n}\nlet _ = 2\nio.write_standard_output_line(\"{f(1)}\")");
        Checking.AssertClean("let g = (x: integer) => x\nlet h = (_: integer) => g(1)\nio.write_standard_output_line(\"{h(2)}\")");
    }

    [Fact]
    public void Independent_errors_are_all_reported_in_position_order()
    {
        Checking.AssertMarked(
            "let a: integer = ‸\"x\"\nlet b = ‸missing\nfunction ‸f() -> integer {\n    io.write_standard_output_line(\"a\")\n}\nlet c = [1, ‸\"y\"]",
            "SCRIPTS-TYPE-0012",
            "SCRIPTS-TYPE-0001",
            "SCRIPTS-TYPE-0052",
            "SCRIPTS-TYPE-0012");
    }
}
