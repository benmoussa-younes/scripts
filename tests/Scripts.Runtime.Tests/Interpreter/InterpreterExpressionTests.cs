using Scripts.Core;

namespace Scripts.Runtime.Tests.InterpreterRuns;

/// <summary>Operators and expression forms on every operand type (DESIGN 3.2, 5.5, 5.6).</summary>
public sealed class InterpreterExpressionTests
{
    private static string Show(string expression) =>
        InterpreterHarness.Output("let result = " + expression + "\nio.write_standard_output_line(\"{result}\")");

    [Theory]
    [InlineData("7 + 3", "10")]
    [InlineData("7 - 10", "-3")]
    [InlineData("6 * 7", "42")]
    [InlineData("7 / 2", "3")]
    [InlineData("-7 / 2", "-3")]
    [InlineData("7 / -2", "-3")]
    [InlineData("-7 / -2", "3")]
    [InlineData("7 % 3", "1")]
    [InlineData("-7 % 3", "-1")]
    [InlineData("7 % -3", "1")]
    [InlineData("-7 % -3", "-1")]
    [InlineData("2 + 3 * 4", "14")]
    [InlineData("(2 + 3) * 4", "20")]
    [InlineData("10 - 4 - 3", "3")]
    [InlineData("100 / 10 / 5", "2")]
    [InlineData("-(3 + 4)", "-7")]
    [InlineData("- -5", "5")]
    [InlineData("0xFF + 0b101 + 0o17", "275")]
    [InlineData("1_000 * 3", "3000")]
    [InlineData("9223372036854775807 - 1 + 1", "9223372036854775807")]
    [InlineData("-9223372036854775807 - 1", "-9223372036854775808")]
    public void IntegerArithmetic(string expression, string expected) =>
        Assert.Equal(expected + "\n", Show(expression));

    [Theory]
    [InlineData("1.5 + 2.25", "3.75")]
    [InlineData("0.1 + 0.2", "0.30000000000000004")]
    [InlineData("1.0 / 4.0", "0.25")]
    [InlineData("1.0 / 3.0", "0.3333333333333333")]
    [InlineData("2.5 * 2.0", "5.0")]
    [InlineData("10.0 - 0.5", "9.5")]
    [InlineData("-(1.5)", "-1.5")]
    [InlineData("-0.0", "-0.0")]
    [InlineData("0.0 - 0.0", "0.0")]
    [InlineData("1e20 * 1.0", "1.0E+20")]
    [InlineData("1.5e-7 * 1.0", "1.5E-7")]
    [InlineData("1e15 * 1.0", "1000000000000000.0")]
    [InlineData("1e16 * 1.0", "1.0E+16")]
    [InlineData("0.00001 * 1.0", "0.00001")]
    [InlineData("0.000001 * 1.0", "1.0E-6")]
    [InlineData("1e300 * 1e-300", "1.0")]
    public void FloatArithmeticAndRendering(string expression, string expected) =>
        Assert.Equal(expected + "\n", Show(expression));

    [Theory]
    [InlineData("1 < 2", "true")]
    [InlineData("2 < 2", "false")]
    [InlineData("2 <= 2", "true")]
    [InlineData("3 > 4", "false")]
    [InlineData("4 >= 4", "true")]
    [InlineData("-1 < 0", "true")]
    [InlineData("1.5 < 2.5", "true")]
    [InlineData("1.5 >= 1.5", "true")]
    [InlineData("-0.0 == 0.0", "true")]
    [InlineData("\"a\" < \"b\"", "true")]
    [InlineData("\"b\" < \"a\"", "false")]
    [InlineData("\"abc\" < \"abd\"", "true")]
    [InlineData("\"Z\" < \"a\"", "true")]
    [InlineData("\"\" < \"a\"", "true")]
    [InlineData("\"\\u{FFFF}\" < \"\\u{10000}\"", "false")]
    [InlineData("p\"a\" < p\"b\"", "true")]
    [InlineData("p\"/a\" >= p\"/a\"", "true")]
    public void OrderedComparisons(string expression, string expected) =>
        Assert.Equal(expected + "\n", Show(expression));

    [Theory]
    [InlineData("1 == 1", "true")]
    [InlineData("1 != 1", "false")]
    [InlineData("1.0 == 1.0", "true")]
    [InlineData("\"a\" == \"a\"", "true")]
    [InlineData("\"a\" != \"b\"", "true")]
    [InlineData("true == false", "false")]
    [InlineData("true != false", "true")]
    [InlineData("p\"a/b\" == p\"a/b\"", "true")]
    [InlineData("p\"a\" == p\"b\"", "false")]
    [InlineData("[1, 2] == [1, 2]", "true")]
    [InlineData("[1, 2] == [2, 1]", "false")]
    [InlineData("[1] != [1, 2]", "true")]
    [InlineData("[[1], [2]] == [[1], [2]]", "true")]
    [InlineData("{\"a\": 1, \"b\": 2} == {\"b\": 2, \"a\": 1}", "true")]
    [InlineData("{\"a\": 1} == {\"a\": 2}", "false")]
    [InlineData("{\"a\": [1]} == {\"a\": [1]}", "true")]
    [InlineData("{1: \"x\"} != {2: \"x\"}", "true")]
    [InlineData("[\"a\"] + [\"b\"] == [\"a\", \"b\"]", "true")]
    public void StructuralEquality(string expression, string expected) =>
        Assert.Equal(expected + "\n", Show(expression));

