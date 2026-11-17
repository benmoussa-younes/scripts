namespace Scripts.Checker.Tests;

public sealed class StatementTests
{
    [Fact]
    public void Expression_statements_must_be_unit_or_never()
    {
        Checking.AssertMarked("‸\"a\".trim_whitespace()", "SCRIPTS-TYPE-0048");
        Checking.AssertMarked("‸\"1\".parse_integer()", "SCRIPTS-TYPE-0048");
        Checking.AssertMarked("‸process.run(process.request(\"true\", []))?", "SCRIPTS-TYPE-0048");
        Checking.AssertMarked("‸[1].length()", "SCRIPTS-TYPE-0048");
        Checking.AssertMarked("function f() -> integer {\n    return 1\n}\n‸f()", "SCRIPTS-TYPE-0048");
        Checking.AssertClean("io.write_standard_output_line(\"a\")\nlet _ = \"a\".trim_whitespace()\nlet _ignored = \"a\".trim_whitespace()");
        Checking.AssertClean("filesystem.make_directory(p\"/tmp/x\")?");
    }

    [Fact]
    public void Result_bindings_must_be_read()
    {
        Checking.AssertMarked("let ‸outcome = \"1\".parse_integer()", "SCRIPTS-TYPE-0049");
        Checking.AssertMarked("let ‸outcomes = [\"1\".parse_integer()]", "SCRIPTS-TYPE-0049");
        Checking.AssertMarked("let ‸m = {\"a\": \"1\".parse_integer()}", "SCRIPTS-TYPE-0049");
        Checking.AssertMarked("function f(‸r: result<integer, string>) {\n}", "SCRIPTS-TYPE-0049");
        Checking.AssertMarked("let rs: list<result<integer, string>> = []\nfor ‸r in rs {\n}", "SCRIPTS-TYPE-0049");
        Checking.AssertMarked("let ‸ys = [\"1\"].transform(s => s.parse_integer())", "SCRIPTS-TYPE-0049");
        Checking.AssertMarked("let rs: list<result<integer, string>> = []\nlet ys = rs.transform(‸r => 1)", "SCRIPTS-TYPE-0049");
    }

    [Fact]
    public void Reading_a_result_binding_in_any_expression_counts()
    {
        Checking.AssertClean("""
            let outcome = "1".parse_integer()
            io.write_standard_output_line("{outcome.is_ok()}")
            let second = "2".parse_integer()
            match second {
                ok(_) => io.write_standard_output_line("ok")
                error(_) => io.write_standard_output_line("error")
            }
            let third = "3".parse_integer()
            let fallback = third.value_or(0)
            let _unused = "4".parse_integer()?
            io.write_standard_output_line("{fallback}")
            """);
    }

    [Fact]
    public void Assignments_alone_do_not_read_a_result_variable()
    {
        Checking.AssertMarked("let mutable ‸outcome = \"1\".parse_integer()\noutcome = \"2\".parse_integer()", "SCRIPTS-TYPE-0049");
    }

    [Fact]
    public void Closures_reading_a_binding_count_as_reads()
    {
        Checking.AssertClean("""
            let outcome = "1".parse_integer()
            let check = (x: integer) => outcome.is_ok()
            io.write_standard_output_line("{check(1)}")
            """);
    }

    [Fact]
    public void Loop_headers_check_their_expressions()
    {
        Checking.AssertMarked("for x in ‸5 {\n}", "SCRIPTS-TYPE-0022");
        Checking.AssertMarked("let m = {\"a\": 1}\nfor k in ‸m {\n}", "SCRIPTS-TYPE-0022");
        Checking.AssertMarked("for c in ‸\"abc\" {\n}", "SCRIPTS-TYPE-0022");
        Checking.AssertClean("let m = {\"a\": 1}\nfor k in m.keys() {\n    io.write_standard_output_line(k)\n}\nfor i in range(0, 3) {\n    io.write_standard_output_line(\"{i}\")\n}");
        Checking.AssertMarked("for x in ‸[] {\n}", "SCRIPTS-TYPE-0029");
    }

    [Fact]
    public void Loop_variables_take_the_element_type()
    {
        Checking.AssertMarked("for item in [1, 2] {\n    io.write_standard_output_line(‸item)\n}", "SCRIPTS-TYPE-0012");
        Checking.AssertClean("for item in [1, 2] {\n    io.write_standard_output_line(\"{item + 1}\")\n}");
        Checking.AssertClean("for _ in [1, 2] {\n    io.write_standard_output_line(\"tick\")\n}");
    }

    [Fact]
    public void Discard_bindings_never_declare_names()
    {
        Checking.AssertClean("let _ = \"a\".trim_whitespace()\nlet _ = \"b\".trim_whitespace()");
    }

    [Fact]
    public void Statements_inside_blocks_are_checked()
    {
        Checking.AssertMarked("if true {\n    let x: integer = ‸\"a\"\n}", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("while false {\n    ‸missing()\n}", "SCRIPTS-TYPE-0001");
        Checking.AssertMarked("for item in [1] {\n    if item > 0 {\n        ‸\"a\".trim_whitespace()\n    }\n}", "SCRIPTS-TYPE-0048");
    }

    [Fact]
    public void Errors_are_ordered_by_position_without_duplicates()
    {
        var result = Checking.Check("let a: integer = \"x\"\nlet b: integer = \"y\"\nio.write_standard_output_line(missing)");
        Assert.Equal(["SCRIPTS-TYPE-0012 1:18", "SCRIPTS-TYPE-0012 2:18", "SCRIPTS-TYPE-0001 3:31"], Checking.ErrorPositions(result));
    }
}
