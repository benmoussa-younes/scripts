using System.Diagnostics;

namespace Scripts.Runtime.Tests.InterpreterRuns;

/// <summary>Collection that never runs in parallel with other test collections, so wall-clock limits are meaningful.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InterpreterPerformanceCollection
{
    public const string Name = "Interpreter performance (alone)";
}

/// <summary>Loops that must finish in seconds and runs that must be reproducible (DESIGN 0.6).</summary>
[Collection(InterpreterPerformanceCollection.Name)]
public sealed class InterpreterPerformanceTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private (string Output, TimeSpan Elapsed) Measure(string program)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = InterpreterHarness.Output(program);
        stopwatch.Stop();
        output.WriteLine($"elapsed {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
        return (result, stopwatch.Elapsed);
    }

    [Fact]
    public void WhileLoopOfOneMillionIterations()
    {
        var (output, elapsed) = Measure("let mutable counter = 0\nlet mutable total = 0\nwhile counter < 1000000 {\n    counter += 1\n    total += counter % 7\n}\nio.write_standard_output_line(\"{counter} {total}\")\n");

        Assert.Equal("1000000 2999998\n", output);
        Assert.True(elapsed < Limit, $"took {elapsed}");
    }

    [Fact]
    public void ForLoopOverARangeOfOneMillionElements()
    {
        var (output, elapsed) = Measure("let mutable total = 0\nfor value in range(0, 1000000) {\n    if value % 3 == 0 {\n        total += value\n    }\n}\nio.write_standard_output_line(\"{total}\")\n");

        Assert.Equal("166666833333\n", output);
        Assert.True(elapsed < Limit, $"took {elapsed}");
    }

    [Fact]
    public void FunctionCallsInALoop()
    {
        const string program = """
            function square(value: integer) -> integer {
                return value * value
            }
            let mutable total = 0
            for value in range(0, 300000) {
                total += square(value) % 10
            }
            io.write_standard_output_line("{total}")
            """;

        var (output, elapsed) = Measure(program);

        Assert.Equal("1350000\n", output);
        Assert.True(elapsed < Limit, $"took {elapsed}");
    }

    [Fact]
    public void ImmutableLargeListsArePassedWithoutCopying()
    {
        const string program = """
            function first_of(items: list<integer>) -> integer {
                return items[0]
            }
            let big = range(0, 100000)
            let mutable total = 0
            for _ in range(0, 100000) {
                total += first_of(big)
            }
            io.write_standard_output_line("{total}")
            """;

        var (output, elapsed) = Measure(program);

        Assert.Equal("0\n", output);
        Assert.True(elapsed < Limit, $"took {elapsed}");
    }

    [Fact]
    public void ManyVariablesInTheScriptScope()
    {
        var declarations = string.Join("\n", Enumerable.Range(0, 300).Select(number => $"let mutable variable_{number} = {number}"));
        var program = declarations
            + "\nlet mutable index = 0\nwhile index < 100000 {\n    variable_299 += variable_0 + variable_150\n    index += 1\n}\nio.write_standard_output_line(\"{variable_299}\")\n";

        var (output, elapsed) = Measure(program);

        Assert.Equal("15000299\n", output);
        Assert.True(elapsed < Limit, $"took {elapsed}");
    }

    [Fact]
    public void AppendingAndIndexingLargeListsStaysLinear()
    {
        const string program = """
            let mutable numbers: list<integer> = []
            for value in range(0, 200000) {
                numbers.append(value)
            }
            let mutable total = 0
            let mutable position = 0
            while position < numbers.length() {
                total += numbers[position]
                position += 1
            }
            numbers[100] = 5
            io.write_standard_output_line("{total} {numbers[100]}")
            """;

        var (output, elapsed) = Measure(program);

        Assert.Equal("19999900000 5\n", output);
        Assert.True(elapsed < Limit, $"took {elapsed}");
    }

    [Fact]
    public void RunsAreDeterministic()
    {
        const string program = """
            let mutable counts: map<string, integer> = {}
            for word in ["pear", "fig", "apple", "fig", "pear", "fig"] {
                if counts.contains_key(word) {
                    counts[word] += 1
                } else {
                    counts[word] = 1
                }
            }
            for word in counts.keys() {
                io.write_standard_output_line("{word}={counts[word]}")
            }
            io.write_standard_output_line(counts.keys().sorted().join(","))
            io.write_standard_output_line(counts.values().transform(count => "{count}").join("+"))
            io.write_standard_output_line("{counts == {"fig": 3, "pear": 2, "apple": 1}}")
            """;

        var first = InterpreterHarness.Run(program);
        var second = InterpreterHarness.Run(program);

        Assert.Equal(first.StandardOutput, second.StandardOutput);
        Assert.Equal(first.ExitCode, second.ExitCode);
        Assert.Equal(
            InterpreterHarness.Lines("pear=2", "fig=3", "apple=1", "apple,fig,pear", "2+3+1", "true"),
            first.StandardOutput);
    }
}
