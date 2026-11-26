using System.Text.Json;
using Scripts.Core;
using Scripts.Core.Reporting;

namespace Scripts.Cli.Tests.Reporting;

[Collection(DiagnosticReporterCollection.Name)]
public sealed class DiagnosticReporterTests
{
    private const string TypeMismatchMessage = "incompatible types: expected `integer`, got `string`";
    private const string TypeMismatchCause = "the value does not have the type announced or expected by the context (annotation, parameter, return, assignment, list element, `match` arm); no implicit conversion exists";
    private const string TypeMismatchSuggestion = "convert explicitly (`.parse_integer()`, `to_float`, `to_integer`, `.to_path()`, interpolation `\"{…}\"`) or fix the type";

    private static Diagnostic CreateDiagnostic(
        SourceFile? file,
        int startOffset,
        int endOffset,
        Severity severity = Severity.Error,
        string code = "SCRIPTS-TYPE-0012",
        string message = TypeMismatchMessage,
        string cause = TypeMismatchCause,
        string suggestion = TypeMismatchSuggestion) =>
        new(code, severity, message, cause, suggestion, file, new Span(startOffset, endOffset));

    private static Diagnostic Underline(string path, string text, string fragment, int occurrenceStart = 0)
    {
        var startOffset = text.IndexOf(fragment, occurrenceStart, StringComparison.Ordinal);
        Assert.True(startOffset >= 0, "fragment missing from the test text");
        return CreateDiagnostic(new SourceFile(path, text), startOffset, startOffset + fragment.Length);
    }

    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + "\n"));

    [Fact]
    public void Design_section_8_2_example_is_rendered_verbatim()
    {
        var diagnostic = Underline("script.scs", "# demo\n# demo\nlet age: integer = \"abc\"\n", "\"abc\"");

        var expected = Lines(
            "error[SCRIPTS-TYPE-0012]: incompatible types: expected `integer`, got `string`",
            "  --> script.scs:3:20",
            "   |",
            " 3 | let age: integer = \"abc\"",
            "   |                    ^^^^^",
            "   = cause: the value does not have the type announced or expected by the context (annotation, parameter, return, assignment, list element, `match` arm); no implicit conversion exists",
            "   = suggestion: convert explicitly (`.parse_integer()`, `to_float`, `to_integer`, `.to_path()`, interpolation `\"{…}\"`) or fix the type");
        Assert.Equal(expected, DiagnosticReporter.RenderHuman(diagnostic));
    }

    [Fact]
    public void Warnings_use_the_warning_label()
    {
        var file = new SourceFile("a.scs", "process.run_shell(\"ls\")\n");
        var diagnostic = CreateDiagnostic(file, 0, 10, Severity.Warning, "SCRIPTS-LINT-0002", "use of `process.run_shell`", "cause", "suggestion");

        var expected = Lines(
            "warning[SCRIPTS-LINT-0002]: use of `process.run_shell`",
            "  --> a.scs:1:1",
            "   |",
            " 1 | process.run_shell(\"ls\")",
            "   | ^^^^^^^^^^",
            "   = cause: cause",
            "   = suggestion: suggestion");
        Assert.Equal(expected, DiagnosticReporter.RenderHuman(diagnostic));
    }

    [Fact]
    public void Tabs_count_as_one_column_and_stay_tabs_in_the_underline_indent()
    {
        var diagnostic = Underline("t.scs", "\tlet x = oops\n", "oops");

        var expected = Lines(
            "error[SCRIPTS-TYPE-0012]: " + TypeMismatchMessage,
            "  --> t.scs:1:10",
            "   |",
            " 1 | \tlet x = oops",
            "   | \t        ^^^^",
            "   = cause: " + TypeMismatchCause,
            "   = suggestion: " + TypeMismatchSuggestion);
        Assert.Equal(expected, DiagnosticReporter.RenderHuman(diagnostic));
    }

    [Fact]
    public void Line_and_paragraph_separators_in_the_quoted_line_are_replaced()
    {
        var diagnostic = Underline("sep.scs", "let x = \"a\u2028b\u2029c\"\n", "a");
        var rendered = DiagnosticReporter.RenderHuman(diagnostic);
        Assert.DoesNotContain("\u2028", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2029", rendered, StringComparison.Ordinal);
        Assert.Contains("\"a\uFFFDb\uFFFDc\"", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Line_separators_in_message_cause_and_suggestion_are_replaced()
    {
        var diagnostic = CreateDiagnostic(
            file: null,
            startOffset: 0,
            endOffset: 0,
            message: "m1\nm2\rm3\u2028m4\u2029m5\tend",
            cause: "c1\nc2\rc3\u2028c4\u2029c5\tend",
            suggestion: "s1\ns2\rs3\u2028s4\u2029s5\tend");

        var rendered = DiagnosticReporter.RenderHuman(diagnostic);

        // The three fields are one line each: the embedded separators became U+FFFD, not extra lines.
        Assert.Equal(4, rendered.Split('\n').Length);
        Assert.DoesNotContain("\r", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2028", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2029", rendered, StringComparison.Ordinal);
        Assert.Contains("m1\uFFFDm2\uFFFDm3\uFFFDm4\uFFFDm5\tend", rendered, StringComparison.Ordinal);
        Assert.Contains("c1\uFFFDc2\uFFFDc3\uFFFDc4\uFFFDc5\tend", rendered, StringComparison.Ordinal);
        Assert.Contains("s1\uFFFDs2\uFFFDs3\uFFFDs4\uFFFDs5\tend", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void The_header_path_and_lone_surrogates_are_sanitized_in_the_human_render()
    {
        var path = "bad\nname\r\u2028\u2029.scs";
        var file = new SourceFile(path, "let x = 1\n");
        var diagnostic = CreateDiagnostic(
            file,
            0,
            1,
            message: "lone \uD800 and pair \U0001F600",
            cause: "cause",
            suggestion: "suggestion");

        var human = DiagnosticReporter.RenderHuman(diagnostic);

        Assert.DoesNotContain("\r", human, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2028", human, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2029", human, StringComparison.Ordinal);
        Assert.Contains("--> bad\uFFFDname\uFFFD\uFFFD\uFFFD.scs:1:1", human, StringComparison.Ordinal);
        Assert.Contains("lone \uFFFD and pair \U0001F600", human, StringComparison.Ordinal);

        var json = DiagnosticReporter.RenderDiagnosticJson(diagnostic);
        // The JSON path keeps its own escaping; the human sanitizer never touches it.
        Assert.Contains("\\u000a", json, StringComparison.Ordinal);
        Assert.Contains("\\u000d", json, StringComparison.Ordinal);
        Assert.Contains("\\u2028", json, StringComparison.Ordinal);
        Assert.Contains("\\u2029", json, StringComparison.Ordinal);
        Assert.Contains("\uFFFD", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A generated or minified script can hold a single line of tens of thousands of code points. Echoing it in full
    /// for every diagnostic made the rendering quadratic and ended in an internal error (254) instead of the
    /// diagnostic; the quoted line is now a bounded window and the carets still land on the underlined character.
    /// </summary>
    [Fact]
    public void A_very_long_line_is_quoted_as_a_bounded_window_around_the_underline()
    {
        var longText = "let " + new string('x', 5000) + " = oops" + new string('y', 5000) + "\n";
        var rendered = DiagnosticReporter.RenderHuman(Underline("long.scs", longText, "oops"));
        var lines = rendered.Split('\n');
        var quoted = lines[3];
        var carets = lines[4];

        Assert.True(quoted.Length <= 130, $"quoted line has {quoted.Length} characters");
        Assert.Contains("oops", quoted, StringComparison.Ordinal);
        Assert.StartsWith(" 1 | …", quoted, StringComparison.Ordinal);
        Assert.EndsWith("…", quoted, StringComparison.Ordinal);
        // A single word on a huge line is underlined where it is in the window, not at the start of the row.
        // The underlined region starts at the word and runs into the following letters, so the caret row is long
        // on purpose; what matters is where it starts: under the word, in the window, never at column 1.
        var caretColumn = 4 + carets.IndexOf('^', StringComparison.Ordinal) - 4;
        Assert.Equal(quoted.IndexOf("oops", StringComparison.Ordinal), caretColumn);
        Assert.True(caretColumn > 3, $"the carets start at column {caretColumn}, not under the word");
    }

    /// <summary>Measuring the rendering of a huge file must stay linear: 15 000 diagnostics on one long line.</summary>
    [Fact]
    public void Fifteen_thousand_diagnostics_on_one_long_line_render_quickly()
    {
        var name = new string('a', 8);
        var source = string.Concat(Enumerable.Repeat(name + " ", 15000));
        var file = new SourceFile("many.scs", source);
        var diagnostics = Enumerable.Range(0, 15000)
            .Select(index => CreateDiagnostic(file, index * 9, index * 9 + 8))
            .ToList();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var rendered = DiagnosticReporter.RenderHuman(diagnostics, []);
        stopwatch.Stop();

        Assert.Contains("error[SCRIPTS-TYPE-0012]", rendered, StringComparison.Ordinal);
        // Before the bound this produced a ~1 GB string and an OutOfMemoryException (internal error, 254).
        // The pre-bound implementation produced a ~1 GB string and an OOM; 30 s still catches that
        // while tolerating a full-solution run where other test assemblies compete for the CPU.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"rendering took {stopwatch.Elapsed}");
    }

    [Fact]
    public void Astral_characters_before_the_span_count_as_one_column()
    {
        var diagnostic = Underline("a.scs", "x = \"😀\" + y\n", "y");

        var expected = Lines(
            "error[SCRIPTS-TYPE-0012]: " + TypeMismatchMessage,
            "  --> a.scs:1:11",
            "   |",
            " 1 | x = \"😀\" + y",
            "   |           ^",
            "   = cause: " + TypeMismatchCause,
            "   = suggestion: " + TypeMismatchSuggestion);
        Assert.Equal(expected, DiagnosticReporter.RenderHuman(diagnostic));
    }

    [Fact]
    public void Astral_characters_inside_the_span_are_underlined_once_each()
    {
        var diagnostic = Underline("a.scs", "let s = \"😀😀\"\n", "\"😀😀\"");

        Assert.Contains("   |         ^^^^\n", DiagnosticReporter.RenderHuman(diagnostic));
        Assert.Contains("a.scs:1:9\n", DiagnosticReporter.RenderHuman(diagnostic));
    }

    [Fact]
    public void Crlf_files_show_lines_without_carriage_return()
    {
        var diagnostic = Underline("c.scs", "let a = 1\r\nlet b = oops\r\nlet c = 3\r\n", "oops");

        var expected = Lines(
            "error[SCRIPTS-TYPE-0012]: " + TypeMismatchMessage,
            "  --> c.scs:2:9",
            "   |",
            " 2 | let b = oops",
            "   |         ^^^^",
            "   = cause: " + TypeMismatchCause,
            "   = suggestion: " + TypeMismatchSuggestion);
        Assert.Equal(expected, DiagnosticReporter.RenderHuman(diagnostic));
        Assert.DoesNotContain('\r', DiagnosticReporter.RenderHuman(diagnostic));
        Assert.DoesNotContain("\r", DiagnosticReporter.RenderDiagnosticJson(diagnostic), StringComparison.Ordinal);
    }

    [Fact]
    public void Span_at_end_of_file_without_trailing_newline_underlines_one_character_past_the_text()
    {
        var text = "let a =";
        var diagnostic = CreateDiagnostic(new SourceFile("e.scs", text), text.Length, text.Length);

        var expected = Lines(
            "error[SCRIPTS-TYPE-0012]: " + TypeMismatchMessage,
            "  --> e.scs:1:8",
            "   |",
            " 1 | let a =",
            "   |        ^",
            "   = cause: " + TypeMismatchCause,
            "   = suggestion: " + TypeMismatchSuggestion);
        Assert.Equal(expected, DiagnosticReporter.RenderHuman(diagnostic));
    }

    [Fact]
    public void Span_at_end_of_file_after_final_newline_shows_an_empty_last_line()
    {
        var text = "let a = 1\n";
        var diagnostic = CreateDiagnostic(new SourceFile("e.scs", text), text.Length, text.Length);

        var expected = Lines(
            "error[SCRIPTS-TYPE-0012]: " + TypeMismatchMessage,
            "  --> e.scs:2:1",
            "   |",
            " 2 |",
            "   | ^",
            "   = cause: " + TypeMismatchCause,
            "   = suggestion: " + TypeMismatchSuggestion);
        Assert.Equal(expected, DiagnosticReporter.RenderHuman(diagnostic));
    }

    [Fact]
    public void Empty_file_is_reported_at_line_one_column_one()
    {
        var diagnostic = CreateDiagnostic(new SourceFile("empty.scs", string.Empty), 0, 0);

        var expected = Lines(
            "error[SCRIPTS-TYPE-0012]: " + TypeMismatchMessage,
            "  --> empty.scs:1:1",
            "   |",
            " 1 |",
            "   | ^",
            "   = cause: " + TypeMismatchCause,
            "   = suggestion: " + TypeMismatchSuggestion);
        Assert.Equal(expected, DiagnosticReporter.RenderHuman(diagnostic));
    }

    [Fact]
    public void Multi_line_span_is_underlined_to_the_end_of_its_first_line()
    {
        var text = "let a = [1,\n  2]\n";
        var startOffset = text.IndexOf('[', StringComparison.Ordinal);
        var diagnostic = CreateDiagnostic(new SourceFile("m.scs", text), startOffset, text.IndexOf(']', StringComparison.Ordinal) + 1);

        var expected = Lines(
            "error[SCRIPTS-TYPE-0012]: " + TypeMismatchMessage,
            "  --> m.scs:1:9",
            "   |",
            " 1 | let a = [1,",
            "   |         ^^^",
            "   = cause: " + TypeMismatchCause,
            "   = suggestion: " + TypeMismatchSuggestion);
        Assert.Equal(expected, DiagnosticReporter.RenderHuman(diagnostic));

        using var document = JsonDocument.Parse(DiagnosticReporter.RenderDiagnosticJson(diagnostic));
        Assert.Equal(1, document.RootElement.GetProperty("line").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("end_line").GetInt32());
        Assert.Equal(5, document.RootElement.GetProperty("end_column").GetInt32());
    }

    [Fact]
    public void Multi_line_span_starting_at_the_line_end_still_underlines_one_character()
    {
        var text = "let a =\n1\n";
        var diagnostic = CreateDiagnostic(new SourceFile("m.scs", text), 7, 9);

        Assert.Contains(" 1 | let a =\n   |        ^\n", DiagnosticReporter.RenderHuman(diagnostic));
    }

    [Fact]
    public void Line_number_width_follows_the_number_of_digits()
    {
        var text = string.Concat(Enumerable.Repeat("let a = 1\n", 9)) + "let b = oops\n";
        var diagnostic = Underline("w.scs", text, "oops");

        var expected = Lines(
            "error[SCRIPTS-TYPE-0012]: " + TypeMismatchMessage,
            "   --> w.scs:10:9",
            "    |",
            " 10 | let b = oops",
            "    |         ^^^^",
            "    = cause: " + TypeMismatchCause,
            "    = suggestion: " + TypeMismatchSuggestion);
        Assert.Equal(expected, DiagnosticReporter.RenderHuman(diagnostic));
    }

    [Fact]
    public void Control_characters_in_the_excerpt_are_made_printable_without_changing_columns()
    {
        var diagnostic = Underline("r.scs", "let a = 1\rlet b = oops\n", "oops");

        Assert.Contains(" 1 | let a = 1\uFFFDlet b = oops\n", DiagnosticReporter.RenderHuman(diagnostic));
        Assert.Contains("r.scs:1:19\n", DiagnosticReporter.RenderHuman(diagnostic));
    }

    [Fact]
    public void Diagnostic_without_file_omits_location_and_excerpt()
    {
        var diagnostic = Core.Diagnostics.Create(ErrorCodes.Cli0002, null, default, ("command", "frobnicate"));

        var expected = Lines(
            "error[SCRIPTS-CLI-0002]: unknown command: `frobnicate`",
            "   = cause: " + diagnostic.Cause,
            "   = suggestion: available commands: run, check, format, explain, describe, help");
        Assert.Equal(expected, DiagnosticReporter.RenderHuman(diagnostic));
    }

    [Fact]
    public void Human_list_puts_errors_before_warnings_separated_by_a_blank_line()
    {
        var error = Core.Diagnostics.Create(ErrorCodes.Cli0005, null, default, ("command", "run"));
        var warning = new Diagnostic("SCRIPTS-LINT-0002", Severity.Warning, "w", "c", "s", null, default);

        var rendered = DiagnosticReporter.RenderHuman([error], [warning]);

        Assert.Equal(
            Lines(
                "error[SCRIPTS-CLI-0005]: missing script for `scripts run`",
                "   = cause: " + error.Cause,
                "   = suggestion: `scripts run <script.scs>`",
                string.Empty,
                "warning[SCRIPTS-LINT-0002]: w",
                "   = cause: c",
                "   = suggestion: s"),
            rendered);
        Assert.Equal(string.Empty, DiagnosticReporter.RenderHuman([], []));
    }

    [Fact]
    public void Diagnostic_json_has_the_fixed_key_order_and_exact_text()
    {
        var diagnostic = Underline("script.scs", "# demo\n# demo\nlet age: integer = \"abc\"\n", "\"abc\"");

        const string expected =
            "{\"code\":\"SCRIPTS-TYPE-0012\",\"severity\":\"error\",\"message\":\"incompatible types: expected `integer`, got `string`\","
            + "\"file\":\"script.scs\",\"line\":3,\"column\":20,\"end_line\":3,\"end_column\":25,"
            + "\"source_line\":\"let age: integer = \\\"abc\\\"\",\"cause\":\"the value does not have the type announced or expected by the context (annotation, parameter, return, assignment, list element, `match` arm); no implicit conversion exists\","
            + "\"suggestion\":\"convert explicitly (`.parse_integer()`, `to_float`, `to_integer`, `.to_path()`, interpolation `\\\"{…}\\\"`) or fix the type\"}";
        var actual = DiagnosticReporter.RenderDiagnosticJson(diagnostic);
        Assert.Equal(expected, actual);
        AssertDiagnosticKeys(actual);
    }

    [Fact]
    public void Diagnostic_json_columns_count_code_points_and_end_column_is_exclusive()
    {
        var diagnostic = Underline("a.scs", "x = \"😀\" + y\n", "y");

        using var document = JsonDocument.Parse(DiagnosticReporter.RenderDiagnosticJson(diagnostic));
        Assert.Equal(11, document.RootElement.GetProperty("column").GetInt32());
        Assert.Equal(12, document.RootElement.GetProperty("end_column").GetInt32());
        Assert.Equal("x = \"😀\" + y", document.RootElement.GetProperty("source_line").GetString());
    }

    [Fact]
    public void Diagnostic_json_without_position_uses_null_for_every_position_key()
    {
        var diagnostic = Core.Diagnostics.Create(ErrorCodes.Cli0005, null, default, ("command", "check"));

        var actual = DiagnosticReporter.RenderDiagnosticJson(diagnostic);

        Assert.Equal(
            "{\"code\":\"SCRIPTS-CLI-0005\",\"severity\":\"error\",\"message\":\"missing script for `scripts check`\","
            + "\"file\":null,\"line\":null,\"column\":null,\"end_line\":null,\"end_column\":null,\"source_line\":null,"
            + "\"cause\":\"" + diagnostic.Cause + "\",\"suggestion\":\"`scripts check <script.scs>`\"}",
            actual);
        AssertDiagnosticKeys(actual);
    }

    [Fact]
    public void Warning_severity_is_written_as_warning()
    {
        var diagnostic = new Diagnostic("SCRIPTS-LINT-0001", Severity.Warning, "m", "c", "s", null, default);

        using var document = JsonDocument.Parse(DiagnosticReporter.RenderDiagnosticJson(diagnostic));
        Assert.Equal("warning", document.RootElement.GetProperty("severity").GetString());
    }

    [Fact]
    public void Errors_json_document_for_success_is_the_documented_literal()
    {
        Assert.Equal(
            "{\"schema_version\":1,\"success\":true,\"errors\":[],\"warnings\":[]}",
            DiagnosticReporter.RenderErrorsJsonDocument([], []));
    }

    [Fact]
    public void Errors_json_document_lists_errors_and_warnings_in_order()
    {
        var error = Underline("script.scs", "# demo\n# demo\nlet age: integer = \"abc\"\n", "\"abc\"");
        var warning = new Diagnostic("SCRIPTS-LINT-0002", Severity.Warning, "w", "c", "s", null, default);

        var document = DiagnosticReporter.RenderErrorsJsonDocument([error], [warning]);

        Assert.Equal(
            "{\"schema_version\":1,\"success\":false,\"errors\":[" + DiagnosticReporter.RenderDiagnosticJson(error)
            + "],\"warnings\":[" + DiagnosticReporter.RenderDiagnosticJson(warning) + "]}",
            document);
        using var parsed = JsonDocument.Parse(document);
        Assert.Equal(["schema_version", "success", "errors", "warnings"], PropertyNames(parsed.RootElement));
        Assert.Single(parsed.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("warning", parsed.RootElement.GetProperty("warnings")[0].GetProperty("severity").GetString());
    }

    [Fact]
    public void Errors_json_success_is_true_when_only_warnings_exist()
    {
        var warning = new Diagnostic("SCRIPTS-LINT-0001", Severity.Warning, "w", "c", "s", null, default);

        using var parsed = JsonDocument.Parse(DiagnosticReporter.RenderErrorsJsonDocument([], [warning]));
        Assert.True(parsed.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void Errors_json_success_is_false_when_warnings_are_denied()
    {
        var warning = new Diagnostic("SCRIPTS-LINT-0001", Severity.Warning, "w", "c", "s", null, default);

        using var parsed = JsonDocument.Parse(DiagnosticReporter.RenderErrorsJsonDocument([], [warning], denyWarnings: true));
        Assert.False(parsed.RootElement.GetProperty("success").GetBoolean());
        Assert.Single(parsed.RootElement.GetProperty("warnings").EnumerateArray());
        Assert.Empty(parsed.RootElement.GetProperty("errors").EnumerateArray());
        Assert.True(JsonDocument.Parse(DiagnosticReporter.RenderErrorsJsonDocument([], [], denyWarnings: true)).RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void Errors_json_document_is_a_single_line()
    {
        var diagnostic = new Diagnostic("SCRIPTS-RUNTIME-0001", Severity.Error, "ligne1\nligne2", "c\r\n", "s\u2028", null, default);

        var document = DiagnosticReporter.RenderErrorsJsonDocument([diagnostic], []);

        Assert.DoesNotContain('\n', document);
        Assert.DoesNotContain('\r', document);
        Assert.DoesNotContain('\u2028', document);
        Assert.Contains("ligne1\\u000aligne2", document, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_json_document_for_a_normal_run_matches_the_documented_literal()
    {
        var document = DiagnosticReporter.RenderRunJsonDocument(0, RunTermination.Normal, "hello\n", string.Empty, null, [], []);

        Assert.Equal(
            "{\"schema_version\":1,\"exit_code\":0,\"success\":true,\"termination\":\"normal\",\"stdout\":\"hello\\u000a\",\"stderr\":\"\",\"result\":null,\"errors\":[],\"warnings\":[]}",
            document);
        using var parsed = JsonDocument.Parse(document);
        Assert.Equal(
            ["schema_version", "exit_code", "success", "termination", "stdout", "stderr", "result", "errors", "warnings"],
            PropertyNames(parsed.RootElement));
    }

    [Fact]
    public void Run_json_document_embeds_the_pre_rendered_result_fragment()
    {
        var document = DiagnosticReporter.RenderRunJsonDocument(0, RunTermination.Normal, string.Empty, string.Empty, "{\"ok\":[1,2.5]}", [], []);

        using var parsed = JsonDocument.Parse(document);
        Assert.Equal(2.5, parsed.RootElement.GetProperty("result").GetProperty("ok")[1].GetDouble());
        Assert.Contains("\"result\":{\"ok\":[1,2.5]},\"errors\"", document, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_json_success_requires_zero_exit_code_and_no_error()
    {
        var error = Core.Diagnostics.Create(ErrorCodes.Cli0005, null, default, ("command", "run"));
        var warning = new Diagnostic("SCRIPTS-LINT-0002", Severity.Warning, "w", "c", "s", null, default);

        Assert.True(Success(DiagnosticReporter.RenderRunJsonDocument(0, RunTermination.Normal, "", "", null, [], [warning])));
        Assert.False(Success(DiagnosticReporter.RenderRunJsonDocument(3, RunTermination.ExitScript, "", "", null, [], [])));
        Assert.False(Success(DiagnosticReporter.RenderRunJsonDocument(0, RunTermination.Normal, "", "", null, [error], [])));
        Assert.False(Success(DiagnosticReporter.RenderRunJsonDocument(ExitCodes.StaticError, RunTermination.StaticError, "", "", null, [error], [])));
    }

    [Theory]
    [InlineData(RunTermination.Normal, "normal")]
    [InlineData(RunTermination.ExitScript, "exit_script")]
    [InlineData(RunTermination.StaticError, "static_error")]
    [InlineData(RunTermination.RuntimeError, "runtime_error")]
    [InlineData(RunTermination.Signal, "signal")]
    public void Termination_values_are_the_documented_names(RunTermination termination, string expectedName)
    {
        Assert.Equal(expectedName, termination.ToJsonName());
        var document = DiagnosticReporter.RenderRunJsonDocument(ExitCodes.RuntimeFailure, termination, "", "", null, [], []);
        using var parsed = JsonDocument.Parse(document);
        Assert.Equal(expectedName, parsed.RootElement.GetProperty("termination").GetString());
    }

    private static bool Success(string document)
    {
        using var parsed = JsonDocument.Parse(document);
        return parsed.RootElement.GetProperty("success").GetBoolean();
    }

    private static List<string> PropertyNames(JsonElement element) => element.EnumerateObject().Select(property => property.Name).ToList();

    private static void AssertDiagnosticKeys(string diagnosticJson)
    {
        using var parsed = JsonDocument.Parse(diagnosticJson);
        Assert.Equal(
            ["code", "severity", "message", "file", "line", "column", "end_line", "end_column", "source_line", "cause", "suggestion"],
            PropertyNames(parsed.RootElement));
    }
}
