namespace Scripts.Checker.Tests;

public sealed class GenericsTests
{
    [Theory]
    [InlineData("let _v = [1, 2, 3].filter(x => x > 1)", "list<integer>")]
    [InlineData("let _v = [\"a\"].transform(s => s.length())", "list<integer>")]
    [InlineData("let _v = [1, 2].transform(x => \"{x}\")", "list<string>")]
    [InlineData("let _v = [1, 2].transform(x => [x])", "list<list<integer>>")]
    [InlineData("let _v = [1, 2].transform(x => x > 1)", "list<boolean>")]
    [InlineData("let _v = [\"a\", \"b\"].fold(0, (total, s) => total + s.length())", "integer")]
    [InlineData("let _v = [1, 2].fold(\"\", (text, n) => text + \"{n}\")", "string")]
    [InlineData("let _v = [1, 2].fold([0], (acc, x) => acc + [x])", "list<integer>")]
    [InlineData("let _v = [1, 2].fold({\"a\": 0}, (acc, x) => acc)", "map<string, integer>")]
    [InlineData("let value = \"1\".parse_integer().transform_error(e => e.message)\nio.write_standard_output_line(\"{value.is_ok()}\")", "result<integer, string>")]
    [InlineData("let value = \"1\".parse_integer().transform_error(e => e.kind)\nio.write_standard_output_line(\"{value.is_ok()}\")", "result<integer, string>")]
    [InlineData("let value = \"1\".parse_integer().transform_error(e => 1)\nio.write_standard_output_line(\"{value.is_ok()}\")", "result<integer, integer>")]
    [InlineData("let _v = [1].get_at_index(0).value_or(5)", "integer")]
    [InlineData("let _v = [1].get_at_index(0).value_or_fail()", "integer")]
    [InlineData("let _v = [1].get_at_index(0).is_ok()", "boolean")]
    [InlineData("let _v = not [1].get_at_index(0).is_ok()", "boolean")]
    [InlineData("let _v = {\"a\": 1}.get_value(\"a\").value_or(0)", "integer")]
    [InlineData("let _v = {\"a\": 1}.keys()", "list<string>")]
    [InlineData("let _v = {\"a\": 1}.values()", "list<integer>")]
    [InlineData("let _v = {1: \"a\"}.contains_key(1)", "boolean")]
    [InlineData("let _v = {1: \"a\"}.length()", "integer")]
    [InlineData("let _v = [\"a\", \"b\"].join(\",\")", "string")]
    [InlineData("let _v = [3, 1].sorted()", "list<integer>")]
    [InlineData("let _v = [p\"b\", p\"a\"].sorted()", "list<path>")]
    [InlineData("let _v = [1.5].reversed()", "list<float>")]
    [InlineData("let _v = [1].contains(1)", "boolean")]
    [InlineData("let _v = [[1]].contains([1])", "boolean")]
    [InlineData("let _v = \"a,b\".split(\",\")", "list<string>")]
    [InlineData("let value = \"a\".matches(\"a\")\nio.write_standard_output_line(\"{value.is_ok()}\")", "result<boolean, error>")]
    [InlineData("let _v = \"a b\".split(\" \").filter(w => w.length() > 0).transform(w => w.uppercased()).join(\"-\")", "string")]
    [InlineData("let _v = [1, 2].transform(x => [3].transform(y => x + y))", "list<list<integer>>")]
    [InlineData("let _v = \"x\".lines()", "list<string>")]
    [InlineData("let value = \"x\".parse_float()\nio.write_standard_output_line(\"{value.is_ok()}\")", "result<float, error>")]
    public void Generic_signatures_bind_variables_in_order(string source, string expectedType)
    {
        Assert.Equal(expectedType, Checking.TypeOfLastLet(source).ToString());
    }

    [Theory]
    [InlineData("let ys = [1].filter(x => ‸x)", "SCRIPTS-TYPE-0012")]
    [InlineData("let n = [1].get_at_index(0).value_or(‸\"a\")", "SCRIPTS-TYPE-0012")]
    [InlineData("let n = {\"a\": 1}.get_value(‸1)?", "SCRIPTS-TYPE-0012")]
    [InlineData("let n = [1].contains(‸\"a\")", "SCRIPTS-TYPE-0012")]
    [InlineData("let parts = \"a\".split(‸1)", "SCRIPTS-TYPE-0012")]
    [InlineData("let n = [1].fold(0, (acc, x) => ‸\"a\")", "SCRIPTS-TYPE-0012")]
    [InlineData("let n = [1].fold(0, ‸x => x)", "SCRIPTS-TYPE-0032")]
    [InlineData("let ys = [1].transform(x => x, ‸1)", "SCRIPTS-TYPE-0013")]
    public void Generic_signature_mismatches(string marked, string code)
    {
        Checking.AssertMarked(marked, code);
    }

