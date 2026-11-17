namespace Scripts.Checker.Tests;

public sealed class CallTests
{
    [Theory]
    [InlineData("let n = to_float(1, ‸2)")]
    [InlineData("let n = to_float(‸)")]
    [InlineData("function f(a: integer) -> integer {\n    return a\n}\nlet n = f(1, ‸2)")]
    [InlineData("function f(a: integer) -> integer {\n    return a\n}\nlet n = f(‸)")]
    [InlineData("let n = process.identifier(‸1)")]
    [InlineData("let b = \"abc\".starts_with(‸)")]
    [InlineData("let b = \"abc\".starts_with(\"a\", ‸\"b\")")]
    [InlineData("let n = \"a\".trim_whitespace(‸1)")]
    [InlineData("let parts = \"a,b\".split(\",\", ‸\",\")")]
    [InlineData("let parts = \"a,b\".split(‸)")]
    [InlineData("let g = (x: integer) => x\nlet n = g(1, ‸2)")]
    [InlineData("let g = (x: integer) => x\nlet n = g(‸)")]
    [InlineData("import \"lib.scs\" as lib\nlet n = lib.one(‸1)")]
    [InlineData("let n = environment.get_variable(‸)")]
    public void Wrong_argument_counts_are_reported_once_at_the_documented_position(string marked)
    {
        Checking.AssertMarkedInModules(marked, [("lib.scs", "public function one() -> integer {\n    return 1\n}\n")], "SCRIPTS-TYPE-0013");
    }

    [Fact]
    public void Arity_errors_do_not_compare_arguments_but_report_their_own_errors()
    {
        Checking.AssertMarked("let n = to_float(‸missing, ‸2)", "SCRIPTS-TYPE-0001", "SCRIPTS-TYPE-0013");
    }

