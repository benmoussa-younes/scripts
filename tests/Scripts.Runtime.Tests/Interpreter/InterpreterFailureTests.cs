namespace Scripts.Runtime.Tests.InterpreterRuns;

/// <summary>Fatal runtime failures: code, position (start of the executing statement), exit code and message placeholders.</summary>
public sealed class InterpreterFailureTests
{
    private const string MinimumInteger = "let low = -9223372036854775807 - 1\n";

    public static TheoryData<string, string, string> Failures => new()
    {
        { "let big = 9223372036854775807\nlet x = big + \"abc\".length()\n", "SCRIPTS-RUNTIME-0001", "2:1" },
        { MinimumInteger + "let x = low - \"a\".length()\n", "SCRIPTS-RUNTIME-0001", "2:1" },
        { "let big = 4611686018427387904\nlet x = big * (\"ab\".length())\n", "SCRIPTS-RUNTIME-0001", "2:1" },
        { MinimumInteger + "let x = -(low + \"\".length())\n", "SCRIPTS-RUNTIME-0001", "2:1" },
        { MinimumInteger + "let x = low / (0 - \"a\".length())\n", "SCRIPTS-RUNTIME-0001", "2:1" },
        { MinimumInteger + "let x = low % (0 - \"a\".length())\n", "SCRIPTS-RUNTIME-0001", "2:1" },
        { "let mutable total = 9223372036854775807\ntotal += \"a\".length()\n", "SCRIPTS-RUNTIME-0001", "2:1" },
        { "let zero = \"\".length()\nlet x = 1 / zero\n", "SCRIPTS-RUNTIME-0002", "2:1" },
        { "let zero = \"\".length()\nlet x = 1 % zero\n", "SCRIPTS-RUNTIME-0002", "2:1" },
        { "let zero = \"\".length()\nio.write_standard_output_line(\"{5 / zero}\")\n", "SCRIPTS-RUNTIME-0002", "2:1" },
        { "let huge = 1e300 * to_float(1)\nlet x = huge * huge\n", "SCRIPTS-RUNTIME-0003", "2:1" },
        { "let huge = 1e300 * to_float(1)\nlet x = huge * 1e10\n", "SCRIPTS-RUNTIME-0003", "2:1" },
        { "let zero = to_float(\"\".length())\nlet x = 1.0 / zero\n", "SCRIPTS-RUNTIME-0003", "2:1" },
        { "let items = [1, 2, 3]\nlet x = items[items.length()]\n", "SCRIPTS-RUNTIME-0004", "2:1" },
        { "let items = [1, 2, 3]\nlet x = items[0 - 1]\n", "SCRIPTS-RUNTIME-0004", "2:1" },
        { "let rows = [[1]]\nlet x = rows[0][3]\n", "SCRIPTS-RUNTIME-0004", "2:1" },
        { "let mutable items = [1, 2, 3]\nitems[3] = 4\n", "SCRIPTS-RUNTIME-0005", "2:1" },
        { "let mutable items = [1, 2, 3]\nitems[0 - 1] = 4\n", "SCRIPTS-RUNTIME-0005", "2:1" },
        { "let mutable grid = [[1]]\ngrid[0][5] = 4\n", "SCRIPTS-RUNTIME-0005", "2:1" },
        { "let mutable items = [1]\nitems[2] += 4\n", "SCRIPTS-RUNTIME-0004", "2:1" },
        { "let ages = {\"alice\": 30}\nlet x = ages[\"bob\".trim_whitespace()]\n", "SCRIPTS-RUNTIME-0006", "2:1" },
        { "let mutable ages = {\"alice\": 1}\nages[\"bob\"] += 1\n", "SCRIPTS-RUNTIME-0006", "2:1" },
        { "let nested = {\"a\": {\"b\": 1}}\nlet x = nested[\"a\"][\"c\"]\n", "SCRIPTS-RUNTIME-0006", "2:1" },
        { "let n = \"x\".parse_integer().value_or_fail()\n", "SCRIPTS-RUNTIME-0007", "1:1" },
        { "let parts = \"abc\".split(\"\")\n", "SCRIPTS-RUNTIME-0008", "1:1" },
        { "let r = \"abc\".replace(\"\", \"x\")\n", "SCRIPTS-RUNTIME-0009", "1:1" },
        { "let items = range(0, 10000001)\n", "SCRIPTS-RUNTIME-0010", "1:1" },
        { "process.exit_with_code(256)\n", "SCRIPTS-RUNTIME-0016", "1:1" },
        { "process.exit_with_code(300)\n", "SCRIPTS-RUNTIME-0016", "1:1" },
        { "process.exit_with_code(0 - 1)\n", "SCRIPTS-RUNTIME-0016", "1:1" },
        { "io.write_standard_output_line(\"a\")\nlet n = \"x\".parse_float()?\n", "SCRIPTS-RUNTIME-0017", "2:1" },
        { "io.write_standard_output_line(\"a\")\nassert(false, \"boom\")\n", "SCRIPTS-RUNTIME-0021", "2:1" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void FailureCodeAndPosition(string program, string expectedCode, string expectedPosition) =>
        InterpreterHarness.AssertFailure(program, expectedCode, expectedPosition);

    [Fact]
    public void MessagesCarryTheirPlaceholders()
    {
        var overflow = InterpreterHarness.AssertFailure("let big = 9223372036854775807\nlet x = big + \"abc\".length()\n", "SCRIPTS-RUNTIME-0001", "2:1");
        Assert.Contains("9223372036854775807 + 3", overflow.Outcome.Failure!.Message, StringComparison.Ordinal);

        var division = InterpreterHarness.AssertFailure("let zero = \"\".length()\nlet x = 7 / zero\n", "SCRIPTS-RUNTIME-0002", "2:1");
        Assert.Contains("7 / 0", division.Outcome.Failure!.Message, StringComparison.Ordinal);

        var index = InterpreterHarness.AssertFailure("let items = [1, 2, 3]\nlet x = items[7]\n", "SCRIPTS-RUNTIME-0004", "2:1");
        Assert.Contains("7", index.Outcome.Failure!.Message, StringComparison.Ordinal);
        Assert.Contains("3", index.Outcome.Failure.Message, StringComparison.Ordinal);

        var key = InterpreterHarness.AssertFailure("let ages = {\"alice\": 30}\nlet x = ages[\"bob\".trim_whitespace()]\n", "SCRIPTS-RUNTIME-0006", "2:1");
        Assert.Contains("bob", key.Outcome.Failure!.Message, StringComparison.Ordinal);

        var unwrap = InterpreterHarness.AssertFailure("let n = \"x\".parse_integer().value_or_fail()\n", "SCRIPTS-RUNTIME-0007", "1:1");
        Assert.Contains("invalid_integer", unwrap.Outcome.Failure!.Message, StringComparison.Ordinal);

        var exit = InterpreterHarness.AssertFailure("process.exit_with_code(256)\n", "SCRIPTS-RUNTIME-0016", "1:1");
        Assert.Contains("256", exit.Outcome.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputBeforeTheFailureIsKept()
    {
        var run = InterpreterHarness.AssertFailure("io.write_standard_output_line(\"before\")\nlet zero = \"\".length()\nlet x = 1 / zero\nio.write_standard_output_line(\"after\")\n", "SCRIPTS-RUNTIME-0002", "3:1");

        Assert.Equal("before\n", run.StandardOutput);
        Assert.Null(run.Outcome.Result);
    }

    [Fact]
    public void PositionIsTheStartOfAMultiLineStatementAndSpansItsEnd()
    {
        var run = InterpreterHarness.AssertFailure("let zero = \"\".length()\nlet value = [\n    1,\n    1 / zero\n]\n", "SCRIPTS-RUNTIME-0002", "2:1");

        var failure = run.Outcome.Failure!;
        Assert.Equal((2, 1), failure.Start);
        Assert.Equal((5, 2), failure.End);
        Assert.Equal(InterpreterHarness.EntryPath, failure.FilePath);
    }

    [Fact]
    public void PositionIsTheInnermostStatementInsideFunctionsAndBlocks()
    {
        const string program = """
            function inner(divisor: integer) -> integer {
                let ratio = 10 / divisor
                return ratio
            }
            function outer(divisor: integer) -> integer {
                if divisor == 0 {
                    for step in [1] {
                        return inner(divisor)
                    }
                }
                return 0
            }
            let zero = 0
            io.write_standard_output_line("start")
            let result = outer(zero)
            """;

        var run = InterpreterHarness.AssertFailure(program, "SCRIPTS-RUNTIME-0002", "2:5");

        Assert.Equal("start\n", run.StandardOutput);
    }

    [Fact]
    public void PositionAfterACalleeReturnsIsTheCallersStatement()
    {
        const string program = """
            function helper() -> integer {
                let ready = 1
                return ready
            }
            let zero = "".length()
            let value = helper() / zero
            """;

        InterpreterHarness.AssertFailure(program, "SCRIPTS-RUNTIME-0002", "6:1");
    }

    [Theory]
    [InlineData("let zero = 0\nif 1 / zero == 0 {\n    io.write_standard_output_line(\"x\")\n}\n", "2:1", 2, 17)]
    [InlineData("let zero = 0\nif false {\n    io.write_standard_output_line(\"x\")\n} else if 1 / zero == 0 {\n    io.write_standard_output_line(\"y\")\n}\n", "4:8", 4, 24)]
    [InlineData("let zero = 0\nwhile 1 / zero == 0 {\n    io.write_standard_output_line(\"x\")\n}\n", "2:1", 2, 20)]
    [InlineData("let zero = 0\nfor item in range(0, 1 / zero) {\n    io.write_standard_output_line(\"x\")\n}\n", "2:1", 2, 31)]
    [InlineData("let zero = 0\nmatch 1 / zero {\n    _ => io.write_standard_output_line(\"x\")\n}\n", "2:1", 2, 15)]
    public void HeaderFailuresSpanTheHeaderOnly(string program, string expectedPosition, int expectedEndLine, int expectedEndColumn)
    {
        var run = InterpreterHarness.AssertFailure(program, "SCRIPTS-RUNTIME-0002", expectedPosition);

        Assert.Equal((expectedEndLine, expectedEndColumn), run.Outcome.Failure!.End);
    }

    [Fact]
    public void WhileHeaderIsTheFailingPositionOnLaterTurns()
    {
        const string program = """
            let mutable divisor = 1
            while 10 / divisor > 0 {
                divisor -= 1
            }
            """;

        InterpreterHarness.AssertFailure(program, "SCRIPTS-RUNTIME-0002", "2:1");
    }

    [Fact]
    public void FailureInsideACallbackPointsAtTheStatementOfTheStdlibCall()
    {
        const string program = """
            let zero = "".length()
            let result = [1, 2].transform(item => item / zero)
            """;

        InterpreterHarness.AssertFailure(program, "SCRIPTS-RUNTIME-0002", "2:1");
    }

    [Fact]
    public void FailureInsideANamedCallbackPointsAtItsOwnStatement()
    {
        const string program = """
            function risky(item: integer) -> integer {
                let zero = 0
                return item / zero
            }
            let result = [1, 2].transform(risky)
            """;

        InterpreterHarness.AssertFailure(program, "SCRIPTS-RUNTIME-0002", "3:5");
    }

    [Fact]
    public void FailureInAModuleFunctionPointsAtTheModuleFile()
    {
        var run = InterpreterHarness.Run(
            "import \"lib.scs\" as lib\nio.write_standard_output_line(\"go\")\nlet value = lib.divide(1, 0)\n",
            ("/lib.scs", "public function divide(left: integer, right: integer) -> integer {\n    return left / right\n}\n"));

        Assert.Equal("SCRIPTS-RUNTIME-0002", run.FailureCode);
        Assert.Equal("/lib.scs", run.Outcome.Failure!.FilePath);
        Assert.Equal("2:5", run.FailurePosition);
        Assert.Equal(253, run.ExitCode);
    }

    [Fact]
    public void ConstantEvaluationFailureReplacesTheUnderlyingCodeBeforeAnyOutput()
    {
        var run = InterpreterHarness.AssertFailure("io.write_standard_output_line(\"never displayed\")\nlet x = 1 / 0\n", "SCRIPTS-RUNTIME-0020", "2:1");

        Assert.Equal(string.Empty, run.StandardOutput);
        Assert.Contains("`x`", run.Outcome.Failure!.Message, StringComparison.Ordinal);
        Assert.Contains("division by zero", run.Outcome.Failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("let big = 9223372036854775807 + 1\n", "integer overflow")]
    [InlineData("let remainder = 5 % 0\n", "division by zero")]
    [InlineData("let huge = 1e300 * 1e300\n", "non-finite float result (overflow)")]
    [InlineData("let ratio = 1.0 / 0.0\n", "non-finite float result (division by zero)")]
    [InlineData("let nested = [1, 2 - 1 / 0]\n", "division by zero")]
    public void ConstantFailuresNameTheirCause(string program, string expectedCause)
    {
        var run = InterpreterHarness.AssertFailure(program, "SCRIPTS-RUNTIME-0020", "1:1");

        Assert.Contains(expectedCause, run.Outcome.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstantsAreEvaluatedModulesFirstInLoadOrderAndTheFirstFailureWins()
    {
        var run = InterpreterHarness.Run(
            "import \"first.scs\" as first\nimport \"second.scs\" as second\nlet entry_failure = 1 / 0\nio.write_standard_output_line(\"never\")\n",
            ("/first.scs", "public let FIRST_OK = 1\npublic let FIRST_BAD = 2 / 0\n"),
            ("/second.scs", "public let SECOND_BAD = 3 % 0\n"));

        Assert.Equal("SCRIPTS-RUNTIME-0020", run.FailureCode);
        Assert.Equal("/first.scs", run.Outcome.Failure!.FilePath);
        Assert.Equal("2:1", run.FailurePosition);
        Assert.Contains("FIRST_BAD", run.Outcome.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, run.StandardOutput);
    }

    [Fact]
    public void ModuleConstantsAreAvailableToTheEntryConstants()
    {
        var run = InterpreterHarness.Run(
            "import \"lib.scs\" as lib\nlet doubled = lib.BASE * 2\nio.write_standard_output_line(\"{doubled}\")\n",
            ("/lib.scs", "public let BASE = 21\n"));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("42\n", run.StandardOutput);
    }

    [Fact]
    public void ScriptVariablesFailWithTheUnderlyingCodeAtTheirOwnStatement()
    {
        var run = InterpreterHarness.AssertFailure("io.write_standard_output_line(\"before\")\nlet mutable x = 1 / 0\n", "SCRIPTS-RUNTIME-0002", "2:1");

        Assert.Equal("before\n", run.StandardOutput);
    }

    [Fact]
    public void DepthOfExactlyTenThousandIsAllowed()
    {
        const string program = """
            function count_down(remaining: integer) -> integer {
                if remaining == 0 {
                    return 0
                }
                return 1 + count_down(remaining - 1)
            }
            io.write_standard_output_line("{count_down(9999)}")
            """;

        var run = InterpreterHarness.RunOnLargeStack(program);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("9999\n", run.StandardOutput);
    }

    [Fact]
    public void DepthOfTenThousandAndOneFailsAtTheCallingStatement()
    {
        const string program = """
            function count_down(remaining: integer) -> integer {
                if remaining == 0 {
                    return 0
                }
                return 1 + count_down(remaining - 1)
            }
            io.write_standard_output_line("{count_down(10000)}")
            """;

        var run = InterpreterHarness.RunOnLargeStack(program);

        Assert.Equal("SCRIPTS-RUNTIME-0011", run.FailureCode);
        Assert.Equal("5:5", run.FailurePosition);
        Assert.Equal(253, run.ExitCode);
        Assert.Contains("count_down", run.Outcome.Failure!.Message, StringComparison.Ordinal);
        Assert.Contains("10000", run.Outcome.Failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4999, true)]
    [InlineData(5000, false)]
    public void CallbacksFromTheStandardLibraryCountTowardTheDepth(int levels, bool succeeds)
    {
        var program = """
            function recurse(level: integer) -> integer {
                if level == 0 {
                    return 0
                }
                return [level].transform(item => recurse(item - 1))[0]
            }
            """ + $"\nio.write_standard_output_line(\"{{recurse({levels})}}\")\n";

        var run = InterpreterHarness.RunOnLargeStack(program);

        if (succeeds)
        {
            Assert.Equal(0, run.ExitCode);
            Assert.Equal("0\n", run.StandardOutput);
        }
        else
        {
            Assert.Equal("SCRIPTS-RUNTIME-0011", run.FailureCode);
            Assert.Equal("5:5", run.FailurePosition);
        }
    }

    [Fact]
    public void MutualRecursionCountsEveryCall()
    {
        const string program = """
            function ping(remaining: integer) -> integer {
                if remaining == 0 {
                    return 0
                }
                return pong(remaining - 1)
            }
            function pong(remaining: integer) -> integer {
                return ping(remaining)
            }
            io.write_standard_output_line("{ping(5000)}")
            """;

        var run = InterpreterHarness.RunOnLargeStack(program);

        Assert.Equal("SCRIPTS-RUNTIME-0011", run.FailureCode);
    }

    [Fact]
    public void DepthIsRestoredAfterEachCall()
    {
        const string program = """
            function leaf() -> integer {
                return 1
            }
            let mutable total = 0
            for _ in range(0, 25000) {
                total += leaf()
            }
            io.write_standard_output_line("{total}")
            """;

        Assert.Equal("25000\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void StringsAboveTheLimitFailWithCodeThirteen()
    {
        var run = InterpreterHarness.AssertFailure(
            "let mutable text = \"ab\"\nfor _ in range(0, 26) {\n    text = text + text\n}\nlet joined = [text, text, text].join(\"\")\n",
            "SCRIPTS-RUNTIME-0013",
            "5:1");

        Assert.Contains("402653184", run.Outcome.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalErrorsAreInvalidOperationExceptionsNeverHangs()
    {
        var program = InterpreterHarness.Load("let value = missing_name + 1\n");
        using var context = RuntimeContext.CreateForCapture([], out _, listenForSignals: false);

        var exception = Assert.Throws<InvalidOperationException>(() => Scripts.Runtime.Interpreter.ProgramInterpreter.Run(program, context));
        Assert.Contains("missing_name", exception.Message, StringComparison.Ordinal);
    }
}
