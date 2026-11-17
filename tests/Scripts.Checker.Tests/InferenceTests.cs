namespace Scripts.Checker.Tests;

public sealed class InferenceTests
{
    [Theory]
    [InlineData("let _v = 1", "integer")]
    [InlineData("let _v = 1.5", "float")]
    [InlineData("let _v = \"a\"", "string")]
    [InlineData("let _v = true", "boolean")]
    [InlineData("let _v = p\"a/b\"", "path")]
    [InlineData("let n = 1\nlet _v = \"n={n}\"", "string")]
    [InlineData("let _v = [1, 2]", "list<integer>")]
    [InlineData("let _v = [[1], [2, 3]]", "list<list<integer>>")]
    [InlineData("let _v = {\"a\": 1}", "map<string, integer>")]
    [InlineData("let _v = {1: [\"a\"]}", "map<integer, list<string>>")]
    [InlineData("let _v = {true: p\"x\"}", "map<boolean, path>")]
    [InlineData("let _v = \"a\".length()", "integer")]
    [InlineData("let value = \"1\".parse_integer()\nio.write_standard_output_line(\"{value.is_ok()}\")", "result<integer, error>")]
    [InlineData("let value = process.run(process.request(\"true\", []))\nio.write_standard_output_line(\"{value.is_ok()}\")", "result<process_result, error>")]
    [InlineData("let _v = (1 + 2) * 3", "integer")]
    [InlineData("let _v = not (1 < 2)", "boolean")]
    [InlineData("let _v = -(3)", "integer")]
    [InlineData("let _v = to_float(2)", "float")]
    [InlineData("let _v = p\"a\" / \"b\"", "path")]
    public void Let_without_annotation_takes_the_synthesized_type(string source, string expectedType)
    {
        Assert.Equal(expectedType, Checking.TypeOfLastLet(source).ToString());
    }

    [Theory]
    [InlineData("let _v: list<integer> = []", "list<integer>")]
    [InlineData("let _v: map<string, integer> = {}", "map<string, integer>")]
    [InlineData("let _v: list<list<string>> = [[], [\"a\"]]", "list<list<string>>")]
    [InlineData("let _v: map<string, list<integer>> = {\"a\": []}", "map<string, list<integer>>")]
    [InlineData("let value: result<integer, string> = ok(1)\nio.write_standard_output_line(\"{value.is_ok()}\")", "result<integer, string>")]
    [InlineData("let value: result<integer, string> = error(\"e\")\nio.write_standard_output_line(\"{value.is_ok()}\")", "result<integer, string>")]
    [InlineData("let value: result<unit, string> = ok()\nio.write_standard_output_line(\"{value.is_ok()}\")", "result<unit, string>")]
    [InlineData("let value: result<list<integer>, string> = ok([])\nio.write_standard_output_line(\"{value.is_ok()}\")", "result<list<integer>, string>")]
    [InlineData("let _v: function(integer) -> integer = x => x + 1", "function(integer) -> integer")]
    [InlineData("let _v = (x: integer, y: string) => y", "function(integer, string) -> string")]
    [InlineData("let _v: function() -> integer = () => 1", "function() -> integer")]
    public void Expected_types_determine_empty_literals_constructors_and_lambdas(string source, string expectedType)
    {
        Assert.Equal(expectedType, Checking.TypeOfLastLet(source).ToString());
    }

    [Theory]
    [InlineData("let xs = ‸[]")]
    [InlineData("let m = ‸{}")]
    [InlineData("let r = ‸ok(1)")]
    [InlineData("let r = ‸error(\"e\")")]
    [InlineData("let r = ‸ok()")]
    public void Empty_literals_and_constructors_without_expected_type_are_undetermined(string marked)
    {
        Checking.AssertMarked(marked, "SCRIPTS-TYPE-0029");
    }

    [Fact]
    public void Nested_empty_literal_is_reported_once()
    {
        Checking.AssertMarked("let xs = [‸[], []]", "SCRIPTS-TYPE-0029");
    }

    [Fact]
    public void Empty_literal_beside_a_typed_element_takes_its_type()
    {
        Assert.Equal("list<list<integer>>", Checking.TypeOfLastLet("let _v = [[], [1]]").ToString());
    }

    [Fact]
    public void Operands_of_equality_and_addition_propagate_the_other_operand_type()
    {
        Checking.AssertClean("""
            let xs = [1]
            let a = xs == []
            let b = [] == xs
            let c = xs + []
            let d = [] + xs
            let e = {"k": 1} == {}
            """);
    }

