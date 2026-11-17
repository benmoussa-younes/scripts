namespace Scripts.Corpus.Tests;

/// <summary>
/// xUnit port of the black-box semantics axis: arguments are verbatim, arithmetic
/// truncates, overflow is a runtime error, boolean operators short-circuit, values
/// have value semantics, and the literal/interpolation rules hold.
/// </summary>
[Collection("corpus")]
public sealed class BlackboxSemanticsTests
{
    private sealed record SemanticCase(int ExitCode, string StandardOutput, string Source, string[] Arguments);

    private static readonly IReadOnlyDictionary<string, SemanticCase> CaseTable =
        new Dictionary<string, SemanticCase>(StringComparer.Ordinal)
        {
            ["args_verbatim"] = new(
                0,
                "[a b]\n[-n]\n[*]\n[$HOME]\n[;]\n[]\n",
                """
                for a in process.arguments() {
                    io.write_standard_output_line("[{a}]")
                }
                """,
                ["a b", "-n", "*", "$HOME", ";", ""]),

            ["integer_division_truncates"] = new(
                0,
                "-3\n3\n",
                """
                io.write_standard_output_line("{-7 / 2}")
                io.write_standard_output_line("{7 / 2}")
                """,
                []),

            ["modulo_sign_of_dividend"] = new(
                0,
                "-1\n1\n",
                """
                io.write_standard_output_line("{-7 % 2}")
                io.write_standard_output_line("{7 % 2}")
                """,
                []),

            ["overflow_is_runtime_error"] = new(
                253,
                string.Empty,
                """
                let n = "9223372036854775807".parse_integer()?
                io.write_standard_output_line("{n + 1}")
                """,
                []),

            ["short_circuit_and"] = new(
                0,
                "no\n",
                """
                function boom() -> boolean {
                    io.write_standard_output_line("boom")
                    return true
                }
                if false and boom() {
                    io.write_standard_output_line("yes")
                } else {
                    io.write_standard_output_line("no")
                }
                """,
                []),

            ["short_circuit_or"] = new(
                0,
                "yes\n",
                """
                function boom() -> boolean {
                    io.write_standard_output_line("boom")
                    return false
                }
                if true or boom() {
                    io.write_standard_output_line("yes")
                }
                """,
                []),

            ["value_semantics_list"] = new(
                0,
                "3\n2\n",
                """
                let a = [1, 2]
                let b = a
                let mutable m = a
                m.append(3)
                io.write_standard_output_line("{m.length()}")
                io.write_standard_output_line("{b.length()}")
                """,
                []),

            ["closure_capture_by_reference"] = new(
                0,
                "1\n2\n",
                """
                function run() -> unit {
                    let mutable counter = 0
                    function step() -> unit {
                        counter += 1
                        io.write_standard_output_line("{counter}")
                    }
                    step()
                    step()
                }
                run()
                """,
                []),

            ["propagate_question_mark"] = new(
                0,
                "42\n",
                """
                function parse(s: string) -> result<integer, error> {
                    return s.parse_integer()
                }
                function add_one(s: string) -> result<integer, error> {
                    let n = parse(s)?
                    return ok(n + 1)
                }
                match add_one("41") {
                    ok(v) => io.write_standard_output_line("{v}")
                    error(e) => io.write_standard_output_line("err")
                }
                """,
                []),

            ["error_propagation_returns_error"] = new(
                0,
                "invalid_integer\n",
                """
                function parse(s: string) -> result<integer, error> {
                    return s.parse_integer()
                }
                match parse("nope") {
                    ok(v) => io.write_standard_output_line("ok")
                    error(e) => io.write_standard_output_line(e.kind)
                }
                """,
                []),

            ["match_alternatives"] = new(
                0,
                "pair\n",
                """
                match 4 {
                    1 | 3 | 5 => io.write_standard_output_line("impair")
                    _ => io.write_standard_output_line("pair")
                }
                """,
                []),

            ["match_boolean_exhaustive"] = new(
                0,
                "vrai\n",
                """
                let b = true
                match b {
                    true => io.write_standard_output_line("vrai")
                    false => io.write_standard_output_line("faux")
                }
                """,
                []),

            ["string_interpolation"] = new(
                0,
                "x=5 et pave=100%\n",
                """
                let x = 5
                io.write_standard_output_line("x={x} et pave=100%")
                """,
                []),

            ["raw_string_no_backslash_processing"] = new(
                0,
                "a\\nb\n",
                """
                io.write_standard_output_line(r"a\nb")
                """,
                []),

            ["unicode_length_counts_scalars"] = new(
                0,
                "1\n1\n3\n",
                """
                io.write_standard_output_line("{"😀".length()}")
                io.write_standard_output_line("{"é".length()}")
                io.write_standard_output_line("{"abc".length()}")
                """,
                []),

            ["map_insertion_order"] = new(
                0,
                "b\na\nc\n",
                """
                let m = {"b": 1, "a": 2, "c": 3}
                for k in m.keys() {
                    io.write_standard_output_line(k)
                }
                """,
                []),

            ["list_sorted"] = new(
                0,
                "1\n2\n3\n",
                """
                let xs = [3, 1, 2]
                for x in xs.sorted() {
                    io.write_standard_output_line("{x}")
                }
                """,
                []),

            ["immutable_reassignment_is_static_error"] = new(
                251,
                string.Empty,
                """
                let x = 1
                x = 2
                """,
                []),

            ["shadowing_is_rejected"] = new(
                251,
                string.Empty,
                """
                let x = 1
                let x = 2
                """,
                []),

            ["map_value_semantics"] = new(
                0,
                "1\n2\n",
                """
                let mutable a = {"k": 1}
                let b = a
                a["k"] = 2
                io.write_standard_output_line("{b.get_value("k")?}")
                io.write_standard_output_line("{a.get_value("k")?}")
                """,
                []),

            ["missing_return_is_static_error"] = new(
                251,
                string.Empty,
                """
                function f(x: integer) -> integer {
                    if x > 0 {
                        return 1
                    }
                }
                """,
                []),

            ["float_render_shortest"] = new(
                0,
                "3.0\n0.5\n1.0E+20\n",
                """
                let a = 3.0
                let b = 0.5
                let c = 1.0e20
                io.write_standard_output_line("{a}")
                io.write_standard_output_line("{b}")
                io.write_standard_output_line("{c}")
                """,
                []),

            ["integer_literal_underscores"] = new(
                0,
                "1000000\n",
                """
                let n = 1_000_000
                io.write_standard_output_line("{n}")
                """,
                []),

            ["hex_bin_oct_literals"] = new(
                0,
                "255\n10\n493\n",
                """
                let a = 0xFF
                let b = 0b1010
                let c = 0o755
                io.write_standard_output_line("{a}")
                io.write_standard_output_line("{b}")
                io.write_standard_output_line("{c}")
                """,
                []),
        };

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var name in CaseTable.Keys.Order(StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case(string name)
    {
        var item = CaseTable[name];
        using var work = new BlackboxWorkspace("scripts-blackbox-semantics-");
        var result = BlackboxRunner.RunScript(work.Path, name, item.Source, item.Arguments);
        Assert.False(result.TimedOut, $"{name}: timed out after {result.ElapsedMilliseconds}ms");
        Assert.True(
            result.ExitCode == item.ExitCode,
            $"{name}: exit {result.ExitCode}, expected {item.ExitCode}\n{result.StandardError}");
        Assert.True(
            result.StandardOutput == item.StandardOutput,
            $"{name}: stdout differs\n--- expected ---\n{item.StandardOutput}\n--- actual ---\n{result.StandardOutput}");
        BlackboxRunner.AssertNoTrace(result, name);
    }
}