    [Theory]
    [InlineData("true and false", "false")]
    [InlineData("true and true", "true")]
    [InlineData("false or true", "true")]
    [InlineData("false or false", "false")]
    [InlineData("not true", "false")]
    [InlineData("not false", "true")]
    [InlineData("not 1 == 2", "true")]
    [InlineData("(true or false) and (false or true)", "true")]
    [InlineData("1 < 2 and 2 < 3", "true")]
    [InlineData("1 > 2 or 2 > 1", "true")]
    public void BooleanOperators(string expression, string expected) =>
        Assert.Equal(expected + "\n", Show(expression));

    [Fact]
    public void AndAndOrShortCircuit()
    {
        const string program = """
            function noisy(label: string, value: boolean) -> boolean {
                io.write_standard_output_line("evaluated {label}")
                return value
            }
            let first = noisy("a", false) and noisy("b", true)
            let second = noisy("c", true) or noisy("d", false)
            let third = noisy("e", true) and noisy("f", true)
            let fourth = noisy("g", false) or noisy("h", true)
            io.write_standard_output_line("{first} {second} {third} {fourth}")
            """;

        Assert.Equal(
            InterpreterHarness.Lines("evaluated a", "evaluated c", "evaluated e", "evaluated f", "evaluated g", "evaluated h", "false true true true"),
            InterpreterHarness.Output(program));
    }

    [Theory]
    [InlineData("\"ab\" + \"cd\"", "abcd")]
    [InlineData("\"\" + \"x\"", "x")]
    [InlineData("\"é\" + \"ü\"", "éü")]
    [InlineData("\"a\" + \"b\" + \"c\"", "abc")]
    [InlineData("\"n={1 + 1}\"", "n=2")]
    [InlineData("r\"{0}\\n\"", "{0}\\n")]
    [InlineData("\"tab\\tquote\\\"brace\\{\"", "tab\tquote\"brace{")]
    [InlineData("\"\\u{1F600}\"", "\U0001F600")]
    public void StringsAndInterpolation(string expression, string expected) =>
        Assert.Equal(expected + "\n", Show(expression));

    [Fact]
    public void InterpolationRendersEveryInterpolableType()
    {
        const string program = """
            let text = "s"
            let number = 42
            let negative = -7
            let ratio = 2.5
            let flag = true
            let location = p"/usr/bin"
            io.write_standard_output_line("{text} {number} {negative} {ratio} {flag} {location}")
            """;

        Assert.Equal("s 42 -7 2.5 true /usr/bin\n", InterpreterHarness.Output(program));
    }

    [Theory]
    [InlineData("p\"/a\" / \"b\"", "/a/b")]
    [InlineData("p\"/a/\" / p\"b\"", "/a/b")]
    [InlineData("p\"\" / \"x\"", "x")]
    [InlineData("p\"a\" / \"b\" / \"c\"", "a/b/c")]
    [InlineData("p\"a/.\" / \"..\"", "a/./..")]
    public void PathDivision(string expression, string expected) =>
        Assert.Equal(expected + "\n", Show(expression));

    [Theory]
    [InlineData("\"/etc\"", "`/etc`")]
    [InlineData("\"\"", "`\"\"`")]
    public void ConstantPathDivisionWrapsTheJoinFailureInRuntime0020(string segment, string displayed)
    {
        var source = $"let segment = {segment}\nlet joined = p\"/srv\" / segment";
        var run = InterpreterHarness.AssertFailure(source, ErrorCodes.Runtime0020, "2:1");
        Assert.Contains(
            $"the right operand of `/` on a path is {displayed}: it is absolute or empty",
            run.Outcome.Failure!.Message,
            StringComparison.Ordinal);
        Assert.Equal(ExitCodes.RuntimeFailure, run.ExitCode);
    }