    [Theory]
    [InlineData("let f = ‸x => x", "SCRIPTS-TYPE-0030")]
    [InlineData("let f = ‸(a, b) => a", "SCRIPTS-TYPE-0030")]
    [InlineData("let n = to_float(‸x => x)", "SCRIPTS-TYPE-0031")]
    [InlineData("let ys = [1, 2].filter(‸(a, b) => true)", "SCRIPTS-TYPE-0032")]
    [InlineData("let ys = [1, 2].filter(‸() => true)", "SCRIPTS-TYPE-0032")]
    [InlineData("let n: integer = ‸x => x", "SCRIPTS-TYPE-0031")]
    public void Lambda_rules(string marked, string code)
    {
        Checking.AssertMarked(marked, code);
    }

    [Fact]
    public void Lambda_annotation_must_match_the_expected_parameter_type()
    {
        // A concrete expected function type is compared as a whole, at the lambda (one diagnostic, no cascade).
        Checking.AssertMarked("let ys = [1].filter(‸(x: string) => true)", "SCRIPTS-TYPE-0012");
        Checking.AssertClean("let ys = [1].filter((x: integer) => x > 0)");
    }

    [Fact]
    public void Lambda_that_does_not_match_a_concrete_function_parameter_names_the_whole_contract()
    {
        var diagnostic = Assert.Single(Checking.Check("let ys = [1].filter((x: string) => true)").Errors);
        Assert.Equal("SCRIPTS-TYPE-0012", diagnostic.Code);
        Assert.Contains("expected `function(integer) -> boolean`", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("got `function(string) -> boolean`", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lambda_that_does_not_match_a_concrete_user_function_parameter_is_reported()
    {
        var diagnostic = Assert.Single(Checking.Check("""
            function apply(f: function(integer) -> integer) -> integer {
                return f(1)
            }
            let n = apply((x: string) => 1)
            """).Errors);
        Assert.Equal("SCRIPTS-TYPE-0012", diagnostic.Code);
        Assert.Contains("expected `function(integer) -> integer`", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lambda_that_matches_a_concrete_user_function_parameter_is_accepted()
    {
        Checking.AssertClean("""
            function apply(f: function(integer) -> integer) -> integer {
                return f(1)
            }
            let n = apply((x: integer) => x + 1)
            io.write_standard_output_line("{n}")
            """);
    }

    [Fact]
    public void Lambda_body_is_checked_against_a_known_return_type()
    {
        Checking.AssertMarked("let ys = [1].filter(x => ‸x)", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("let f: function(integer) -> string = x => ‸x", "SCRIPTS-TYPE-0012");
    }

    [Fact]
    public void Lambda_whose_body_is_unit_cannot_bind_a_return_variable()
    {
        Checking.AssertMarked("let ys = [1].transform(x => ‸io.write_standard_output_line(\"a\"))", "SCRIPTS-TYPE-0034");
    }

    [Fact]
    public void Lambda_callback_may_return_unit_when_the_parameter_type_says_so()
    {
        Checking.AssertClean("""
            function run_twice(action: function(integer) -> unit) {
                action(1)
                action(2)
            }
            run_twice(n => io.write_standard_output_line("{n}"))
            """);
    }

    [Fact]
    public void Lambdas_capture_visible_variables()
    {
        Checking.AssertClean("""
            let offset = 10
            let shifted = [1, 2].transform(x => x + offset)
            """);
    }

    [Theory]
    [InlineData("let x: integer = ‸\"abc\"")]
    [InlineData("let x: string = ‸1")]
    [InlineData("let x: list<integer> = [‸\"a\"]")]
    [InlineData("let x: list<integer> = [1, ‸\"a\"]")]
    [InlineData("let x: map<string, integer> = {\"a\": ‸\"b\"}")]
    [InlineData("let x: path = ‸\"a\"")]
    [InlineData("let x: float = ‸1")]
    [InlineData("let x: result<integer, string> = ‸1")]
    public void Annotations_are_checked(string marked)
    {
        Checking.AssertMarked(marked, "SCRIPTS-TYPE-0012");
    }

    [Fact]
    public void List_elements_must_share_the_first_element_type()
    {
        Checking.AssertMarked("let xs = [1, ‸\"a\", 2]", "SCRIPTS-TYPE-0012");
    }

    [Fact]
    public void Constructors_report_incompatible_expected_types()
    {
        Checking.AssertMarked("let a: integer = ‸ok(1)", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("let a: integer = ‸error(\"e\")", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("let a: list<integer> = ‸[1].get_at_index(0)", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("let a: string = ‸[]", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("let a: string = ‸{}", "SCRIPTS-TYPE-0012");
    }

    [Fact]
    public void Payloads_of_constructors_are_checked_against_the_expected_parts()
    {
        Checking.AssertMarked("let r: result<integer, string> = ok(‸\"x\")\nio.write_standard_output_line(\"{r.is_ok()}\")", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("let r: result<integer, string> = error(‸1)\nio.write_standard_output_line(\"{r.is_ok()}\")", "SCRIPTS-TYPE-0012");
    }

    [Fact]
    public void Ok_without_argument_requires_a_unit_result()
    {
        Checking.AssertMarked("let r: result<integer, string> = ‸ok()\nio.write_standard_output_line(\"{r.is_ok()}\")", "SCRIPTS-TYPE-0033");
        Checking.AssertMarked("let r: list<integer> = ‸ok()", "SCRIPTS-TYPE-0033");
    }

    [Fact]
    public void Unit_values_cannot_be_bound_stored_or_returned()
    {
        Checking.AssertMarked("let x = ‸io.write_standard_output_line(\"a\")", "SCRIPTS-TYPE-0034");
        Checking.AssertMarked("let _ = ‸io.write_standard_output_line(\"a\")", "SCRIPTS-TYPE-0034");
        Checking.AssertMarked("let xs = [‸io.write_standard_output_line(\"a\")]", "SCRIPTS-TYPE-0034");
        Checking.AssertMarked("let x = ‸process.exit_with_code(1)", "SCRIPTS-TYPE-0034");
        Checking.AssertMarked("""
            function f() -> result<unit, string> {
                return ok(‸io.write_standard_output_line("a"))
            }
            """, "SCRIPTS-TYPE-0034");
        Checking.AssertMarked("""
            function f() {
                return ‸io.write_standard_output_line("a")
            }
            """, "SCRIPTS-TYPE-0034");
    }

    [Fact]
    public void Never_is_compatible_with_every_expected_type()
    {
        Checking.AssertClean("""
            function pick(flag: boolean) -> integer {
                if flag {
                    return 1
                }
                process.exit_with_code(2)
            }
            let n: integer = pick(true)
            """);
    }

    [Theory]
    [InlineData("let a = 1\nlet b: string = ‸a")]
    [InlineData("let a = [1]\nlet b: list<string> = ‸a")]
    public void Variables_are_not_implicitly_converted(string marked)
    {
        Checking.AssertMarked(marked, "SCRIPTS-TYPE-0012");
    }

    [Fact]
    public void Index_expressions_use_the_container_types()
    {
        Assert.Equal("integer", Checking.TypeOfLastLet("let xs = [1]\nlet _v = xs[0]").ToString());
        Assert.Equal("string", Checking.TypeOfLastLet("let m = {\"a\": \"b\"}\nlet _v = m[\"a\"]").ToString());
        Checking.AssertMarked("let xs = [1]\nlet y = xs[‸\"a\"]", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("let m = {\"a\": 1}\nlet y = m[‸1]", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("let c = ‸\"abc\"[0]", "SCRIPTS-TYPE-0041");
        Checking.AssertMarked("let n = 5\nlet c = ‸n[0]", "SCRIPTS-TYPE-0041");
    }

    [Fact]
    public void Map_literals_validate_key_types_and_duplicate_constant_keys()
    {
        Checking.AssertMarked("let m = {‸1.5: \"a\"}", "SCRIPTS-TYPE-0035");
        Checking.AssertMarked("let m = {\"a\": 1, ‸\"a\": 2}", "SCRIPTS-TYPE-0055");
        Checking.AssertMarked("let m = {1: \"a\", ‸2 - 1: \"b\"}", "SCRIPTS-TYPE-0055");
        Checking.AssertMarked("let k = \"a\"\nlet m = {\"a\": 1, ‸k: 2}", "SCRIPTS-TYPE-0055");
        Checking.AssertMarked("let m = {true: 1, ‸true: 2}", "SCRIPTS-TYPE-0055");
        Checking.AssertClean("let name = \"x\".trim_whitespace()\nlet m = {name: 1, name: 2}");
    }

    [Fact]
    public void Interpolation_accepts_only_text_number_boolean_and_path()
    {
        Checking.AssertClean("""
            let text_value = "a"
            let number = 1
            let ratio = 1.5
            let flag = true
            let location = p"a"
            let rendered = "{text_value} {number} {ratio} {flag} {location}"
            """);
        Checking.AssertMarked("let xs = [1]\nlet s = \"{‸xs}\"", "SCRIPTS-TYPE-0023");
        Checking.AssertMarked("let r = \"1\".parse_integer().value_or(1)\nlet q = \"1\".parse_integer()\nlet s = \"{‸q}\"", "SCRIPTS-TYPE-0023");
        Checking.AssertMarked("let s = \"{‸io.write_standard_output_line(\"a\")}\"", "SCRIPTS-TYPE-0023");
    }
}
