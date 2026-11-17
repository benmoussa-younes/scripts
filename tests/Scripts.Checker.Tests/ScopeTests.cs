namespace Scripts.Checker.Tests;

public sealed class ScopeTests
{
    [Fact]
    public void Redeclaring_a_name_is_reported_on_the_later_declaration()
    {
        Checking.AssertMarked("let count = 1\nlet ‸count = 2", "SCRIPTS-TYPE-0003");
        Checking.AssertMarked("let value = 1\nfunction f() {\n    let mutable inner = 1\n    let ‸inner = 2\n}\nio.write_standard_output_line(\"{value}\")", "SCRIPTS-TYPE-0003");
    }

    [Fact]
    public void The_message_cites_the_first_declaration_line()
    {
        var result = Checking.Check("let count = 1\n\nlet count = 2");
        var error = Assert.Single(result.Errors);
        Assert.Contains("line 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_message_names_the_nature_of_the_first_declaration()
    {
        Assert.Contains("constant", Assert.Single(Checking.Check("let count = 1\nlet count = 2").Errors).Message, StringComparison.Ordinal);
        Assert.Contains("function", Assert.Single(Checking.Check("function helper() {\n}\nfunction f(helper: integer) {\n}").Errors).Message, StringComparison.Ordinal);
        Assert.Contains(
            "local variable",
            Assert.Single(Checking.Check("let value = 1\nfunction f() {\n    let mutable inner = 1\n    let inner = 2\n}\nio.write_standard_output_line(\"{value}\")").Errors).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Parameters_cannot_shadow_functions_or_each_other()
    {
        Checking.AssertMarked("function helper() {\n}\nfunction f(‸helper: integer) {\n}", "SCRIPTS-TYPE-0003");
        Checking.AssertMarked("function f(a: integer, ‸a: integer) {\n}", "SCRIPTS-TYPE-0006");
        Checking.AssertMarked("let total = [1].fold(0, (a, ‸a) => a)", "SCRIPTS-TYPE-0006");
    }

    [Fact]
    public void A_local_conflicts_with_a_constant_even_when_the_constant_comes_later()
    {
        Checking.AssertMarked("""
            function f() {
                let limit = 1
                io.write_standard_output_line("{limit}")
            }
            let ‸limit = 10
            """, "SCRIPTS-TYPE-0003");
    }

    [Fact]
    public void A_local_may_reuse_the_name_of_a_script_variable_invisible_to_functions()
    {
        Checking.AssertClean("""
            let total = "x".trim_whitespace()
            function f() {
                let total = 1
                io.write_standard_output_line("{total}")
            }
            io.write_standard_output_line(total)
            """);
    }

    [Fact]
    public void A_block_local_conflicts_with_a_later_top_level_function()
    {
        Checking.AssertMarked("""
            if true {
                let helper = 1
                io.write_standard_output_line("{helper}")
            }
            function ‸helper() {
            }
            """, "SCRIPTS-TYPE-0003");
    }

    [Fact]
    public void Top_level_names_conflict_in_textual_order()
    {
        Checking.AssertMarked("function total() {\n}\nlet ‸total = \"x\".trim_whitespace()", "SCRIPTS-TYPE-0003");
        Checking.AssertMarked("function twice() {\n}\nfunction ‸twice() {\n}", "SCRIPTS-TYPE-0003");
        Checking.AssertMarked("let base = 1\nfunction ‸base() {\n}", "SCRIPTS-TYPE-0003");
    }

    [Fact]
    public void Loop_lambda_and_pattern_bindings_cannot_shadow()
    {
        Checking.AssertMarked("let item = 1\nfor ‸item in [1] {\n}", "SCRIPTS-TYPE-0003");
        Checking.AssertMarked("let x = 1\nlet ys = [1].transform(‸x => x)", "SCRIPTS-TYPE-0003");
        Checking.AssertMarked("let value = 1\nmatch 1 {\n    ‸value => io.write_standard_output_line(\"a\")\n}", "SCRIPTS-TYPE-0003");
        Checking.AssertMarked("let failure = 1\nmatch \"1\".parse_integer() {\n    ok(_) => io.write_standard_output_line(\"a\")\n    error(‸failure) => io.write_standard_output_line(\"b\")\n}", "SCRIPTS-TYPE-0003");
    }

    [Fact]
    public void Nested_scopes_cannot_shadow_enclosing_names()
    {
        Checking.AssertMarked("function f(n: integer) {\n    if n > 0 {\n        let ‸n = 2\n        io.write_standard_output_line(\"{n}\")\n    }\n}", "SCRIPTS-TYPE-0003");
    }

    [Fact]
    public void Siblings_may_reuse_names()
    {
        Checking.AssertClean("""
            function f(n: integer) {
                if n > 0 {
                    let local = 1
                    io.write_standard_output_line("{local}")
                } else {
                    let local = 2
                    io.write_standard_output_line("{local}")
                }
            }
            function g(n: integer) {
                let local = 3
                io.write_standard_output_line("{local}{n}")
            }
            let ys = [1].transform(x => x)
            let zs = [1].transform(x => x)
            for item in [1] {
            }
            for item in [2] {
            }
            """);
    }

    [Theory]
    [InlineData("let ‸environment = 1", "SCRIPTS-TYPE-0004")]
    [InlineData("let ‸encoding = 1", "SCRIPTS-TYPE-0004")]
    [InlineData("let ‸random = 1", "SCRIPTS-TYPE-0004")]
    [InlineData("let ‸ok = 1", "SCRIPTS-TYPE-0004")]
    [InlineData("let ‸error = 1", "SCRIPTS-TYPE-0004")]
    [InlineData("function ‸environment() {\n}", "SCRIPTS-TYPE-0004")]
    [InlineData("for ‸error in [1] {\n}", "SCRIPTS-TYPE-0004")]
    [InlineData("let ‸assert = 1", "SCRIPTS-TYPE-0005")]
    [InlineData("function ‸assert() {\n}", "SCRIPTS-TYPE-0005")]
    [InlineData("function f(‸range: integer) {\n}", "SCRIPTS-TYPE-0005")]
    [InlineData("for ‸assert in [1] {\n}", "SCRIPTS-TYPE-0005")]
    public void Reserved_and_standard_names_cannot_be_declared(string marked, string code)
    {
        Checking.AssertMarked(marked, code);
    }

    [Fact]
    public void Methods_are_not_reserved_names()
    {
        Checking.AssertClean("let length = 1\nlet filter = 2\nfunction split() {\n}\nio.write_standard_output_line(\"{length}{filter}\")");
    }

    [Fact]
    public void Using_a_name_before_its_declaration_is_reported()
    {
        Checking.AssertMarked("io.write_standard_output_line(\"{‸later}\")\nlet later = \"x\".trim_whitespace()", "SCRIPTS-TYPE-0002");
        Checking.AssertMarked("let x = ‸x + 1", "SCRIPTS-TYPE-0002");
        Checking.AssertMarked("function f() {\n    io.write_standard_output_line(\"{‸y}\")\n    let y = 1\n}", "SCRIPTS-TYPE-0002");
        Checking.AssertMarked("io.write_standard_output_line(\"{‸limit}\")\nlet limit = 10", "SCRIPTS-TYPE-0002");
        Checking.AssertMarked("let first = ‸second + 1\nlet second = 2", "SCRIPTS-TYPE-0002");
    }

    [Fact]
    public void Nested_functions_are_visible_only_after_their_declaration()
    {
        Checking.AssertMarked("function outer() {\n    ‸inner()\n    function inner() {\n    }\n}", "SCRIPTS-TYPE-0002");
        Checking.AssertClean("function outer() {\n    function inner() {\n    }\n    inner()\n}");
        Checking.AssertClean("function outer(n: integer) -> integer {\n    function down(k: integer) -> integer {\n        if k <= 0 {\n            return 0\n        }\n        return down(k - 1)\n    }\n    return down(n)\n}");
    }

    [Fact]
    public void A_name_declared_in_a_closed_block_is_unknown_afterwards()
    {
        Checking.AssertMarked("if true {\n    let a = 1\n    io.write_standard_output_line(\"{a}\")\n}\nio.write_standard_output_line(\"{‸a}\")", "SCRIPTS-TYPE-0001");
    }

    [Fact]
    public void Unknown_names_suggest_the_closest_visible_name()
    {
        var result = Checking.Check("let counter = 1\nio.write_standard_output_line(\"{countr}\")");
        var error = Assert.Single(result.Errors);
        Assert.Equal("SCRIPTS-TYPE-0001", error.Code);
        Assert.Contains("counter", error.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Top_level_functions_are_hoisted_and_may_be_mutually_recursive()
    {
        Checking.AssertClean("""
            io.write_standard_output_line("{is_even(4)}")
            function is_even(n: integer) -> boolean {
                if n == 0 {
                    return true
                }
                return is_odd(n - 1)
            }
            function is_odd(n: integer) -> boolean {
                if n == 0 {
                    return false
                }
                return is_even(n - 1)
            }
            """);
    }

    [Fact]
    public void Constants_are_visible_in_functions_regardless_of_order()
    {
        Checking.AssertClean("""
            function show() {
                io.write_standard_output_line(greeting)
                io.write_standard_output_line("{count + 1}")
            }
            let greeting = "hi"
            let count = 1 + 2
            show()
            """);
    }

    [Fact]
    public void Script_variables_are_invisible_in_top_level_functions()
    {
        Checking.AssertMarked("let total = \"x\".trim_whitespace()\nfunction show() {\n    io.write_standard_output_line(‸total)\n}", "SCRIPTS-TYPE-0007");
        Checking.AssertMarked("let mutable counter = 0\nfunction bump() {\n    ‸counter += 1\n}", "SCRIPTS-TYPE-0007");
        Checking.AssertMarked("let total = \"x\".trim_whitespace()\nfunction show() {\n    let ys = [1].transform(x => \"{‸total}\")\n    io.write_standard_output_line(ys.join(\",\"))\n}", "SCRIPTS-TYPE-0007");
        Checking.AssertMarked("function show() {\n    io.write_standard_output_line(‸total)\n}\nlet total = \"x\".trim_whitespace()", "SCRIPTS-TYPE-0007");
    }

    [Fact]
    public void Script_variable_reason_explains_why_it_is_not_constant()
    {
        var mutableResult = Checking.Check("let mutable counter = 0\nfunction bump() {\n    io.write_standard_output_line(\"{counter}\")\n}");
        Assert.Contains("mutable", Assert.Single(mutableResult.Errors).Cause, StringComparison.Ordinal);
        var callResult = Checking.Check("let total = \"x\".trim_whitespace()\nfunction show() {\n    io.write_standard_output_line(total)\n}");
        Assert.Contains("call", Assert.Single(callResult.Errors).Cause, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_functions_and_lambdas_at_script_level_capture_script_variables()
    {
        Checking.AssertClean("""
            let mutable counter = 0
            if true {
                function bump() {
                    counter += 1
                }
                bump()
            }
            let add = [1].transform(x => x + counter)
            io.write_standard_output_line("{add.length()}")
            """);
    }

    [Fact]
    public void Nested_functions_can_assign_captured_variables_of_the_enclosing_function()
    {
        Checking.AssertClean("""
            function count_up() -> integer {
                let mutable counter = 0
                function bump() {
                    counter += 1
                }
                bump()
                bump()
                return counter
            }
            io.write_standard_output_line("{count_up()}")
            """);
    }

    [Fact]
    public void Break_and_continue_target_the_innermost_loop_of_the_same_function()
    {
        Checking.AssertMarked("‸break", "SCRIPTS-TYPE-0050");
        Checking.AssertMarked("if true {\n    ‸continue\n}", "SCRIPTS-TYPE-0050");
        Checking.AssertMarked("while true {\n    function f() {\n        ‸break\n    }\n    f()\n    break\n}", "SCRIPTS-TYPE-0050");
        Checking.AssertClean("for item in [1, 2] {\n    if item == 1 {\n        continue\n    }\n    break\n}");
    }
}