    [Theory]
    [InlineData("\"/etc\"", "`/etc`")]
    [InlineData("\"\"", "`\"\"`")]
    public void CompoundPathDivisionOfAComputedSegmentIsFatal(string segment, string displayed)
    {
        var source = $"let mutable base = p\"/srv\"\nlet segment = {segment}\nbase /= segment";
        var run = InterpreterHarness.AssertFailure(source, ErrorCodes.Runtime0022, "3:1");
        Assert.Contains(
            $"the right operand of `/` on a path is {displayed}: it is absolute or empty",
            run.Outcome.Failure!.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ListAndMapLiteralsAndIndexing()
    {
        const string program = """
            let numbers = [10, 20, 30]
            let names = {"a": 1, "b": 2}
            let by_number = {1: "one", 2: "two"}
            let nested = [[1, 2], [3, 4]]
            io.write_standard_output_line("{numbers[0]} {numbers[2]} {names["b"]} {by_number[2]} {nested[1][0]}")
            io.write_standard_output_line("{numbers.length()} {names.length()} {([1] + [2, 3]).length()}")
            """;

        Assert.Equal("10 30 2 two 3\n3 2 3\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void MapLiteralKeepsInsertionOrderAndLastValueFirstPosition()
    {
        const string program = """
            let key_text = "a".trim_whitespace()
            let entries = {"x": 1, "y": 2, key_text: 3, "a": 4}
            io.write_standard_output_line(entries.keys().join(","))
            io.write_standard_output_line("{entries["a"]}")
            """;

        Assert.Equal("x,y,a\n4\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void RecordFieldsAreReadable()
    {
        const string program = """
            let outcome = process.run(process.request("echo", ["hi"]))?
            io.write_standard_output_line("{outcome.exit_code} {outcome.standard_output.trim_whitespace()} [{outcome.standard_error}]")
            """;

        Assert.Equal("0 hi []\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void ResultConstructorsAndMethods()
    {
        const string program = """
            let success: result<integer, string> = ok(5)
            let failure: result<integer, string> = error("bad")
            let nothing: result<unit, string> = ok()
            io.write_standard_output_line("{success.is_ok()} {not failure.is_ok()} {nothing.is_ok()}")
            io.write_standard_output_line("{success.value_or(0)} {failure.value_or(9)} {success.value_or_fail()}")
            let converted = failure.transform_error(reason => reason + "!")
            match converted {
                ok(_) => io.write_standard_output_line("unexpected")
                error(text) => io.write_standard_output_line(text)
            }
            """;

        Assert.Equal(InterpreterHarness.Lines("true true true", "5 9 5", "bad!"), InterpreterHarness.Output(program));
    }

    [Fact]
    public void EvaluationOrderIsLeftToRight()
    {
        const string program = """
            function trace(label: string, value: integer) -> integer {
                io.write_standard_output_line(label)
                return value
            }
            let sum = trace("a", 1) + trace("b", 2) * trace("c", 3)
            let items = [trace("d", 1), trace("e", 2)]
            let mapping = {"k": trace("f", 1), "j": trace("g", 2)}
            let lookup = items[trace("h", 0)] + trace("i", 5)
            io.write_standard_output_line("{sum} {items.length()} {mapping.length()} {lookup}")
            """;

        Assert.Equal(
            InterpreterHarness.Lines("a", "b", "c", "d", "e", "f", "g", "h", "i", "7 2 2 6"),
            InterpreterHarness.Output(program));
    }

    [Fact]
    public void CallArgumentsAreEvaluatedLeftToRight()
    {
        const string program = """
            function trace(label: string, value: integer) -> integer {
                io.write_standard_output_line(label)
                return value
            }
            function combine(first: integer, second: integer, third: integer) -> integer {
                return first * 100 + second * 10 + third
            }
            io.write_standard_output_line("{combine(trace("x", 1), trace("y", 2), trace("z", 3))}")
            """;

        Assert.Equal(InterpreterHarness.Lines("x", "y", "z", "123"), InterpreterHarness.Output(program));
    }

    [Fact]
    public void NamedArgumentsAreEvaluatedInSourceOrderAndBoundByName()
    {
        const string program = """
            function trace(label: string, value: integer) -> integer {
                io.write_standard_output_line(label)
                return value
            }
            function combine(first: integer, second: integer, third: integer) -> integer {
                return first * 100 + second * 10 + third
            }
            io.write_standard_output_line("{combine(third = trace("x", 3), first = trace("y", 1), second = trace("z", 2))}")
            """;

        Assert.Equal(InterpreterHarness.Lines("x", "y", "z", "123"), InterpreterHarness.Output(program));
    }

    [Fact]
    public void NamedArgumentsWorkOnModuleFunctionsAndGenericMethods()
    {
        const string program = """
            let request = process.request(arguments = ["ok"], program = "printf")
            match process.run(request) {
                ok(result) => io.write_standard_output_line("[{result.standard_output}]")
                error(failure) => io.write_standard_output_line(failure.kind)
            }
            let values = [1, 2, 3]
            io.write_standard_output_line("{values.transform(mapper = n => n * 2).length()}")
            """;

        Assert.Equal(InterpreterHarness.Lines("[ok]", "3"), InterpreterHarness.Output(program));
    }

    [Fact]
    public void NamedArgumentsBindMethodAndModuleParametersByName()
    {
        const string program = """
            match "abcdef".substring(count = 2, start_index = 1) {
                ok(part) => io.write_standard_output_line("[{part}]")
                error(e) => io.write_standard_output_line(e.kind)
            }
            io.write_standard_output_line("{range(end_value = 3, start_value = 0).length()}")
            """;

        Assert.Equal(InterpreterHarness.Lines("[bc]", "3"), InterpreterHarness.Output(program));
    }
}
