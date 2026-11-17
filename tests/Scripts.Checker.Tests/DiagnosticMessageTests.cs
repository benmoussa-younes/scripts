using Scripts.Core;

namespace Scripts.Checker.Tests;

/// <summary>
/// Wording regression tests for the diagnostics improved in group 1: SCRIPTS-TYPE-0029 (type-aware
/// suggestion) and SCRIPTS-TYPE-0034 (the `unit` message names the `?`/`result<unit, E>` cause).
/// </summary>
public sealed class DiagnosticMessageTests
{
    private static Diagnostic First(string mainText)
    {
        var result = Checking.Check(mainText);
        return result.Errors.Count > 0 ? result.Errors[0] : throw new Xunit.Sdk.XunitException("no diagnostic for: " + mainText);
    }

    [Fact]
    public void Undetermined_list_literal_suggests_a_list_annotation()
    {
        var diagnostic = First("let xs = []");
        Assert.Equal(ErrorCodes.Type0029, diagnostic.Code);
        Assert.Contains("undetermined element type of the list", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("let xs: list<integer> = …", diagnostic.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Undetermined_map_literal_suggests_a_map_annotation()
    {
        var diagnostic = First("let m = {}");
        Assert.Equal(ErrorCodes.Type0029, diagnostic.Code);
        Assert.Contains("undetermined key and value types of the map", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("let xs: map<string, integer> = …", diagnostic.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Undetermined_ok_constructor_suggests_a_result_annotation()
    {
        var diagnostic = First("let r = ok()");
        Assert.Equal(ErrorCodes.Type0029, diagnostic.Code);
        Assert.Contains("undetermined error type of the `result`", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("result<unit, error>", diagnostic.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Undetermined_error_constructor_suggests_a_result_annotation()
    {
        var diagnostic = First("let r = error(1)");
        Assert.Equal(ErrorCodes.Type0029, diagnostic.Code);
        Assert.Contains("undetermined success type of the `result`", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("result<integer, integer>", diagnostic.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Binding_a_propagated_unit_names_the_question_mark_cause()
    {
        var diagnostic = First("let r: result<unit, error> = ok()\nlet _ = r?");
        Assert.Equal(ErrorCodes.Type0034, diagnostic.Code);
        Assert.Contains("the operand of `?` unwraps to `unit`", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("result<unit, E>", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("f()?", diagnostic.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Binding_a_plain_unit_leaves_no_context_placeholder()
    {
        var diagnostic = First("let x = io.write_standard_output_line(\"a\")");
        Assert.Equal(ErrorCodes.Type0034, diagnostic.Code);
        Assert.DoesNotContain("{context}", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_discarded_fallible_binding_reports_the_binding_name_and_the_accepted_forms()
    {
        var diagnostic = First("let _value = filesystem.read_file(p\"/tmp/scripts-absent-0070\")");
        Assert.Equal(ErrorCodes.Type0070, diagnostic.Code);
        Assert.Equal(1, diagnostic.Start.Line);
        Assert.Equal(5, diagnostic.Start.Column);
        Assert.Contains("never read", diagnostic.Cause, StringComparison.Ordinal);
        Assert.Contains("`let` bindings, parameters, `for` variables and pattern bindings", diagnostic.Cause, StringComparison.Ordinal);
        Assert.Contains("nested `result<result<…>>`", diagnostic.Cause, StringComparison.Ordinal);
        Assert.Contains("`?`", diagnostic.Suggestion, StringComparison.Ordinal);
        Assert.Contains("`match`", diagnostic.Suggestion, StringComparison.Ordinal);
        Assert.Contains("`require_success`", diagnostic.Suggestion, StringComparison.Ordinal);
        Assert.Contains("`value_or`", diagnostic.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Undetermined_return_of_a_never_returning_lambda_names_the_cause()
    {
        // `never` is compatible with every type, so the lambda binds no type variable: the call keeps `list<U>`.
        var diagnostic = First("let _ys = [1].transform(x => process.exit_with_code(1))");
        Assert.Equal(ErrorCodes.Type0029, diagnostic.Code);
        Assert.Contains("a lambda that never returns", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("a lambda that never returns", diagnostic.Cause, StringComparison.Ordinal);
        Assert.Contains("let xs: list<integer> = …", diagnostic.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Undetermined_non_list_return_derives_its_shape_from_the_signature()
    {
        // The suggestion used to be the fixed `list<integer>`; it now renders the partially applied return type.
        var diagnostic = First("let r = \"1\".parse_integer().transform_error(e => process.exit_with_code(1))\nio.write_standard_output_line(\"{r.is_ok()}\")");
        Assert.Equal(ErrorCodes.Type0029, diagnostic.Code);
        Assert.Contains("a lambda that never returns", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("let xs: result<integer, error> = …", diagnostic.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_name_without_a_close_name_gives_an_actionable_fallback()
    {
        var diagnostic = First("let n = totally_unknown_name");
        Assert.Equal(ErrorCodes.Type0001, diagnostic.Code);
        Assert.Contains(
            "It may come from a module: add the `import`.",
            diagnostic.Suggestion,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_close_name_carries_a_machine_applicable_fix()
    {
        var diagnostic = First("let total = 1\nlet n = totl");
        Assert.Equal("total", diagnostic.Fix?.Replacement);
    }

    [Fact]
    public void A_close_method_carries_a_machine_applicable_fix()
    {
        var diagnostic = First("let n = \"abc\".lenght()");
        Assert.Equal("length", diagnostic.Fix?.Replacement);
    }

    [Fact]
    public void A_kind_typo_in_the_error_constructor_carries_a_fix()
    {
        var diagnostic = First("let e = error(\"not_fond\", \"x\", \"y\")");
        Assert.Equal(ErrorCodes.Type0062, diagnostic.Code);
        Assert.Equal("\"not_found\"", diagnostic.Fix?.Replacement);
    }

    [Fact]
    public void A_kind_typo_carries_a_machine_applicable_fix()
    {
        var diagnostic = First("""
            let status = filesystem.get_path_status(p"/tmp")?
            io.write_standard_output_line("{status.kind == "fiel"}")
            """);
        Assert.Equal("\"file\"", diagnostic.Fix?.Replacement);
    }

    [Fact]
    public void The_json_diagnostic_carries_the_fix_edits()
    {
        var diagnostic = First("let n = \"abc\".lenght()");
        var json = Scripts.Core.Reporting.DiagnosticReporter.RenderDiagnosticJson(diagnostic);
        Assert.Contains("\"fix\":", json, StringComparison.Ordinal);
        Assert.Contains("\"replacement\":\"length\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_name_with_a_close_name_keeps_the_close_name()
    {
        var diagnostic = First("let total = 1\nlet n = totl");
        Assert.Equal(ErrorCodes.Type0001, diagnostic.Code);
        Assert.Contains("close name: `total`.", diagnostic.Suggestion, StringComparison.Ordinal);
        Assert.DoesNotContain("import the module", diagnostic.Suggestion, StringComparison.Ordinal);
    }
}
