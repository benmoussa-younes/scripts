namespace Scripts.Checker.Tests;

public sealed class LintTests
{
    [Theory]
    [InlineData("sh")]
    [InlineData("bash")]
    [InlineData("dash")]
    [InlineData("zsh")]
    [InlineData("csh")]
    [InlineData("scripts")]
    [InlineData("ksh")]
    [InlineData("tcsh")]
    [InlineData("env")]
    [InlineData("xargs")]
    [InlineData("busybox")]
    [InlineData("python")]
    [InlineData("python3")]
    [InlineData("perl")]
    [InlineData("ruby")]
    [InlineData("node")]
    [InlineData("/bin/sh")]
    [InlineData("/usr/bin/env")]
    [InlineData("./tools/python3")]
    public void Launcher_programs_are_flagged_on_the_literal(string program)
    {
        var result = Checking.AssertMarkedWarnings($"let outcome = process.run(process.request({Checking.Mark}\"{program}\", []))?\nio.write_standard_output_line(outcome.standard_output)", "SCRIPTS-LINT-0001");
        Assert.Contains(program, result.Warnings[0].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("let outcome = process.run(process.request({0}\"bash\", []))?\nio.write_standard_output_line(outcome.standard_output)")]
    [InlineData("let handle = process.spawn({0}\"bash\", [])?\nio.write_standard_output_line(\"{{handle.process_identifier}}\")")]
    [InlineData("let request = process.request({0}\"bash\", [])\nio.write_standard_output_line(request.program)")]
    public void Every_program_launching_function_is_checked(string template)
    {
        Checking.AssertMarkedWarnings(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Checking.Mark), "SCRIPTS-LINT-0001");
    }

    [Fact]
    public void Exec_request_checks_its_program_argument()
    {
        Checking.AssertMarkedWarnings(
            "let _request = process.request(‸\"sh\", [\"-c\", \"x\"])",
            "SCRIPTS-LINT-0001");
        Checking.AssertMarkedWarnings(
            "let program = \"true\"\nlet _request = process.request(‸program, [])",
            "SCRIPTS-LINT-0004");
    }

