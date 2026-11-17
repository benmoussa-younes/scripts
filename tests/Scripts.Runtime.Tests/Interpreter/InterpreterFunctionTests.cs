using Scripts.Runtime.Interpreter;

namespace Scripts.Runtime.Tests.InterpreterRuns;

/// <summary>Functions, closures, callbacks and error propagation (DESIGN 3.6, 3.7, 4.3, 4.4, 5.3, 5.4).</summary>
public sealed class InterpreterFunctionTests
{
    [Fact]
    public void RecursionAndMutualRecursionOfHoistedFunctions()
    {
        const string program = """
            io.write_standard_output_line("{is_even(10)} {is_odd(10)} {fib(20)} {factorial(10)}")
            function is_even(value: integer) -> boolean {
                if value == 0 {
                    return true
                }
                return is_odd(value - 1)
            }
            function is_odd(value: integer) -> boolean {
                if value == 0 {
                    return false
                }
                return is_even(value - 1)
            }
            function fib(value: integer) -> integer {
                if value < 2 {
                    return value
                }
                return fib(value - 1) + fib(value - 2)
            }
            function factorial(value: integer) -> integer {
                return match value {
                    0 => 1
                    _ => value * factorial(value - 1)
                }
            }
            """;

        Assert.Equal("true false 6765 3628800\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void FunctionsWithoutReturnYieldUnitAndBareReturnLeaves()
    {
        const string program = """
            function announce(message: string) {
                io.write_standard_output_line("> {message}")
                if message == "stop" {
                    return
                }
                io.write_standard_output_line("continued")
            }
            announce("go")
            announce("stop")
            """;

        Assert.Equal(InterpreterHarness.Lines("> go", "continued", "> stop"), InterpreterHarness.Output(program));
    }

    [Fact]
    public void ConstantsAreVisibleInFunctionsRegardlessOfOrder()
    {
        const string program = """
            function describe() -> string {
                return "{greeting}, {limit * 2} {names.length()}"
            }
            io.write_standard_output_line(describe())
            let greeting = "hello"
            let limit = 20 + 1
            let names = ["a", "b"]
            """;

        Assert.Equal("hello, 42 2\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void ConstantsReferenceEarlierConstants()
    {
        const string program = """
            let base = 10
            let doubled = base * 2
            let label = "n={doubled}"
            let table = {"k": doubled}
            io.write_standard_output_line("{label} {table["k"]}")
            """;

        Assert.Equal("n=20 20\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void ScriptVariablesAreVisibleToLaterStatementsAndLambdas()
    {
        const string program = """
            let started = process.run(process.request("echo", ["go"]))?.standard_output.trim_whitespace()
            let mutable counter = 0
            let bump = () => counter + 1
            counter = 41
            io.write_standard_output_line("{started} {bump()}")
            """;

        Assert.Equal("go 42\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void WhileClosuresShareTheVariableWhileForClosuresGetOneBindingPerIteration()
    {
        const string program = """
            let mutable while_closures: list<function() -> integer> = []
            let mutable counter = 0
            while counter < 3 {
                while_closures.append(() => counter)
                counter += 1
            }
            let mutable for_closures: list<function() -> integer> = []
            for index in range(0, 3) {
                for_closures.append(() => index)
            }
            let mutable seen: list<string> = []
            for closure in while_closures {
                seen.append("w{closure()}")
            }
            for closure in for_closures {
                seen.append("f{closure()}")
            }
            io.write_standard_output_line(seen.join(" "))
            """;

        Assert.Equal("w3 w3 w3 f0 f1 f2\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void NestedFunctionsCaptureByReferenceAndKeepTheirOwnState()
    {
        const string program = """
            function make_counter() -> function() -> integer {
                let mutable count = 0
                function next() -> integer {
                    count += 1
                    return count
                }
                return next
            }
            let first = make_counter()
            let second = make_counter()
            io.write_standard_output_line("{first()} {first()} {second()} {first()}")
            """;

        Assert.Equal("1 2 1 3\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void LambdaSeesLaterAssignmentsOfACapturedVariable()
    {
        const string program = """
            let mutable value = 1
            let read = () => value
            value = 5
            let before = read()
            value = 6
            io.write_standard_output_line("{before} {read()}")
            """;

        Assert.Equal("5 6\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void NestedFunctionsCanRecurseAndClosuresCanReturnClosures()
    {
        const string program = """
            function make_adder(amount: integer) -> function(integer) -> integer {
                return value => value + amount
            }
            function total_of(limit: integer) -> integer {
                function sum_to(current: integer) -> integer {
                    if current == 0 {
                        return 0
                    }
                    return current + sum_to(current - 1)
                }
                return sum_to(limit)
            }
            let add_five = make_adder(5)
            io.write_standard_output_line("{add_five(1)} {make_adder(10)(2)} {(value => value * 3)(4)} {total_of(100)}")
            """;

        Assert.Equal("6 12 12 5050\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void LambdasWithTypedAndDiscardedParameters()
    {
        const string program = """
            let add = (first: integer, second: integer) => first + second
            let constant = _ => 7
            let pair_sum = (_, second) => second
            io.write_standard_output_line("{add(2, 3)} {constant("x")} {pair_sum(1, 9)}")
            """;

        Assert.Equal("5 7 9\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void NamedFunctionsAreValues()
    {
        const string program = """
            function is_big(value: integer) -> boolean {
                return value > 2
            }
            function double(value: integer) -> integer {
                return value * 2
            }
            function add(left: integer, right: integer) -> integer {
                return left + right
            }
            let numbers = [1, 2, 3, 4]
            let chosen = double
            io.write_standard_output_line("{numbers.filter(is_big).length()} {numbers.transform(double).fold(0, add)} {chosen(21)}")
            """;

        Assert.Equal("2 20 42\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void ListMethodsWithCallbacks()
    {
        const string program = """
            let words = ["pear", "fig", "apple"]
            let lengths = words.transform(word => word.length())
            let total = lengths.fold(0, (accumulator, length) => accumulator + length)
            io.write_standard_output_line("{lengths.length()} {total} {words.sorted().join(",")} {words.reversed().join(",")}")
            io.write_standard_output_line(words.filter(word => word.contains("p")).join("+"))
            """;

        Assert.Equal(
            InterpreterHarness.Lines("3 12 apple,fig,pear apple,fig,pear", "pear+apple"),
            InterpreterHarness.Output(program));
    }

    [Fact]
    public void QuestionMarkPropagatesAlongAFileReadChain()
    {
        const string program = """
            let temporary_directory = filesystem.make_temporary_directory("scripts-pipe")?
            let target = temporary_directory / "out.txt"
            filesystem.write_file_overwrite("line one\nline two\n", target)?
            let count = filesystem.read_file(target)?.lines().length()
            io.write_standard_output_line("{count}")
            filesystem.remove_directory_tree(temporary_directory)?
            """;

        Assert.Equal("2\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void QuestionMarkPropagatesFromFunctionsAtEveryStatementShape()
    {
        const string program = """
            function parse(text: string) -> result<integer, error> {
                return text.parse_integer()
            }
            function check(text: string) -> result<unit, error> {
                let _ = parse(text)?
                return ok()
            }
            function statement_form(text: string) -> result<unit, error> {
                check(text)?
                io.write_standard_output_line("statement ok")
                return ok()
            }
            function let_form(text: string) -> result<integer, error> {
                let value = parse(text)?
                io.write_standard_output_line("let ok")
                return ok(value + 1)
            }
            function return_form(text: string) -> result<integer, error> {
                return parse(text)
            }
            function assign_form(text: string) -> result<integer, error> {
                let mutable total = 0
                total = parse(text)?
                io.write_standard_output_line("assign ok")
                return ok(total)
            }
            function nested_form(first: string, second: string) -> result<integer, error> {
                let total = parse(first)? + parse(second)?
                io.write_standard_output_line("nested ok")
                return ok(total)
            }
            function argument_form(first: string, second: string) -> result<integer, error> {
                let total = [parse(first)?, parse(second)?].length()
                io.write_standard_output_line("argument ok")
                return ok(total)
            }
            function loop_form(texts: list<string>) -> result<integer, error> {
                let mutable total = 0
                for text in texts {
                    total += parse(text)?
                }
                return ok(total)
            }
            function show(outcome: result<integer, error>) -> string {
                return match outcome {
                    ok(value) => "ok {value}"
                    error(failure) => "error {failure.kind}"
                }
            }
            io.write_standard_output_line(show(let_form("4")))
            io.write_standard_output_line(show(let_form("x")))
            io.write_standard_output_line(show(assign_form("5")))
            io.write_standard_output_line(show(assign_form("y")))
            io.write_standard_output_line(show(nested_form("1", "2")))
            io.write_standard_output_line(show(nested_form("1", "z")))
            io.write_standard_output_line(show(argument_form("1", "2")))
            io.write_standard_output_line(show(argument_form("w", "2")))
            io.write_standard_output_line(show(loop_form(["1", "2", "3"])))
            io.write_standard_output_line(show(loop_form(["1", "bad", "3"])))
            io.write_standard_output_line("{statement_form("1").is_ok()} {not statement_form("q").is_ok()}")
            """;

        Assert.Equal(
            InterpreterHarness.Lines(
                "let ok",
                "ok 5",
                "error invalid_integer",
                "assign ok",
                "ok 5",
                "error invalid_integer",
                "nested ok",
                "ok 3",
                "error invalid_integer",
                "argument ok",
                "ok 2",
                "error invalid_integer",
                "ok 6",
                "error invalid_integer",
                "statement ok",
                "true true"),
            InterpreterHarness.Output(program));
    }

    [Fact]
    public void QuestionMarkInsideMatchArmBlocksAndAfterMapError()
    {
        const string program = """
            function convert(text: string) -> result<integer, string> {
                let value = text.parse_integer().transform_error(failure => "bad: {failure.message}")?
                match value {
                    0 => {
                        return error("zero")
                    }
                    _ => {
                        let doubled = text.parse_integer().transform_error(failure => "again")? * 2
                        return ok(doubled)
                    }
                }
            }
            function show(outcome: result<integer, string>) -> string {
                return match outcome {
                    ok(value) => "ok {value}"
                    error(text) => "error {text.length() > 0}"
                }
            }
            io.write_standard_output_line("{show(convert("21"))} {show(convert("0"))} {show(convert("nope"))}")
            """;

        Assert.Equal("ok 42 error true error true\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void ManyFailingPropagationsStayFast()
    {
        const string program = """
            function fail(value: integer) -> result<integer, string> {
                return error("no")
            }
            function attempt(value: integer) -> result<integer, string> {
                let parsed = fail(value)?
                return ok(parsed)
            }
            let mutable failures = 0
            for value in range(0, 20000) {
                if not attempt(value).is_ok() {
                    failures += 1
                }
            }
            io.write_standard_output_line("{failures}")
            """;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var output = InterpreterHarness.Output(program);
        stopwatch.Stop();

        Assert.Equal("20000\n", output);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void ModulesInitializeBeforeTheImporterAndExposeFunctionsAndConstants()
    {
        var run = InterpreterHarness.Run(
            """
            import "lib/math.scs" as math
            import "lib/text.scs" as text
            io.write_standard_output_line("{math.square(7)} {math.LIMIT} {text.shout("hi")} {text.WIDTH_TEXT}")
            io.write_standard_output_line("{math.square(4)} {math.double_limit()}")
            """,
            ("/lib/math.scs", """
                public let LIMIT = 10
                let hidden = 3
                public function square(value: integer) -> integer {
                    return value * value + hidden - hidden
                }
                public function double_limit() -> integer {
                    return LIMIT * 2
                }
                """),
            ("/lib/text.scs", """
                import "math.scs" as numbers
                public let WIDTH_TEXT = "w{numbers.LIMIT + 1}"
                function exclaim(word: string) -> string {
                    return word + "!"
                }
                public function shout(word: string) -> string {
                    return exclaim(word.uppercased())
                }
                """));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(InterpreterHarness.Lines("49 10 HI! w11", "16 20"), run.StandardOutput);
    }

    [Fact]
    public void ModuleFunctionsOnlySeeTheirOwnFileAndCallbacksCrossModules()
    {
        var run = InterpreterHarness.Run(
            """
            import "tools.scs" as tools
            let offset = 100
            io.write_standard_output_line("{tools.apply_twice(value => value + offset, 1)} {tools.offset_value()}")
            """,
            ("/tools.scs", """
                let offset = 5
                public function apply_twice(step: function(integer) -> integer, start: integer) -> integer {
                    return step(step(start))
                }
                public function offset_value() -> integer {
                    return offset
                }
                """));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("201 5\n", run.StandardOutput);
    }

    [Fact]
    public void ASharedModuleIsInitializedOnce()
    {
        var run = InterpreterHarness.Run(
            """
            import "left.scs" as left
            import "right.scs" as right
            io.write_standard_output_line("{left.value()} {right.value()}")
            """,
            ("/left.scs", """
                import "shared.scs" as shared
                public function value() -> integer {
                    return shared.COUNTER + 1
                }
                """),
            ("/right.scs", """
                import "shared.scs" as shared
                public function value() -> integer {
                    return shared.COUNTER + 2
                }
                """),
            ("/shared.scs", """
                public let COUNTER = 40
                """));

        Assert.Equal("41 42\n", run.StandardOutput);
    }

    [Fact]
    public void FunctionValuesCannotReachTheJsonResultButTheResultOfCallsCan()
    {
        var run = InterpreterHarness.Run("function twice(value: integer) -> integer {\n    return value * 2\n}\nreturn twice(21)\n");

        Assert.Equal("42", ResultJson.Render(run.Outcome.Result!));
    }
}