    [Fact]
    public void Fold_with_an_empty_literal_accumulator_is_undetermined()
    {
        Checking.AssertMarked("let r = [1, 2].fold(‸[], (acc, x) => acc)", "SCRIPTS-TYPE-0029");
    }

    [Fact]
    public void A_lambda_that_never_returns_leaves_its_variable_undetermined()
    {
        Checking.AssertMarked("let ys = ‸[1].transform(x => process.exit_with_code(1))", "SCRIPTS-TYPE-0029");
    }

    [Fact]
    public void Receiver_constraints_are_reported_on_the_method_name()
    {
        Checking.AssertMarked("let s = [1, 2].‸join(\",\")", "SCRIPTS-TYPE-0057");
        Checking.AssertMarked("let s = [true].‸sorted()", "SCRIPTS-TYPE-0057");
        Checking.AssertMarked("let s = [[1]].‸sorted()", "SCRIPTS-TYPE-0057");
        Checking.AssertMarked("let xs: list<result<integer, string>> = []\nlet s = xs.‸contains(ok(1))", "SCRIPTS-TYPE-0057");
        Checking.AssertMarked("let _g = [1].‸group_by(x => [x])", "SCRIPTS-TYPE-0035");
        Checking.AssertMarked("let s = [\"a\"].join(‸1)", "SCRIPTS-TYPE-0012");
    }

    [Fact]
    public void Unknown_methods_list_the_methods_of_the_type()
    {
        var result = Checking.Check("let n = \"abc\".size()");
        var error = Assert.Single(result.Errors);
        Assert.Equal("SCRIPTS-TYPE-0008", error.Code);
        Assert.Contains("uppercased", error.Suggestion, StringComparison.Ordinal);
        Checking.AssertMarked("let r: result<integer, string> = ok(1)\nlet n = r.‸length()", "SCRIPTS-TYPE-0008");
        Checking.AssertMarked("let n = (5).‸abs()", "SCRIPTS-TYPE-0008");
        Checking.AssertMarked("let p = p\"a\"\nlet n = p.‸length()", "SCRIPTS-TYPE-0008");
        Checking.AssertMarked("let xs = [1]\nlet n = xs.‸keys()", "SCRIPTS-TYPE-0008");
    }

    [Fact]
    public void An_unknown_method_names_the_closest_method_before_the_full_list()
    {
        var error = Assert.Single(Checking.Check("let n = \"abc\".lenght()").Errors);
        Assert.Equal("SCRIPTS-TYPE-0008", error.Code);
        Assert.Contains("close name: `length`", error.Suggestion, StringComparison.Ordinal);
        Assert.Contains("uppercased", error.Suggestion, StringComparison.Ordinal);
        Assert.True(
            error.Suggestion.IndexOf("close name", StringComparison.Ordinal)
            < error.Suggestion.IndexOf("Full list", StringComparison.Ordinal),
            error.Suggestion);
    }

    [Fact]
    public void Calling_a_record_field_is_an_unknown_method()
    {
        Checking.AssertMarked("let outcome = process.run(process.request(\"true\", []))?\nlet n = outcome.‸exit_code()", "SCRIPTS-TYPE-0008");
    }

    [Fact]
    public void User_functions_are_valid_function_arguments()
    {
        Checking.AssertClean("""
            function is_long(text: string) -> boolean {
                return text.length() > 3
            }
            function measure(text: string) -> integer {
                return text.length()
            }
            let long_ones = ["abcd", "a"].filter(is_long)
            let sizes = ["abcd", "a"].transform(measure)
            io.write_standard_output_line("{long_ones.length()}{sizes.length()}")
            """);
    }

    [Fact]
    public void Function_arguments_must_have_the_expected_signature()
    {
        Checking.AssertMarked("function bad(a: integer) -> boolean {\n    return a > 0\n}\nlet ys = [\"a\"].filter(‸bad)", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("let ys = [\"a\"].transform(‸to_float)", "SCRIPTS-TYPE-0056");
        Checking.AssertMarked("let ys = [\"a\"].transform(‸filesystem.list_directory)", "SCRIPTS-TYPE-0056");
        Checking.AssertMarked("let g = [\"a\"].transform(‸environment)", "SCRIPTS-TYPE-0059");
    }

    [Fact]
    public void Expected_types_flow_into_generic_results()
    {
        Checking.AssertClean("let ys: list<string> = [1].transform(x => \"{x}\")");
        Checking.AssertMarked("let ys: list<integer> = ‸[1].transform(x => \"{x}\")", "SCRIPTS-TYPE-0012");
    }
}
