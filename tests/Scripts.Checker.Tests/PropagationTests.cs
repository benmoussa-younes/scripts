namespace Scripts.Checker.Tests;

public sealed class PropagationTests
{
    [Fact]
    public void Question_mark_unwraps_results_inside_functions_with_the_same_error_type()
    {
        Checking.AssertClean("""
            function read_number(file_path: path) -> result<integer, error> {
                let text = filesystem.read_file(file_path)?
                return ok(text.length())
            }
            function parse(text: string) -> result<integer, string> {
                let value = text.parse_integer().transform_error(failure => failure.message)?
                return ok(value + 1)
            }
            function chain(file_path: path) -> result<unit, error> {
                filesystem.write_file_overwrite("x", file_path)?
                filesystem.make_directory(file_path)?
                return ok()
            }
            let first_ok = read_number(p"/x").is_ok()
            let second_ok = parse("1").is_ok()
            let third_ok = chain(p"/y").is_ok()
            io.write_standard_output_line("{first_ok}{second_ok}{third_ok}")
            """);
    }

    [Fact]
    public void The_error_type_must_match_the_function_return_type()
    {
        Checking.AssertMarked("function f() -> result<integer, string> {\n    let n = \"1\".parse_integer()‸?\n    return ok(n)\n}", "SCRIPTS-TYPE-0046");
        Checking.AssertMarked("function f() -> result<integer, string> {\n    let n = process.run(process.request(\"true\", []))‸?\n    return ok(1)\n}", "SCRIPTS-TYPE-0046");
        Checking.AssertMarked("function f() {\n    filesystem.make_directory(p\"/x\")‸?\n}", "SCRIPTS-TYPE-0046");
        Checking.AssertMarked("function f() -> integer {\n    let n = \"1\".parse_integer()‸?\n    return 1\n}", "SCRIPTS-TYPE-0046");
    }

    [Fact]
    public void One_unified_error_lets_read_file_and_parse_integer_chain_without_transform_error()
    {
        // Both filesystem.read_file and parse_integer fail with `error`, so a single `?` chain type-checks as is.
        Checking.AssertClean("""
            function read_and_parse(file_path: path) -> result<integer, error> {
                let text = filesystem.read_file(file_path)?
                let value = text.trim_whitespace().parse_integer()?
                return ok(value)
            }
            """);
    }

    [Fact]
    public void The_message_names_the_function_and_the_expected_type()
    {
        var result = Checking.Check("function convert() -> result<integer, string> {\n    let n = \"1\".parse_integer()?\n    return ok(n)\n}");
        var error = Assert.Single(result.Errors);
        Assert.Contains("convert", error.Message, StringComparison.Ordinal);
        Assert.Contains("error", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Question_mark_is_forbidden_in_lambdas()
    {
        Checking.AssertMarked("let ys = [\"1\"].transform(s => s.parse_integer()‸?)", "SCRIPTS-TYPE-0044");
        Checking.AssertMarked("function f() -> result<integer, error> {\n    let ys = [\"1\"].transform(s => s.parse_integer()‸?)\n    return ok(ys.length())\n}", "SCRIPTS-TYPE-0044");
    }

    [Fact]
    public void Question_mark_requires_a_result_operand()
    {
        Checking.AssertMarked("let x = 5‸?", "SCRIPTS-TYPE-0045");
        Checking.AssertMarked("let x = \"a\"‸?", "SCRIPTS-TYPE-0045");
        Checking.AssertMarked("let x = [1]‸?", "SCRIPTS-TYPE-0045");
        Checking.AssertMarked("io.write_standard_output_line(\"a\")‸?", "SCRIPTS-TYPE-0045");
    }

    [Fact]
    public void Top_level_question_mark_requires_a_standard_error_type_or_string()
    {
        Checking.AssertClean("""
            let a = filesystem.read_file(p"/x")?
            let b = process.run(process.request("true", []))?
            let c = environment.get_variable("HOME")?
            let d = "1".parse_integer()?
            let e = "1".parse_integer().transform_error(failure => failure.message)?
            io.write_standard_output_line("{a}{b.exit_code}{c}{d}{e}")
            """);
        Checking.AssertMarked("function make() -> result<integer, integer> {\n    return error(1)\n}\nlet n = make()‸?", "SCRIPTS-TYPE-0047");
        Checking.AssertMarked("function make() -> result<integer, list<string>> {\n    return error([\"a\"])\n}\nlet n = make()‸?", "SCRIPTS-TYPE-0047");
    }

    [Fact]
    public void Question_mark_in_a_nested_function_uses_that_functions_return_type()
    {
        Checking.AssertClean("""
            function outer() -> result<integer, string> {
                function inner() -> result<integer, error> {
                    let text = filesystem.read_file(p"/x")?
                    return ok(text.length())
                }
                return inner().transform_error(failure => failure.message)
            }
            io.write_standard_output_line("{outer().is_ok()}")
            """);
    }
}