    [Theory]
    [InlineData("let program = \"ls\"\nlet outcome = process.run(process.request(‸program, []))?\nio.write_standard_output_line(outcome.standard_output)")]
    [InlineData("let name = \"ba\"\nlet outcome = process.run(process.request(‸\"{name}sh\", []))?\nio.write_standard_output_line(outcome.standard_output)")]
    [InlineData("let program = \"ls\"\nlet request = process.request(‸program, [])\nio.write_standard_output_line(request.program)")]
    [InlineData("let program = \"ls\"\nlet handle = process.spawn(‸program, [])?\nio.write_standard_output_line(\"{handle.process_identifier}\")")]
    public void Computed_program_names_are_flagged(string source)
    {
        var result = Checking.AssertMarkedWarnings(source, "SCRIPTS-LINT-0004");
        Assert.Contains("computed", result.Warnings[0].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("let outcome = process.run(process.request(\"ls\", []))?\nio.write_standard_output_line(outcome.standard_output)")]
    [InlineData("let outcome = process.run(process.request(\"bashful\", []))?\nio.write_standard_output_line(outcome.standard_output)")]
    [InlineData("let outcome = process.run(process.request(\"/usr/bin/bash/ls\", []))?\nio.write_standard_output_line(outcome.standard_output)")]
    [InlineData("let found = process.find_program_path(\"bash\")\nio.write_standard_output_line(\"{found.is_ok()}\")")]
    [InlineData("let outcome = process.run(process.request(\"Bash\", []))?\nio.write_standard_output_line(outcome.standard_output)")]
    public void Other_calls_are_not_flagged(string source)
    {
        var result = Checking.AssertClean(source);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("let message = \"hello ‸$name\"\nio.write_standard_output_line(message)")]
    [InlineData("let value = 1\nlet message = \"x ‸$value y\"\nio.write_standard_output_line(message)")]
    [InlineData("let message = \"‸$A\"\nio.write_standard_output_line(message)")]
    [InlineData("let message = \"‸$a1 ok\"\nio.write_standard_output_line(message)")]
    public void Dollar_name_inside_a_string_is_flagged(string source)
    {
        var result = Checking.AssertMarkedWarnings(source, "SCRIPTS-LINT-0003");
        Assert.Contains("interpolation", result.Warnings[0].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("let price = \"cost 5$\"\nio.write_standard_output_line(price)")]
    [InlineData("let message = \"a $ b\"\nio.write_standard_output_line(message)")]
    [InlineData("let message = \"$1\"\nio.write_standard_output_line(message)")]
    [InlineData("let value = 1\nio.write_standard_output_line(\"value {value} $\")")]
    [InlineData("io.write_standard_output_line(r\"$HOME/x\")")]
    public void Dollar_without_a_name_is_not_flagged(string source)
    {
        var result = Checking.AssertClean(source);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Mutating_a_captured_variable_in_a_parallel_lambda_is_rejected()
    {
        var result = Checking.AssertMarked(
            "let mutable seen: list<path> = []\nlet _walked = parallel.walk_directory(p\"/tmp\", 4, entry => ‸seen.append(entry))?",
            "SCRIPTS-TYPE-0063");
        Assert.Contains("seen", result.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_nested_function_called_from_a_parallel_lambda_is_rejected()
    {
        var result = Checking.AssertMarked("""
            function outer() -> result<integer, error> {
                let mutable count = 0
                function bump(value: integer) -> integer {
                    count += 1
                    return value
                }
                let values = parallel.transform([1, 2], 2, value => ‸bump(value))?
                return ok(count + values.length())
            }
            match outer() {
                ok(value) => io.write_standard_output_line("{value}")
                error(failure) => io.write_standard_output_line(failure.message)
            }
            """, "SCRIPTS-TYPE-0066");
        Assert.Contains("bump", result.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_nested_function_passed_as_a_parallel_worker_is_rejected()
    {
        Checking.AssertMarked("""
            function outer() -> result<integer, error> {
                let mutable count = 0
                function bump(value: integer) -> integer {
                    count += 1
                    return value
                }
                let values = parallel.transform([1, 2], 2, ‸bump)?
                return ok(count + values.length())
            }
            match outer() {
                ok(value) => io.write_standard_output_line("{value}")
                error(failure) => io.write_standard_output_line(failure.message)
            }
            """, "SCRIPTS-TYPE-0066");
    }

    [Fact]
    public void A_top_level_function_as_a_parallel_worker_is_accepted()
    {
        Checking.AssertClean("""
            function double(value: integer) -> integer {
                return value * 2
            }
            let values = parallel.transform([1, 2], 2, double)?
            io.write_standard_output_line("{values.length()}")
            """);
    }

    [Fact]
    public void Mutating_a_captured_variable_outside_a_parallel_lambda_is_not_flagged()
    {
        var result = Checking.AssertClean("let mutable seen: list<path> = []\nseen.append(p\"/tmp\")");
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Warnings_are_ordered_and_never_count_as_errors()
    {
        var result = Checking.AssertMarkedWarnings(
            "let name = \"echo\"\nlet a = process.run(process.request(‸name, []))?\nlet b = process.run(process.request(‸\"sh\", []))?\nio.write_standard_output_line(a.standard_output + b.standard_output)",
            "SCRIPTS-LINT-0004",
            "SCRIPTS-LINT-0001");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void An_interpolated_format_template_is_rejected_on_the_template()
    {
        var result = Checking.AssertMarked(
            "let name = \"Ada\"\nio.write_standard_output_line(‸\"{name}\".format_template([\"ignored\"]).value_or_fail())",
            "SCRIPTS-TYPE-0064");
        Assert.Contains("interpolated", result.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_format_template_is_rejected_with_the_propagation_operator()
    {
        Checking.AssertMarked(
            "let name = \"Ada\"\nio.write_standard_output_line(‸\"{name}\".format_template([\"ignored\"])?)",
            "SCRIPTS-TYPE-0064");
    }

    [Theory]
    [InlineData("let name = \"Ada\"\nio.write_standard_output_line(r\"{name}\".format_template([\"ignored\"])?)")]
    [InlineData("let name = \"Ada\"\nlet template = \"{name}\"\nio.write_standard_output_line(template.format_template([\"ignored\"])?)")]
    [InlineData("let values = [\"a\"]\nio.write_standard_output_line(r\"{0}\".format_template(values)?)")]
    public void A_format_template_that_is_not_an_interpolated_literal_is_accepted(string source)
    {
        var result = Checking.AssertClean(source);
        Assert.DoesNotContain(result.Errors, error => error.Code == "SCRIPTS-TYPE-0064");
    }

    [Theory]
    [InlineData("let outcome = process.run(process.request(\"ls\", []).with_standard_input(\"/tmp\"))?\nio.write_standard_output_line(outcome.standard_output)")]
    [InlineData("let outcome = process.run(process.request(\"ls\", []).with_standard_input(\"./input\"))?\nio.write_standard_output_line(outcome.standard_output)")]
    [InlineData("let outcome = process.run(process.request(\"ls\", []).with_standard_input(\"../input\"))?\nio.write_standard_output_line(outcome.standard_output)")]
    [InlineData("let request = process.request(\"ls\", []).with_standard_input(\"/tmp\")\nlet _ = process.run(request)?")]
    public void A_directory_like_standard_input_is_flagged(string source)
    {
        var result = Checking.AssertMarkedWarnings(source.Replace("\"/tmp\"", "\u2038\"/tmp\"").Replace("\"./input\"", "\u2038\"./input\"").Replace("\"../input\"", "\u2038\"../input\""), "SCRIPTS-LINT-0005");
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("let outcome = process.run(process.request(\"ls\", []))?\nio.write_standard_output_line(outcome.standard_output)")]
    [InlineData("let outcome = process.run(process.request(\"ls\", []).with_standard_input(\"tmp\"))?\nio.write_standard_output_line(outcome.standard_output)")]
    [InlineData("let outcome = process.run(process.request(\"ls\", []).with_standard_input(\"output.txt\"))?\nio.write_standard_output_line(outcome.standard_output)")]
    [InlineData("let data = filesystem.read_file(p\"x\")?\nlet outcome = process.run(process.request(\"ls\", []).with_standard_input(data))?\nio.write_standard_output_line(outcome.standard_output)")]
    [InlineData("let outcome = process.run(process.request(\"ls\", []).with_working_directory(p\"/tmp\"))?\nio.write_standard_output_line(outcome.standard_output)")]
    public void Other_requests_are_not_flagged_as_swapped(string source)
    {
        var result = Checking.AssertClean(source);
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "SCRIPTS-LINT-0005");
    }

    [Fact]
    public void A_computed_standard_input_is_not_flagged_as_swapped()
    {
        var result = Checking.AssertClean("let directory = \"/tmp\"\nlet outcome = process.run(process.request(\"ls\", []).with_standard_input(directory))?\nio.write_standard_output_line(outcome.standard_output)");
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "SCRIPTS-LINT-0005");
    }

    [Fact]
    public void The_swapped_argument_is_quoted_in_the_warning_message_only()
    {
        var result = Checking.AssertMarkedWarnings(
            "let request = process.request(\"echo\", [\"ok\"]).with_standard_input(‸\"/tmp\")\nlet _ = process.run(request)?",
            "SCRIPTS-LINT-0005");
        // The message quotes the literal; the suggestion names the method that takes the directory.
        Assert.Contains("\"/tmp\"", result.Warnings[0].Message, StringComparison.Ordinal);
        Assert.Contains("with_working_directory", result.Warnings[0].Suggestion, StringComparison.Ordinal);
        Assert.DoesNotContain("\"/tmp\"", result.Warnings[0].Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void A_spawned_program_carries_no_swapped_input_warning()
    {
        var result = Checking.AssertClean("let handle = process.spawn(\"ls\", [])?\nio.write_standard_output_line(\"{handle.process_identifier}\")");
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "SCRIPTS-LINT-0005");
    }

    [Fact]
    public void A_directory_like_input_with_a_wrong_arity_reports_only_the_arity_error()
    {
        var result = Checking.Check("let request = process.request(\"ls\", [], \"extra\")\nlet _ = process.run(request)?");
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "SCRIPTS-LINT-0005");
    }

    [Fact]
    public void A_wrong_format_arity_reports_only_the_arity_error()
    {
        Assert.Equal(
            ["SCRIPTS-TYPE-0013 2:38"],
            Checking.ErrorPositions(Checking.Check("let name = \"Ada\"\nlet text = r\"{name}\".format_template()\nio.write_standard_output_line(text)")));
    }

    [Fact]
    public void Warnings_are_still_reported_next_to_errors()
    {
        var result = Checking.Check("let a = process.run(process.request(\"sh\", []))?\nlet n: integer = \"a\"\nio.write_standard_output_line(a.standard_output)");
        Assert.Equal(["SCRIPTS-TYPE-0012 2:18"], Checking.ErrorPositions(result));
        Assert.Equal(["SCRIPTS-LINT-0001 1:37"], Checking.WarningPositions(result));
    }

    [Fact]
    public void A_discarded_fallible_value_is_an_error_when_the_binding_is_never_read()
    {
        AssertType0070WithoutWarnings("let ‸_ = filesystem.read_file(p\"/tmp/scripts-absent-0070\")");
        AssertType0070WithoutWarnings("let ‸_value = filesystem.read_file(p\"/tmp/scripts-absent-0070\")\nio.write_standard_output_line(\"x\")");
    }

    [Fact]
    public void A_discarded_fallible_parameter_or_for_variable_keeps_firing()
    {
        // The rule covers parameters and `for` variables, with `_` or a `_name`.
        AssertType0070WithoutWarnings("function f(‸_: result<integer, error>) {\n}");
        AssertType0070WithoutWarnings("function f(‸_ignored: result<integer, error>) {\n}");
        AssertType0070WithoutWarnings("let rs: list<result<integer, string>> = []\nfor ‸_ in rs {\n}");
        AssertType0070WithoutWarnings("let rs: list<result<integer, string>> = []\nfor ‸_ignored in rs {\n}");
    }

    [Fact]
    public void A_fallible_value_consumed_by_question_match_or_value_or_is_accepted()
    {
        Checking.AssertClean("let _ = filesystem.read_file(p\"/tmp/scripts-absent-0070\")?");
        Checking.AssertClean("let _value = filesystem.read_file(p\"/tmp/scripts-absent-0070\")?");
        Checking.AssertClean("""
            match filesystem.read_file(p"/tmp/scripts-absent-0070") {
                ok(text) => io.write_standard_output_line(text)
                error(failure) => io.write_standard_output_line(failure.message)
            }
            """);
        Checking.AssertClean("let _ = filesystem.read_file(p\"/tmp/scripts-absent-0070\").value_or(\"\")");
        Checking.AssertClean("let _ = process.run(process.request(\"true\", []))?.require_success()?");
    }

    [Fact]
    public void A_non_fallible_discarded_value_stays_legal_and_silent()
    {
        Checking.AssertClean("let _ = 3\nlet _name = \"a\".trim_whitespace()\nlet _ = [1]");
    }

    [Fact]
    public void A_nested_result_in_a_propagated_success_type_is_still_a_discard()
    {
        // DESIGN 3.8: `?` removes the outer result; the nested result remains in the bound type, so the binding is
        // discarded and SCRIPTS-TYPE-0070 applies.
        AssertType0070WithoutWarnings("""
            function nested() -> result<result<integer, error>, error> {
                return ok(ok(1))
            }
            function consume() -> result<unit, error> {
                let ‸_ = nested()?
                let ‸_done = nested()?
                return ok()
            }
            io.write_standard_output_line("{consume().is_ok()}")
            """);
    }

    [Fact]
    public void A_read_prefixed_binding_is_accepted_without_any_warning()
    {
        AssertNoErrorsOrWarnings("let _value = filesystem.read_file(p\"/tmp/scripts-absent-0070\")\nio.write_standard_output_line(_value.value_or(\"\"))");
        AssertNoErrorsOrWarnings("function f(_r: result<integer, error>) -> integer {\n    return _r.value_or(0)\n}");
        AssertNoErrorsOrWarnings("let rs: list<result<integer, string>> = []\nfor _item in rs {\n    match _item {\n        ok(value) => io.write_standard_output_line(\"{value}\")\n        error(failure) => io.write_standard_output_line(failure)\n    }\n}");
        AssertNoErrorsOrWarnings("let rs: list<result<integer, string>> = []\nfor _item in rs {\n    let _ = _item.value_or(0)\n}");
        AssertNoErrorsOrWarnings("function check(_r: process_result) -> result<unit, error> {\n    let _ = _r.require_success()?\n    return ok()\n}");
    }

    [Fact]
    public void A_pattern_binding_whose_bound_type_contains_result_and_is_never_read_is_rejected()
    {
        AssertType0070WithoutWarnings("""
            function f(r: result<result<integer, error>, error>) {
                match r {
                    ok(‸_value) => io.write_standard_output_line("ok")
                    error(_) => io.write_standard_output_line("error")
                }
            }
            """);
    }

    [Fact]
    public void A_read_pattern_binding_whose_bound_type_contains_result_is_accepted_without_any_warning()
    {
        AssertNoErrorsOrWarnings("""
            function f(r: result<result<integer, error>, error>) {
                match r {
                    ok(_value) => io.write_standard_output_line("{_value.value_or(0)}")
                    error(_) => io.write_standard_output_line("error")
                }
            }
            """);
    }

    [Fact]
    public void Self_concatenation_inside_a_for_loop_is_flagged()
    {
        var result = Checking.AssertMarkedWarnings("""
            let mutable s = ""
            for item in ["a", "b"] {
                ‸s = s + item
            }
            io.write_standard_output_line(s)
            """, "SCRIPTS-LINT-0012");
        Assert.Contains("quadratic", result.Warnings[0].Message, StringComparison.Ordinal);
        Assert.Contains("list<string>", result.Warnings[0].Message, StringComparison.Ordinal);
        Assert.Contains("join", result.Warnings[0].Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_assignment_inside_a_while_loop_is_flagged()
    {
        Checking.AssertMarkedWarnings("""
            let mutable s = ""
            let mutable index = 0
            while index < 2 {
                ‸s += "x"
                index += 1
            }
            io.write_standard_output_line(s)
            """, "SCRIPTS-LINT-0012");
    }

    [Fact]
    public void Self_concatenation_inside_an_if_nested_in_a_loop_is_flagged()
    {
        Checking.AssertMarkedWarnings("""
            let mutable s = ""
            for item in ["a"] {
                if item != "" {
                    ‸s = s + item
                }
            }
            io.write_standard_output_line(s)
            """, "SCRIPTS-LINT-0012");
    }

    [Fact]
    public void A_match_arm_inside_a_loop_is_flagged()
    {
        Checking.AssertMarkedWarnings("""
            let mutable s = ""
            for item in ["a", "b"] {
                match item {
                    "" => io.write_standard_output_line("empty")
                    _ => { ‸s = s + item }
                }
            }
            io.write_standard_output_line(s)
            """, "SCRIPTS-LINT-0012");
    }

    [Fact]
    public void A_left_spine_sum_inside_a_loop_is_flagged()
    {
        Checking.AssertMarkedWarnings("""
            let mutable s = ""
            for item in ["a"] {
                ‸s = s + item + "\n"
            }
            io.write_standard_output_line(s)
            """, "SCRIPTS-LINT-0012");
    }

    [Fact]
    public void A_single_append_outside_a_loop_is_not_flagged()
    {
        var result = Checking.AssertClean("""
            let mutable s = ""
            s = s + "a"
            io.write_standard_output_line(s)
            """);
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "SCRIPTS-LINT-0012");
    }

    [Fact]
    public void Accumulating_a_list_and_joining_is_not_flagged()
    {
        var result = Checking.AssertClean("""
            let mutable parts: list<string> = []
            for item in ["a", "b"] {
                parts.append(item)
            }
            let s = parts.join("")
            io.write_standard_output_line(s)
            """);
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "SCRIPTS-LINT-0012");
    }

    [Fact]
    public void A_list_concatenation_in_a_loop_is_not_flagged()
    {
        var result = Checking.AssertClean("""
            let mutable items: list<string> = []
            for item in ["a", "b"] {
                items = items + [item]
            }
            io.write_standard_output_line("{items.length()}")
            """);
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "SCRIPTS-LINT-0012");
    }

    [Fact]
    public void A_different_variable_on_the_left_is_not_flagged()
    {
        var result = Checking.AssertClean("""
            let mutable s = ""
            let other = "o"
            for item in ["a"] {
                s = other + item
            }
            io.write_standard_output_line(s)
            """);
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "SCRIPTS-LINT-0012");
    }

    [Fact]
    public void A_prepend_in_a_loop_is_not_flagged()
    {
        var result = Checking.AssertClean("""
            let mutable s = ""
            for item in ["a"] {
                s = item + s
            }
            io.write_standard_output_line(s)
            """);
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "SCRIPTS-LINT-0012");
    }

    [Fact]
    public void A_non_string_receiver_in_a_loop_is_not_flagged()
    {
        var result = Checking.AssertClean("""
            let mutable total = 0
            let mutable index = 0
            while index < 2 {
                total = total + index
                total += 1
                index += 1
            }
            io.write_standard_output_line("{total}")
            """);
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "SCRIPTS-LINT-0012");
    }

    [Fact]
    public void A_loop_local_accumulator_is_not_flagged()
    {
        // The string is declared inside the loop body, so it is rebuilt every iteration: the accumulator stays linear.
        var result = Checking.AssertClean("""
            let mutable lines: list<string> = []
            for item in ["a", "b"] {
                let mutable line = ""
                line = line + item
                lines.append(line)
            }
            io.write_standard_output_line(lines.join("\n"))
            """);
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "SCRIPTS-LINT-0012");
    }

    [Fact]
    public void An_accumulator_declared_in_the_outer_loop_is_still_flagged_in_the_inner_loop()
    {
        // Declared outside the innermost loop body, the string accumulates across its iterations: still quadratic.
        Checking.AssertMarkedWarnings("""
            for _outer in ["a", "b"] {
                let mutable s = ""
                for inner in ["x", "y"] {
                    ‸s = s + inner
                }
                io.write_standard_output_line(s)
            }
            """, "SCRIPTS-LINT-0012");
    }

    [Fact]
    public void A_self_append_outside_a_loop_is_not_flagged()
    {
        var result = Checking.AssertClean("""
            let mutable s = ""
            s += "x"
            io.write_standard_output_line(s)
            """);
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "SCRIPTS-LINT-0012");
    }

    [Fact]
    public void A_self_concatenation_with_a_non_string_operand_is_a_type_error_without_the_lint()
    {
        // SCRIPTS-TYPE-0016 rejects the `+`: the assignment is not a string concatenation, so the cost warning is not added.
        var result = Checking.Check("""
            let mutable s = ""
            for item in [1] {
                s = s + item
            }
            io.write_standard_output_line(s)
            """);
        Assert.Contains(result.Errors, error => error.Code == "SCRIPTS-TYPE-0016");
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "SCRIPTS-LINT-0012");
    }

    [Fact]
    public void A_self_append_to_an_unknown_name_is_a_type_error_without_the_lint()
    {
        // SCRIPTS-TYPE-0001 rejects the operand: the assignment is not a typed string concatenation, so no cost warning.
        var result = Checking.Check("""
            let mutable s = ""
            for item in ["a"] {
                s += unknown_name
            }
            io.write_standard_output_line(s)
            """);
        Assert.Contains(result.Errors, error => error.Code == "SCRIPTS-TYPE-0001");
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "SCRIPTS-LINT-0012");
    }

    [Theory]
    // L5: a local, `for`, pattern or non-public script binding never read is SCRIPTS-LINT-0013.
    [InlineData("let ‸unused = \"a\".trim_whitespace()\nio.write_standard_output_line(\"ok\")")]
    [InlineData("for ‸index in range(0, 2) {\n    io.write_standard_output_line(\"t\")\n}")]
    [InlineData("match \"7\".parse_integer() {\n    ok(‸value) => io.write_standard_output_line(\"e\")\n    error(_) => io.write_standard_output_line(\"n\")\n}")]
    [InlineData("function f() -> integer {\n    let ‸inner = 1\n    return 2\n}\nio.write_standard_output_line(\"{f()}\")")]
    [InlineData("let mutable ‸total = 0\nfor n in [1, 2] {\n    total += n\n}")]
    public void An_unread_binding_is_LINT_0013(string marked) =>
        Checking.AssertMarkedWarnings(marked, "SCRIPTS-LINT-0013");

    [Theory]
    [InlineData("let mutable items = [1]\nitems.append(2)")]
    [InlineData("let _kept = \"a\".trim_whitespace()")]
    [InlineData("function f(unused: integer) -> integer {\n    return 1\n}\nio.write_standard_output_line(\"{f(2)}\")")]
    [InlineData("let doubled = [1].transform(n => 2)\nio.write_standard_output_line(\"{doubled.length()}\")")]
    public void Reads_parameters_constants_and_underscore_names_are_not_LINT_0013(string source) =>
        AssertNoErrorsOrWarnings(source);

    [Fact]
    public void An_unread_result_binding_is_TYPE_0049_only()
    {
        var result = Checking.Check("let parsed = \"1\".parse_integer()");
        Assert.Equal(["SCRIPTS-TYPE-0049"], result.Errors.Select(error => error.Code));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void The_LINT_0013_fix_renames_the_declaration_only_when_safe()
    {
        var fix = Checking.Check("let unused = \"a\".trim_whitespace()").Warnings.Single().Fix;
        Assert.NotNull(fix);
        Assert.Equal("_unused", fix.Replacement);
        Assert.Equal((4, 10), (fix.Span.Start, fix.Span.End));

        // A write would be left behind by a declaration-only rename, and an existing `_unused` could be shadowed.
        Assert.Null(Checking.Check("let mutable total = \"a\".length()\ntotal = 1").Warnings.Single().Fix);
        Assert.Null(Checking.Check("let unused = \"a\".trim_whitespace()\nlet _unused = \"b\".trim_whitespace()").Warnings.Single().Fix);
    }

    private static void AssertNoErrorsOrWarnings(string source)
    {
        var result = Checking.Check(source);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    // L3: two consecutive `path` parameters receiving positional arguments are SCRIPTS-LINT-0014.
    [InlineData("filesystem.copy_file(‸p\"a\", p\"b\")?")]
    [InlineData("let a = p\"a\"\nfilesystem.make_hard_link(‸a, p\"b\")?")]
    [InlineData("function move(from: path, to: path) -> unit {\n    io.write_standard_output_line(\"{from} {to}\")\n}\nmove(‸p\"a\", p\"b\")")]
    public void Consecutive_paths_passed_by_position_are_LINT_0014(string marked) =>
        Checking.AssertMarkedWarnings(marked, "SCRIPTS-LINT-0014");

    [Theory]
    [InlineData("let source_path = p\"a\"\nlet destination_path = p\"b\"\nfilesystem.copy_file(source_path, destination_path)?")]
    [InlineData("filesystem.copy_file(source_path = p\"a\", destination_path = p\"b\")?")]
    [InlineData("io.write_standard_output_line(\"a-b\".replace(\"-\", \"+\"))")]
    [InlineData("let copy = (a: path, b: path) => \"{a}{b}\"\nio.write_standard_output_line(copy(p\"a\", p\"b\"))")]
    public void Named_self_describing_string_and_function_value_arguments_are_not_LINT_0014(string source) =>
        AssertNoErrorsOrWarnings(source);

    [Fact]
    public void The_LINT_0014_fix_names_every_argument_and_keeps_their_order()
    {
        const string text = "filesystem.copy_file(p\"a\" ,  p\"b\")?";
        var fix = Checking.Check(text).Warnings.Single().Fix;
        Assert.NotNull(fix);
        Assert.Equal("source_path = p\"a\" ,  destination_path = p\"b\"", fix.Replacement);
        Assert.Equal((21, 33), (fix.Span.Start, fix.Span.End));
    }

    [Fact]
    public void The_LINT_0014_fix_keeps_a_comment_between_the_arguments()
    {
        const string text = "filesystem.copy_file(p\"a\", # keep\n    p\"b\")?";
        var fix = Checking.Check(text).Warnings.Single().Fix;
        Assert.NotNull(fix);
        Assert.Equal("source_path = p\"a\", # keep\n    destination_path = p\"b\"", fix.Replacement);

        var fixedText = text[..fix.Span.Start] + fix.Replacement + text[fix.Span.End..];
        var checkedAfterFix = Checking.Check(fixedText);
        Assert.Empty(checkedAfterFix.Errors);
        Assert.Empty(checkedAfterFix.Warnings);
    }

    private static void AssertType0070WithoutWarnings(string marked)
    {
        var (text, positions) = Checking.StripMarks(marked);
        var result = Checking.Check(text);
        Assert.Empty(result.Warnings);
        Assert.Equal(positions.Count, result.Errors.Count);
        for (var index = 0; index < positions.Count; index++)
        {
            Assert.Equal("SCRIPTS-TYPE-0070", result.Errors[index].Code);
            Assert.Equal(positions[index].Line, result.Errors[index].Start.Line);
            Assert.Equal(positions[index].Column, result.Errors[index].Start.Column);
        }
    }
}
