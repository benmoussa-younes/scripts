using System.Diagnostics;

namespace Scripts.Runtime.Tests.InterpreterRuns;

/// <summary>
/// In-place string concatenation (`x += y`) must stay linear and must not break value semantics (DESIGN 3.6).
/// The accumulator keeps an append buffer that is only grown in place while it is the unique reference; reading
/// the variable, copying it or storing it in a container retires the buffer.
/// </summary>
public sealed class InterpreterStringConcatTests
{
    [Fact]
    public void StringCopyOnWriteDivergesAfterAppend()
    {
        const string program = """
            let mutable original = "hello"
            let copy = original
            original += " world"
            io.write_standard_output_line("{original}|{copy}")
            """;

        Assert.Equal("hello world|hello\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void MutableCopyDivergesInBothDirections()
    {
        const string program = """
            let mutable first = "a"
            let mutable second = first
            second += "b"
            first += "c"
            io.write_standard_output_line("{first}|{second}")
            """;

        Assert.Equal("ac|ab\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void PlainConcatenationIsIndependentOfItsOperands()
    {
        const string program = """
            let mutable part = "x"
            let mutable combined = part + "y"
            part += "z"
            combined += "!"
            io.write_standard_output_line("{part}|{combined}")
            """;

        Assert.Equal("xz|xy!\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void ConcatenationStoredInAContainerIsFrozen()
    {
        const string program = """
            let mutable accumulator: list<string> = []
            for value in ["a", "b", "c"] {
                accumulator.append(value)
            }
            let joined = accumulator.join("")
            let mutable same = joined
            same += "!"
            io.write_standard_output_line("{joined}|{same}")
            """;

        Assert.Equal("abc|abc!\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void AppendBufferDoesNotLeakIntoAContainer()
    {
        const string program = """
            let mutable text = ""
            for _ in range(0, 4) {
                text += "a"
            }
            let mutable items: list<string> = []
            items.append(text)
            text += "Z"
            io.write_standard_output_line("{items[0]}|{text}")
            """;

        Assert.Equal("aaaa|aaaaZ\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void IndexedCompoundAppendStaysIndependent()
    {
        const string program = """
            let mutable text = "ab"
            let mutable items = [text]
            items[0] += "c"
            text += "d"
            io.write_standard_output_line("{items[0]}|{text}")
            """;

        Assert.Equal("abc|abd\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void MapValueStaysIndependentOfItsSource()
    {
        const string program = """
            let mutable text = "k"
            text += "1"
            let mutable table: map<string, string> = {"key": text}
            text += "2"
            let stored = table["key"]
            io.write_standard_output_line("{stored}|{text}")
            """;

        Assert.Equal("k1|k12\n", InterpreterHarness.Output(program));
    }

    [Theory]
    [InlineData("\"ab\" + \"cd\"", "abcd")]
    [InlineData("\"\" + \"x\"", "x")]
    [InlineData("\"x\" + \"\"", "x")]
    public void AdditionOfStringsConcatenates(string expression, string expected) =>
        Assert.Equal(expected + "\n", InterpreterHarness.Output($"let result = {expression}\nio.write_standard_output_line(\"{{result}}\")"));

    [Fact]
    public void LoopConcatenationProducesTheCorrectResult()
    {
        const string program = """
            let mutable accumulator = ""
            for index in range(0, 1000) {
                accumulator += "0123456789"
            }
            io.write_standard_output_line("len={accumulator.length()}")
            """;

        Assert.Equal("len=10000\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void ConcatenationResultEqualityAndHashingAreStructural()
    {
        const string program = """
            let mutable built = ""
            for index in range(0, 5) {
                built += "ab"
            }
            let direct = "ababababab"
            io.write_standard_output_line("{built == direct}")
            let mutable table: map<string, integer> = {}
            table["ababababab"] = 1
            table[built] += 41
            io.write_standard_output_line("{table[direct]}")
            """;

        Assert.Equal("true\n42\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void InterpolationOfAnAccumulatorSeesItsFullText()
    {
        const string program = """
            let mutable accumulator = ""
            for index in range(0, 3) {
                accumulator += "{index}"
            }
            io.write_standard_output_line("value={accumulator} len={accumulator.length()}")
            """;

        Assert.Equal("value=012 len=3\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void NestedSnapshotInsideTheLoopStaysStable()
    {
        const string program = """
            let mutable text = ""
            for _ in range(0, 3) {
                text += "a"
                let snapshot = text
                text += "b"
                io.write_standard_output_line("{snapshot}")
            }
            """;

        Assert.Equal(InterpreterHarness.Lines("a", "aba", "ababa"), InterpreterHarness.Output(program));
    }

    [Fact]
    public void AppendingToItselfUsesTheOperandEvaluatedBeforeTheAppend()
    {
        const string program = """
            let mutable text = "w"
            text += text
            io.write_standard_output_line("{text}")
            let mutable other = "ab"
            other += other + other
            io.write_standard_output_line("{other}")
            """;

        Assert.Equal(InterpreterHarness.Lines("ww", "ababab"), InterpreterHarness.Output(program));
    }

    [Fact]
    public void StringMethodsStillWorkAfterInPlaceAppend()
    {
        const string program = """
            let mutable text = "  Hello"
            text += " World  "
            io.write_standard_output_line("{text.trim_whitespace()}")
            io.write_standard_output_line("{text.uppercased()}")
            io.write_standard_output_line("{text.contains("World")}")
            io.write_standard_output_line("{text.length()}")
            """;

        Assert.Equal(InterpreterHarness.Lines("Hello World", "  HELLO WORLD  ", "true", "15"), InterpreterHarness.Output(program));
    }

    [Fact]
    public void ResultPayloadAndJsonSerialisationSeeTheMaterialisedText()
    {
        const string program = """
            let mutable text = "a"
            text += "b"
            let wrapped = ok(text)
            match wrapped {
                ok(value) => io.write_standard_output_line("{value}")
                error(_) => io.write_standard_output_line("error")
            }
            return text
            """;

        Assert.Equal("ab\n", InterpreterHarness.Output(program));
    }
}

/// <summary>Wall-clock proof that repeated string concatenation is linear, not quadratic.</summary>
[Collection(InterpreterPerformanceCollection.Name)]
public sealed class InterpreterStringConcatPerformanceTests(ITestOutputHelper output)
{
    private (string Output, TimeSpan Elapsed) Measure(string program)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = InterpreterHarness.Output(program);
        stopwatch.Stop();
        output.WriteLine($"elapsed {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
        return (result, stopwatch.Elapsed);
    }

    [Fact]
    public void SixtyThousandAppendsStayWellUnderHalfASecond()
    {
        const string program = """
            let mutable accumulator = ""
            for _ in range(0, 60000) {
                accumulator += "0123456789"
            }
            io.write_standard_output_line("len={accumulator.length()}")
            """;

        var (result, elapsed) = Measure(program);

        Assert.Equal("len=600000\n", result);
        Assert.True(elapsed < TimeSpan.FromMilliseconds(500), $"took {elapsed}");
    }

    [Fact]
    public void ThreeHundredThousandAppendsStayUnderTwoSeconds()
    {
        const string program = """
            let mutable accumulator = ""
            for _ in range(0, 300000) {
                accumulator += "0123456789"
            }
            io.write_standard_output_line("len={accumulator.length()}")
            """;

        var (result, elapsed) = Measure(program);

        Assert.Equal("len=3000000\n", result);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"took {elapsed}");
    }

    [Fact]
    public void AppendDoesNotMutateAValueBoundByAMatchPattern()
    {
        // A `match` binding aliases the scrutinee; `+=` on the original must not be observed through the binding.
        const string program = """
            let mutable source = "AB"
            match source {
                captured => {
                    source += "C"
                    io.write_standard_output_line("captured=[{captured}] source=[{source}]")
                }
            }
            """;

        Assert.Equal("captured=[AB] source=[ABC]\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void AppendDoesNotCorruptAStringUsedAsAMapKey()
    {
        const string program = """
            let mutable key = "AB"
            let table = {key: 1}
            key += "C"
            io.write_standard_output_line("{table.get_value("AB").value_or(-1)}")
            io.write_standard_output_line("{table.get_value("ABC").value_or(-1)}")
            io.write_standard_output_line("{table.keys().join(",")}")
            """;

        Assert.Equal("1\n-1\nAB\n", InterpreterHarness.Output(program));
    }

    [Fact]
    public void CopyingAMapCopiesItsKeys()
    {
        // Two maps copied from the same source must stay equal even after the key variable they were built from is
        // mutated; and each map keeps its own snapshot of the key.
        const string program = """
            let mutable key = "AB"
            let first = {key: 1}
            let second = first
            key += "C"
            io.write_standard_output_line("{first == second}")
            io.write_standard_output_line("{first.keys().join(",")}")
            io.write_standard_output_line("{second.keys().join(",")}")
            """;

        Assert.Equal("true\nAB\nAB\n", InterpreterHarness.Output(program));
    }
}
