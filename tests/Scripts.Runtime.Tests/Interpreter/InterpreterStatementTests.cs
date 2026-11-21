using Scripts.Core.Reporting;
using Scripts.Runtime.Interpreter;

namespace Scripts.Runtime.Tests.InterpreterRuns;

/// <summary>Statements, control flow, match and the top-level protocol (DESIGN 3.6, 3.7, 4.1, 5.1, 5.2).</summary>
public sealed class InterpreterStatementTests
{
    [Fact]
    public void CompoundAssignmentOnEveryOperatorAndType()
    {
        const string program = """
            let mutable number = 10
            number += 5
            number -= 3
            number *= 2
            number /= 5
            number %= 3
            let mutable text = "a"
            text += "b"
            let mutable location = p"/x"
            location /= "y"
            let mutable numbers = [1]
            numbers += [2, 3]
            let mutable ratio = 1.5
            ratio *= 2.0
            ratio -= 0.5
            ratio /= 5.0
            ratio += 0.5
            io.write_standard_output_line("{number} {text} {location} {numbers.length()} {ratio}")
            """;

        Assert.Equal("1 ab /x/y 3 1.0\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void CompoundAssignmentReadsTheTargetBeforeEvaluatingTheValue()
    {
        const string program = """
            function run() -> integer {
                let mutable number = 1
                function bump() -> integer {
                    number = 100
                    return 1
                }
                number += bump()
                return number
            }
            io.write_standard_output_line("{run()}")
            """;

        Assert.Equal("2\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void IfElseChainsPickTheFirstTrueBranch()
    {
        const string program = """
            function classify(value: integer) -> string {
                if value < 0 {
                    return "negative"
                } else if value == 0 {
                    return "zero"
                } else if value < 10 {
                    return "small"
                } else {
                    return "large"
                }
            }
            for value in [-5, 0, 3, 50] {
                io.write_standard_output_line(classify(value))
            }
            """;

        Assert.Equal(InterpreterHarness.Lines("negative", "zero", "small", "large"), InterpreterHarness.Output(program));
    }

    [Fact]
    public void IfWithoutElseAndElseOnNextLine()
    {
        const string program = """
            let mutable seen = ""
            if 1 > 2 {
                seen += "a"
            }
            if 2 > 1 {
                seen += "b"
            }
            else {
                seen += "c"
            }
            io.write_standard_output_line(seen)
            """;

        Assert.Equal("b\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void WhileSupportsBreakAndContinue()
    {
        const string program = """
            let mutable index = 0
            let mutable total = 0
            while true {
                index += 1
                if index > 10 {
                    break
                }
                if index % 2 == 0 {
                    continue
                }
                total += index
            }
            io.write_standard_output_line("{index} {total}")
            """;

        Assert.Equal("11 25\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void ForSupportsBreakContinueAndNestedLoops()
    {
        const string program = """
            let mutable lines: list<string> = []
            for outer in [1, 2, 3] {
                for inner in [1, 2, 3] {
                    if inner == 2 {
                        continue
                    }
                    if inner == 3 and outer == 2 {
                        break
                    }
                    lines.append("{outer}{inner}")
                }
                if outer == 3 {
                    break
                }
            }
            io.write_standard_output_line(lines.join(","))
            """;

        Assert.Equal("11,13,21,31,33\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void ForIteratesAnInstantaneousSnapshot()
    {
        const string program = """
            let mutable items = [1, 2, 3]
            let mutable seen = 0
            for item in items {
                items.append(item * 10)
                seen += 1
            }
            io.write_standard_output_line("{seen} {items.length()}")
            """;

        Assert.Equal("3 6\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void ForWithDiscardVariableAndLetDiscard()
    {
        const string program = """
            function side_effect() -> integer {
                io.write_standard_output_line("called")
                return 1
            }
            let mutable count = 0
            for _ in range(0, 4) {
                count += 1
            }
            let _ = side_effect()
            io.write_standard_output_line("{count}")
            """;

        Assert.Equal(InterpreterHarness.Lines("called", "4"), InterpreterHarness.Output(program));
    }

    [Fact]
    public void ForOverMapKeysFollowsInsertionOrder()
    {
        const string program = """
            let mutable ages = {"zoe": 1, "amy": 2}
            ages["bob"] = 3
            ages["zoe"] = 4
            for name in ages.keys() {
                io.write_standard_output_line("{name}={ages[name]}")
            }
            """;

        Assert.Equal(InterpreterHarness.Lines("zoe=4", "amy=2", "bob=3"), InterpreterHarness.Output(program));
    }

    [Fact]
    public void ReturnInsideLoopsLeavesTheFunction()
    {
        const string program = """
            function first_even(values: list<integer>) -> integer {
                for value in values {
                    let mutable attempts = 0
                    while true {
                        attempts += 1
                        if attempts > 1 {
                            break
                        }
                    }
                    if value % 2 == 0 {
                        return value
                    }
                }
                return -1
            }
            io.write_standard_output_line("{first_even([3, 5, 8, 10])} {first_even([1, 3])}")
            """;

        Assert.Equal("8 -1\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void MatchOnIntegersWithLiteralsNegativesAlternativesAndWildcard()
    {
        const string program = """
            function name(value: integer) -> string {
                return match value {
                    0 => "zero"
                    -1 => "minus one"
                    1 | 2 | 3 => "small"
                    _ => "other"
                }
            }
            for value in [0, -1, 2, 3, 99] {
                io.write_standard_output_line(name(value))
            }
            """;

        Assert.Equal(InterpreterHarness.Lines("zero", "minus one", "small", "small", "other"), InterpreterHarness.Output(program));
    }

    [Fact]
    public void MatchOnStringsBooleansAndBindings()
    {
        const string program = """
            function answer(flag: boolean) -> string {
                return match flag {
                    true => "yes"
                    false => "no"
                }
            }
            function echo(word: string) -> string {
                return match word {
                    "a" | "b" => "early"
                    other => "got {other}"
                }
            }
            io.write_standard_output_line("{answer(true)} {answer(false)} {echo("a")} {echo("z")}")
            """;

        Assert.Equal("yes no early got z\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void MatchTestsArmsInOrder()
    {
        const string program = """
            let label = match 5 {
                captured => "binding {captured}"
                5 => "literal"
            }
            io.write_standard_output_line(label)
            """;

        Assert.Equal("binding 5\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void MatchOnResultsWithWildcardAndBinding()
    {
        const string program = """
            function show(outcome: result<integer, string>) -> string {
                return match outcome {
                    ok(value) => "ok {value}"
                    error(_) => "failed"
                }
            }
            function describe(outcome: result<integer, string>) -> string {
                match outcome {
                    ok(_) => {
                        return "fine"
                    }
                    error(reason) => {
                        return "bad {reason}"
                    }
                }
            }
            io.write_standard_output_line("{show(ok(1))} {show(error("x"))} {describe(ok(2))} {describe(error("y"))}")
            """;

        Assert.Equal("ok 1 failed fine bad y\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void MatchStatementArmsCanBreakContinueAndReturn()
    {
        const string program = """
            function scan(values: list<integer>) -> integer {
                let mutable total = 0
                for value in values {
                    match value {
                        0 => {
                            continue
                        }
                        99 => {
                            break
                        }
                        -1 => {
                            return -1
                        }
                        _ => {
                            total += value
                        }
                    }
                }
                return total
            }
            io.write_standard_output_line("{scan([1, 0, 2, 99, 100])} {scan([5, -1, 7])}")
            """;

        Assert.Equal("3 -1\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void MatchStatementWithExpressionArmsCallsTheArm()
    {
        const string program = """
            for value in [1, 2, 3] {
                match value {
                    1 => io.write_standard_output_line("one")
                    2 => io.write_standard_output_line("two")
                    _ => io.write_standard_output_line("many")
                }
            }
            """;

        Assert.Equal(InterpreterHarness.Lines("one", "two", "many"), InterpreterHarness.Output(program));
    }

    [Fact]
    public void NestedMatchExpressions()
    {
        const string program = """
            function pick(first: integer, second: integer) -> string {
                return match first {
                    0 => match second {
                        0 => "zero-zero"
                        _ => "zero-any"
                    }
                    _ => "other"
                }
            }
            io.write_standard_output_line("{pick(0, 0)} {pick(0, 1)} {pick(1, 0)}")
            """;

        Assert.Equal("zero-zero zero-any other\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void MatchWithoutAMatchingArmIsAnInternalError()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => InterpreterHarness.Run("match 3 {\n    1 => io.write_standard_output_line(\"one\")\n}\n"));
        Assert.Contains("match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TopLevelReturnGivesTheResultValueAndStopsTheScript()
    {
        var run = InterpreterHarness.Run("io.write_standard_output_line(\"before\")\nreturn 40 + 2\nio.write_standard_output_line(\"after\")\n");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(RunTermination.Normal, run.Outcome.Termination);
        Assert.Null(run.Outcome.Failure);
        Assert.Equal("before\n", run.StandardOutput);
        Assert.Equal(new IntegerValue(42), run.Outcome.Result);
    }

    [Fact]
    public void TopLevelBareReturnYieldsUnitAndNoReturnYieldsNull()
    {
        Assert.Equal(UnitValue.Instance, InterpreterHarness.Run("return\n").Outcome.Result);
        Assert.Null(InterpreterHarness.Run("let x = 1\n").Outcome.Result);
    }

    [Fact]
    public void TopLevelReturnInsideALoopEndsTheScriptSuccessfully()
    {
        var run = InterpreterHarness.Run("for value in [1, 2, 3] {\n    if value == 2 {\n        return value\n    }\n}\nio.write_standard_output_line(\"never\")\n");

        Assert.Equal(new IntegerValue(2), run.Outcome.Result);
        Assert.Equal(string.Empty, run.StandardOutput);
    }

    [Fact]
    public void TopLevelReturnOfAnErrorValueIsStillASuccess()
    {
        var run = InterpreterHarness.Run("let failure: result<integer, string> = error(\"x\")\nreturn failure\n");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(ResultValue.Error(new StringValue("x")), run.Outcome.Result);
    }

    [Fact]
    public void ExitScriptEndsWithItsCodeAfterPreviousOutput()
    {
        var run = InterpreterHarness.Run("io.write_standard_output_line(\"bye\")\nprocess.exit_with_code(3)\nio.write_standard_output_line(\"never\")\n");

        Assert.Equal(3, run.ExitCode);
        Assert.Equal(RunTermination.ExitScript, run.Outcome.Termination);
        Assert.Null(run.Outcome.Failure);
        Assert.Null(run.Outcome.Result);
        Assert.Equal("bye\n", run.StandardOutput);
    }

    [Fact]
    public void ExitScriptZeroIsStillExitScript()
    {
        var run = InterpreterHarness.Run("process.exit_with_code(0)\n");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(RunTermination.ExitScript, run.Outcome.Termination);
    }

    [Fact]
    public void ExitScriptAcceptsTheHighestScriptCode()
    {
        var run = InterpreterHarness.Run("process.exit_with_code(255)\n");

        Assert.Equal(255, run.ExitCode);
        Assert.Equal(RunTermination.ExitScript, run.Outcome.Termination);
        Assert.Null(run.Outcome.Failure);
    }

    [Fact]
    public void ExitScriptAboveTheHighestScriptCodeFailsWithRuntime0016()
    {
        var run = InterpreterHarness.AssertFailure("process.exit_with_code(256)\n", "SCRIPTS-RUNTIME-0016", "1:1");

        Assert.Contains("0..255 expected", run.Outcome.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExitWithMessageWritesTheMessageOnStandardErrorAndEndsWithItsCode()
    {
        var run = InterpreterHarness.Run("process.defer(() => io.write_standard_output_line(\"cleanup\"))\nio.write_standard_output_line(\"bye\")\nprocess.exit_with_message(3, \"stopped: {1 + 1}\")\nio.write_standard_output_line(\"never\")\n");

        Assert.Equal(3, run.ExitCode);
        Assert.Equal(RunTermination.ExitScript, run.Outcome.Termination);
        Assert.Null(run.Outcome.Failure);
        Assert.Equal("bye\ncleanup\n", run.StandardOutput);
        Assert.Equal("stopped: 2\n", run.StandardError);
    }

    [Theory]
    [InlineData("process.exit_with_message(0, \"x\")\n")]
    [InlineData("process.exit_with_message(256, \"x\")\n")]
    public void ExitWithMessageOutsideOneTo255FailsWithRuntime0016(string source)
    {
        var run = InterpreterHarness.AssertFailure(source, "SCRIPTS-RUNTIME-0016", "1:1");

        Assert.Contains("1..255 expected", run.Outcome.Failure!.Message, StringComparison.Ordinal);
        Assert.Equal("", run.StandardError);
    }
    [Fact]
    public void AssertTrueReturnsUnitAndTheRunContinues()
    {
        var run = InterpreterHarness.Run("assert(2 + 2 == 4, \"two plus two is four\")\nio.write_standard_output_line(\"after\")\n");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("after\n", run.StandardOutput);
        Assert.Null(run.Outcome.Failure);
    }

    [Fact]
    public void AssertFalseStopsTheRunAtTheCallWithItsMessage()
    {
        var run = InterpreterHarness.AssertFailure("io.write_standard_output_line(\"start\")\nassert(1 == 2, \"one is not two\")\nio.write_standard_output_line(\"never\")\n", "SCRIPTS-RUNTIME-0021", "2:1");

        Assert.Equal("start\n", run.StandardOutput);
        Assert.Equal("assertion failed: one is not two", run.Outcome.Failure!.Message);
    }

    [Fact]
    public void AssertFalseOnALiteralIsNotFolded()
    {
        var run = InterpreterHarness.AssertFailure("assert(false, \"boom\")\n", "SCRIPTS-RUNTIME-0021", "1:1");

        Assert.Equal("assertion failed: boom", run.Outcome.Failure!.Message);
    }

    [Fact]
    public void ExitScriptInsideAFunctionAndACallback()
    {
        var run = InterpreterHarness.Run("function stop() -> integer {\n    process.exit_with_code(7)\n}\nlet values = [1].transform(item => stop())\nio.write_standard_output_line(\"never\")\n");

        Assert.Equal(7, run.ExitCode);
        Assert.Equal(RunTermination.ExitScript, run.Outcome.Termination);
        Assert.Equal(string.Empty, run.StandardOutput);
    }

    [Fact]
    public void ScriptArgumentsAreVisible()
    {
        var run = InterpreterHarness.RunWithArguments("io.write_standard_output_line(process.arguments().join(\"|\"))\n", ["one", "two words", "--flag"]);

        Assert.Equal("one|two words|--flag\n", run.StandardOutput);
    }

    [Fact]
    public void ErrorToTheTopLevelByQuestionMarkIsAnUnhandledRuntimeFailure()
    {
        var run = InterpreterHarness.AssertFailure("io.write_standard_output_line(\"start\")\nlet value = \"x\".parse_integer()?\nio.write_standard_output_line(\"never\")\n", "SCRIPTS-RUNTIME-0017", "2:1");

        Assert.Equal("start\n", run.StandardOutput);
        Assert.Contains("invalid_integer", run.Outcome.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnhandledStringErrorShowsItsText()
    {
        var run = InterpreterHarness.AssertFailure(
            "function fail() -> result<integer, string> {\n    return error(\"boom\")\n}\nlet value = fail()?\n",
            "SCRIPTS-RUNTIME-0017",
            "4:1");

        Assert.Contains("boom", run.Outcome.Failure!.Message, StringComparison.Ordinal);
    }
}