    [Theory]
    [InlineData("let x = 1\nlet y = ‸x(2)", "integer")]
    [InlineData("let s = \"a\"\nlet y = ‸s()", "string")]
    [InlineData("function f() -> integer {\n    return 1\n}\nlet y = ‸f()(1)", "integer")]
    [InlineData("let y = ‸\"a\"(1)", "string")]
    public void Values_that_are_not_functions_cannot_be_called(string marked, string typeText)
    {
        var result = Checking.AssertMarked(marked, "SCRIPTS-TYPE-0014");
        Assert.Contains(typeText, result.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void For_each_line_accepts_a_fallible_string_visitor()
    {
        Checking.AssertClean("filesystem.for_each_line(p\"/tmp/x\", line => ok())?");
        Checking.AssertClean("function visit_line(line: string) -> result<unit, error> {\n    return ok()\n}\nfilesystem.for_each_line(p\"/tmp/x\", visit_line)?");
        Checking.AssertClean("function visit_line(line: string) -> result<unit, error> {\n    return error(error(\"other\", \"\", \"stop\"))\n}\nfilesystem.for_each_line(p\"/tmp/x\", visit_line)?");

        var rejected = Checking.Check("filesystem.for_each_line(p\"/tmp/x\", line => line)");
        Assert.Contains(rejected.Errors, error => error.Code == "SCRIPTS-TYPE-0012");
    }

    [Fact]
    public void Arguments_are_checked_against_parameter_types()
    {
        Checking.AssertMarked("io.write_standard_output_line(‸1)", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("function f(a: integer, b: string) {\n}\nf(1, ‸2)", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("let g = (x: integer) => x\nlet y = g(‸\"a\")", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("let listed = filesystem.list_directory(‸\"a\")?", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("let content = filesystem.read_file(‸\"a\")?", "SCRIPTS-TYPE-0012");
    }

    [Fact]
    public void Function_values_are_called_through_variables_parameters_and_results()
    {
        Checking.AssertClean("""
            function apply(action: function(integer) -> integer, value: integer) -> integer {
                return action(value)
            }
            function make_adder(step: integer) -> function(integer) -> integer {
                return (n: integer) => n + step
            }
            let add_two = make_adder(2)
            io.write_standard_output_line("{apply(add_two, 1)}{make_adder(3)(4)}")
            """);
    }

    [Fact]
    public void Unknown_callees_are_reported()
    {
        Checking.AssertMarked("‸missing(1)", "SCRIPTS-TYPE-0001");
        Checking.AssertMarked("‸environment(1)", "SCRIPTS-TYPE-0059");
        Checking.AssertMarkedInModules("import \"lib.scs\" as lib\n‸lib(1)", [("lib.scs", "public let value = 1\n")], "SCRIPTS-TYPE-0059");
        Checking.AssertMarked("let n = filesystem.‸nothing(p\"a\")", "SCRIPTS-TYPE-0011");
        Checking.AssertMarked("let n = environment.‸unset(\"A\")", "SCRIPTS-TYPE-0011");
    }

    [Fact]
    public void Arguments_of_failed_calls_still_report_their_own_errors_without_cascade()
    {
        Checking.AssertMarked("‸missing(‸other, [])", "SCRIPTS-TYPE-0001", "SCRIPTS-TYPE-0001");
    }

    [Fact]
    public void Chained_calls_and_field_reads_type_through_results()
    {
        Checking.AssertClean("""
            let first_line = process.run(process.request("true", []))?.standard_output.lines().get_at_index(0).value_or("")
            let code = process.run(process.request("true", []))?.exit_code + 1
            io.write_standard_output_line("{first_line}{code}")
            """);
    }

    [Fact]
    public void Named_arguments_bind_by_name_for_user_functions_and_catalogue_entries()
    {
        Checking.AssertClean("""
            function add(first: integer, second: integer) -> integer {
                return first + second
            }
            let total = add(second = 2, first = 1)
            io.write_standard_output_line("{total}")
            """);
        Checking.AssertClean("filesystem.copy_file(destination_path = p\"b.txt\", source_path = p\"a.txt\")?");
        Checking.AssertClean("let _part = \"abcdef\".substring(count = 2, start_index = 1)?");
        Checking.AssertClean("let _request = process.request(program = \"true\", arguments = [])");
    }

    [Fact]
    public void Named_and_positional_forms_are_both_accepted_and_bind_the_same_parameters()
    {
        Checking.AssertClean("filesystem.copy_file(destination_path = p\"b\", source_path = p\"a\")?");
        Checking.AssertClean("filesystem.copy_file(p\"b\", p\"a\")?");
    }

    [Fact]
    public void A_named_call_on_a_function_value_is_one_diagnostic_at_the_call()
    {
        var result = Checking.Check("""
            let g = (x: integer, y: integer) => x + y
            let value = g(x = 1, y = 2)
            """);

        var diagnostic = Assert.Single(result.Errors);
        Assert.Equal("SCRIPTS-TYPE-0067", diagnostic.Code);
        Assert.Contains("function value", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_named_call_on_a_zero_parameter_function_is_an_arity_error_with_its_signature()
    {
        var result = Checking.Check("function g() -> integer { return 1 }\nlet value = g(y = 1)");

        var diagnostic = Assert.Single(result.Errors);
        Assert.Equal("SCRIPTS-TYPE-0013", diagnostic.Code);
        Assert.Equal("wrong number of arguments for `g`: expected 0, got 1", diagnostic.Message);
        Assert.Equal("call `g() -> integer`", diagnostic.Suggestion);
    }

    [Fact]
    public void A_named_call_on_a_zero_parameter_method_is_an_arity_error_with_its_signature()
    {
        var result = Checking.Check("let n = \"abc\".length(y = 1)");

        var diagnostic = Assert.Single(result.Errors);
        Assert.Equal("SCRIPTS-TYPE-0013", diagnostic.Code);
        Assert.Equal("wrong number of arguments for `string.length`: expected 0, got 1", diagnostic.Message);
        Assert.Equal("call `string.length() -> integer`", diagnostic.Suggestion);
    }

    [Fact]
    public void A_trailing_comma_is_accepted_in_positional_and_named_calls()
    {
        Checking.AssertClean("""
            function f(a: integer) -> integer {
                return a
            }
            let first = f(1,)
            let second = f(a = 2,)
            io.write_standard_output_line("{first}{second}")
            """);
    }

    [Fact]
    public void An_unknown_module_member_names_the_closest_member()
    {
        var result = Checking.Check("let _request = process.spwan(\"true\", [])");

        var diagnostic = Assert.Single(result.Errors);
        Assert.Equal("SCRIPTS-TYPE-0011", diagnostic.Code);
        Assert.Contains("close name: `spawn`", diagnostic.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void A_method_fix_that_would_change_the_arity_is_withheld()
    {
        var result = Checking.Check("let n = \"abc\".lenght(1)");

        var diagnostic = Assert.Single(result.Errors);
        Assert.Equal("SCRIPTS-TYPE-0008", diagnostic.Code);
        Assert.Null(diagnostic.Fix);
    }

    [Fact]
    public void Unknown_named_argument_is_reported_at_its_name()
    {
        var result = Checking.AssertMarked("""
            function f(a: integer) -> integer {
                return a
            }
            let x = f(‸b = 1)
            """, "SCRIPTS-TYPE-0067");
        Assert.Contains("unknown argument `b`", result.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_named_argument_is_reported_at_the_second_name()
    {
        var result = Checking.AssertMarked("""
            function f(a: integer) -> integer {
                return a
            }
            let x = f(a = 1, ‸a = 2)
            """, "SCRIPTS-TYPE-0067");
        Assert.Contains("argument `a` given twice", result.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_named_argument_is_reported_at_the_call()
    {
        var result = Checking.AssertMarked("""
            function f(a: integer, b: integer) -> integer {
                return a + b
            }
            let x = ‸f(a = 1)
            """, "SCRIPTS-TYPE-0067");
        Assert.Contains("missing argument `b`", result.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_later_named_parameter_does_not_hide_an_earlier_missing_one()
    {
        var result = Checking.AssertMarked("""
            function f(a: integer, b: integer) -> integer {
                return a + b
            }
            let x = ‸f(b = 1)
            """, "SCRIPTS-TYPE-0067");
        Assert.Contains("missing argument `a`", result.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_named_method_argument_is_reported_at_its_name()
    {
        var result = Checking.AssertMarked(
            "let _part = \"abc\".substring(start_index = 1, ‸length = 2)",
            "SCRIPTS-TYPE-0067");
        Assert.Contains("unknown argument `length`", result.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_function_value_has_no_published_parameter_names()
    {
        var result = Checking.AssertMarked("""
            let g = (x: integer) => x
            let y = ‸g(x = 1)
            """, "SCRIPTS-TYPE-0067");
        Assert.Contains("function value", result.Errors[0].Message, StringComparison.Ordinal);
    }
}
