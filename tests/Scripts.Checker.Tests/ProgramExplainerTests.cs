using System.Text.Json;

namespace Scripts.Checker.Tests;

/// <summary>`scripts explain` (DESIGN 9): the human sentence of an assignment keeps its operator, and the machine form of a step carries its source position.</summary>
public sealed class ProgramExplainerTests
{
    [Theory]
    [InlineData("let mutable total = 0\ntotal += 3", "adds 3 to `total`")]
    [InlineData("let mutable total = 3\ntotal -= 1", "subtracts 1 from `total`")]
    [InlineData("let mutable total = 3\ntotal *= 2", "multiplies `total` by 2")]
    [InlineData("let mutable total = 6\ntotal /= 2", "divides `total` by 2")]
    [InlineData("let mutable total = 7\ntotal %= 2", "sets `total` to `total` modulo 2")]
    [InlineData("let mutable total = 0\ntotal = 3", "modifies `total` with 3")]
    public void An_assignment_sentence_names_its_operator(string source, string expected)
    {
        Assert.Contains(expected, Explain(source), StringComparison.Ordinal);
    }

    [Fact]
    public void Json_steps_carry_the_position_of_their_statement()
    {
        var program = Checking.Load("let n = 3\nlet mutable total = 0\ntotal += n\n");
        var check = ProgramChecker.Check(program);
        Assert.Empty(check.Errors);

        using var document = JsonDocument.Parse(ProgramExplainer.ExplainJson(program, check.Warnings));
        Assert.Equal(1, document.RootElement.GetProperty("schema_version").GetInt32());
        var steps = document.RootElement.GetProperty("steps");
        Assert.Equal(3, steps.GetArrayLength());

        var assignment = steps[2];
        Assert.Equal("assignment", assignment.GetProperty("kind").GetString());
        Assert.Equal("adds `n` to `total`", assignment.GetProperty("text").GetString());
        Assert.Equal(3, assignment.GetProperty("line").GetInt32());
        Assert.Equal(1, assignment.GetProperty("column").GetInt32());
    }

    [Fact]
    public void Json_steps_carry_the_position_of_a_nested_statement()
    {
        var program = Checking.Load("while false {\n    io.write_standard_output_line(\"x\")\n}\n");
        var check = ProgramChecker.Check(program);
        Assert.Empty(check.Errors);

        using var document = JsonDocument.Parse(ProgramExplainer.ExplainJson(program, check.Warnings));
        var steps = document.RootElement.GetProperty("steps");
        Assert.Equal(2, steps.GetArrayLength());
        Assert.Equal(1, steps[0].GetProperty("line").GetInt32());
        Assert.Equal(1, steps[0].GetProperty("column").GetInt32());
        Assert.Equal(2, steps[1].GetProperty("line").GetInt32());
        Assert.Equal(5, steps[1].GetProperty("column").GetInt32());
    }

    [Fact]
    public void Else_branches_are_anchored_on_the_else_keyword()
    {
        var program = Checking.Load("""
            let n = 1
            if n > 1 {
                io.write_standard_output_line("a")
            } else if n > 0 {
                io.write_standard_output_line("b")
            } else {
                io.write_standard_output_line("c")
            }
            """);
        var check = ProgramChecker.Check(program);
        Assert.Empty(check.Errors);

        using var document = JsonDocument.Parse(ProgramExplainer.ExplainJson(program, check.Warnings));
        var steps = document.RootElement.GetProperty("steps").EnumerateArray().ToList();
        var elseIf = Assert.Single(steps, step =>
            (step.GetProperty("text").GetString() ?? string.Empty).StartsWith("else if", StringComparison.Ordinal));
        // "} else if" and "} else {": the `else` keyword is column 3, not the `if`/`{` that follows it.
        Assert.Equal(4, elseIf.GetProperty("line").GetInt32());
        Assert.Equal(3, elseIf.GetProperty("column").GetInt32());
        var elseStep = Assert.Single(steps, step => step.GetProperty("text").GetString() == "else:");
        Assert.Equal(6, elseStep.GetProperty("line").GetInt32());
        Assert.Equal(3, elseStep.GetProperty("column").GetInt32());
    }

    [Fact]
    public void A_named_call_keeps_its_names_in_the_explanation()
    {
        var explanation = Explain("""
            function first_minus(second: integer, first: integer) -> integer {
                return first - second
            }
            let value = first_minus(second = 2, first = 10)
            io.write_standard_output_line("{value}")
            """);

        Assert.Contains("second = 2, first = 10", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_named_stdlib_call_renders_its_arguments_by_parameter_name()
    {
        var explanation = Explain("""
            let text = "abcdef"
            let part = text.substring(count = 2, start_index = 1)?
            io.write_standard_output_line(part)
            """);

        Assert.Contains("starting at 1 over 2 characters", explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("count = 2", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_warnings_carry_the_full_diagnostic_object()
    {
        var program = Checking.Load("let text = \"cost $name\"\nio.write_standard_output_line(text)\n");
        var check = ProgramChecker.Check(program);
        Assert.NotEmpty(check.Warnings);

        using var document = JsonDocument.Parse(ProgramExplainer.ExplainJson(program, check.Warnings));
        var warning = document.RootElement.GetProperty("warnings")[0];
        Assert.Equal("SCRIPTS-LINT-0003", warning.GetProperty("code").GetString());
        foreach (var key in new[] { "file", "line", "column", "source_line", "cause", "suggestion" })
        {
            Assert.True(warning.TryGetProperty(key, out _), $"missing warning key {key}");
        }
    }

    private static string Explain(string source)
    {
        var program = Checking.Load(source);
        var check = ProgramChecker.Check(program);
        Assert.True(check.Errors.Count == 0, Checking.Describe(check));
        return ProgramExplainer.Explain(program, check.Warnings);
    }
}
