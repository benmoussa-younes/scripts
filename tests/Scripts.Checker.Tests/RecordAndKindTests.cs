using Scripts.Core;

namespace Scripts.Checker.Tests;

public sealed class RecordAndKindTests
{
    private static string AfterIoFailure(string body) =>
        "match filesystem.read_file(p\"/scripts-absent\") {\n    ok(content) => io.write_standard_output_line(content)\n    error(failure) => {\n" + body + "\n    }\n}";

    [Theory]
    [InlineData("let _v = process.run(process.request(\"true\", []))?.exit_code", "integer")]
    [InlineData("let _v = process.run(process.request(\"true\", []))?.standard_output", "string")]
    [InlineData("let _v = process.run(process.request(\"true\", []))?.standard_error.lines()", "list<string>")]
    [InlineData("let _v = filesystem.get_path_status(p\"/x\")?.size_bytes", "integer")]
    [InlineData("let _v = filesystem.get_path_status(p\"/x\")?.kind", "string")]
    [InlineData("let _v = filesystem.get_path_status(p\"/x\")?.modified_unix_seconds", "integer")]
    [InlineData("let _v = filesystem.get_path_status(p\"/x\")?.permissions", "integer")]
    [InlineData("let _v = system.get_information()?.kernel_name", "string")]
    [InlineData("let _v = system.get_information()?.machine_architecture", "string")]
    [InlineData("let _v = process.spawn(\"sleep\", [\"1\"])?.process_identifier", "integer")]
    public void Record_fields_have_their_declared_types(string source, string expectedType)
    {
        Assert.Equal(expectedType, Checking.TypeOfLastLet(source).ToString());
    }

    [Fact]
    public void The_standard_input_file_field_and_method_are_typed()
    {
        Assert.Equal("path", Checking.TypeOfLastLet("let _v = process.request(\"true\", []).standard_input_file").ToString());
        Assert.Equal("run_request", Checking.TypeOfLastLet("let _v = process.request(\"true\", []).with_standard_input_from_file(p\"/x\")").ToString());
        Assert.Equal("path", Checking.TypeOfLastLet("let _v = process.request(\"true\", []).with_standard_input_from_file(p\"/x\").standard_input_file").ToString());
        Assert.Equal("string", Checking.TypeOfLastLet("let _v = process.request(\"true\", []).with_standard_input_from_file(p\"/x\").standard_input").ToString());
        Assert.Equal("path", Checking.TypeOfLastLet("let _v = process.request(\"true\", []).with_standard_input(\"x\").standard_input_file").ToString());
    }

    [Theory]
    [InlineData("let outcome = process.run(process.request(\"true\", []))?\nlet _v = outcome.‸output")]
    [InlineData("let status = filesystem.get_path_status(p\"/x\")?\nlet _v = status.‸mode")]
    [InlineData("let info = system.get_information()?\nlet _v = info.‸name")]
    public void Unknown_record_fields_are_reported(string marked)
    {
        var result = Checking.AssertMarked(marked, "SCRIPTS-TYPE-0009");
        Assert.NotEmpty(result.Errors[0].Suggestion);
    }

    [Theory]
    [InlineData("let n = 5\nlet _v = n.‸value")]
    [InlineData("let s = \"a\"\nlet _v = s.‸size")]
    [InlineData("let xs = [1]\nlet _v = xs.‸first")]
    [InlineData("let r = \"1\".parse_integer()\nlet _v = r.‸value")]
    [InlineData("let _v = (x: integer) => x\nlet w = _v.‸name")]
    public void Fields_of_values_without_fields_are_reported(string marked)
    {
        Checking.AssertMarked(marked, "SCRIPTS-TYPE-0010");
    }

    [Fact]
    public void Error_values_expose_message_and_their_own_fields()
    {
        Checking.AssertClean(AfterIoFailure("        io.write_standard_output_line(failure.message)\n        io.write_standard_output_line(failure.subject)\n        io.write_standard_output_line(failure.kind)"));
        Checking.AssertClean("match process.run(process.request(\"nothing\", [])) {\n    ok(_) => io.write_standard_output_line(\"ok\")\n    error(failure) => io.write_standard_output_line(failure.subject + failure.kind + failure.message)\n}");
        Checking.AssertClean("match environment.get_variable(\"HOME\") {\n    ok(_) => io.write_standard_output_line(\"ok\")\n    error(failure) => io.write_standard_output_line(failure.subject + failure.kind + failure.message)\n}");
        Checking.AssertClean("match \"1\".parse_integer() {\n    ok(_) => io.write_standard_output_line(\"ok\")\n    error(failure) => io.write_standard_output_line(failure.kind + failure.message)\n}");
        Checking.AssertMarked(AfterIoFailure("        io.write_standard_output_line(failure.‸program)"), "SCRIPTS-TYPE-0009");
        Checking.AssertMarked(AfterIoFailure("        io.write_standard_output_line(failure.‸name)"), "SCRIPTS-TYPE-0009");
    }

    [Fact]
    public void Kind_literals_must_belong_to_the_closed_set()
    {
        Checking.AssertClean(AfterIoFailure("        if failure.kind == \"not_found\" {\n            io.write_standard_output_line(\"absent\")\n        }"));
        Checking.AssertMarked(AfterIoFailure("        if failure.kind == ‸\"not_fund\" {\n            io.write_standard_output_line(\"absent\")\n        }"), "SCRIPTS-TYPE-0058");
        Checking.AssertMarked(AfterIoFailure("        if failure.kind != ‸\"missing\" {\n            io.write_standard_output_line(\"absent\")\n        }"), "SCRIPTS-TYPE-0058");
        Checking.AssertMarked(AfterIoFailure("        if ‸\"not_fund\" == failure.kind {\n            io.write_standard_output_line(\"absent\")\n        }"), "SCRIPTS-TYPE-0058");
        Checking.AssertMarked("match process.run(process.request(\"nothing\", [])) {\n    ok(_) => io.write_standard_output_line(\"ok\")\n    error(failure) => {\n        if failure.kind == ‸\"timeout\" {\n            io.write_standard_output_line(\"slow\")\n        }\n    }\n}", "SCRIPTS-TYPE-0058");
        Checking.AssertMarked("match \"1\".parse_integer() {\n    ok(_) => io.write_standard_output_line(\"ok\")\n    error(failure) => {\n        if failure.kind == ‸\"invalid_int\" {\n            io.write_standard_output_line(\"bad\")\n        }\n    }\n}", "SCRIPTS-TYPE-0058");
        Checking.AssertMarked("let status = filesystem.get_path_status(p\"/x\")?\nif status.kind == ‸\"folder\" {\n    io.write_standard_output_line(\"d\")\n}", "SCRIPTS-TYPE-0058");
        Checking.AssertClean("let status = filesystem.get_path_status(p\"/x\")?\nif status.kind == \"directory\" or status.kind == \"symlink\" or status.kind == \"file\" or status.kind == \"other\" {\n    io.write_standard_output_line(\"d\")\n}");
        Checking.AssertClean("let report = filesystem.walk_directory(p\"/x\")?\nfor skip in report.skipped_paths {\n    if skip.kind == \"permission_denied\" or skip.kind == \"invalid_encoding\" or skip.kind == \"other\" {\n        io.write_standard_output_line(\"skip\")\n    }\n}");
        Checking.AssertMarked("let report = filesystem.walk_directory(p\"/x\")?\nfor skip in report.skipped_paths {\n    if skip.kind == ‸\"folder\" {\n        io.write_standard_output_line(\"skip\")\n    }\n}", "SCRIPTS-TYPE-0058");
    }

    [Fact]
    public void Kind_patterns_in_a_match_are_checked_too()
    {
        Checking.AssertClean(AfterIoFailure("        match failure.kind {\n            \"not_found\" => io.write_standard_output_line(\"a\")\n            _ => io.write_standard_output_line(\"b\")\n        }"));
        Checking.AssertMarked(AfterIoFailure("        match failure.kind {\n            ‸\"not_fund\" => io.write_standard_output_line(\"a\")\n            _ => io.write_standard_output_line(\"b\")\n        }"), "SCRIPTS-TYPE-0058");
    }

    [Fact]
    public void The_message_lists_the_values_and_suggests_the_closest_one()
    {
        var result = Checking.Check(AfterIoFailure("        if failure.kind == \"not_fund\" {\n            io.write_standard_output_line(\"absent\")\n        }"));
        var error = Assert.Single(result.Errors);
        Assert.Contains("not_found", error.Suggestion, StringComparison.Ordinal);
        Assert.Contains("permission_denied", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Comparing_kind_to_a_non_literal_is_not_checked()
    {
        Checking.AssertClean(AfterIoFailure("        let expected = \"whatever\"\n        if failure.kind == expected {\n            io.write_standard_output_line(\"same\")\n        }"));
    }

    [Fact]
    public void User_code_can_construct_an_error_with_kind_subject_and_message()
    {
        // Standalone value (no expected result): the constructor's type is `error`.
        Assert.Equal("error", Checking.TypeOfLastLet("let failure = error(\"not_found\", \"/x\", \"absent\")").ToString());
        Assert.Equal("error", Checking.TypeOfLastLet("let failure = error(\"not_found\", \"\", \"no context\")").ToString());
        // In an expected `result<T, error>` context the constructor is the failure branch.
        Checking.AssertClean("function f() -> result<integer, error> {\n    return error(\"invalid_format\", \"x\", \"bad\")\n}\nlet r = f()\nio.write_standard_output_line(\"{r.is_ok()}\")");
        Checking.AssertClean("let r: result<integer, error> = error(\"other\", \"\", \"broken\")\nio.write_standard_output_line(\"{not r.is_ok()}\")");
        // The subject is a free-form string, including an interpolation.
        Checking.AssertClean("let name = \"x\"\nlet failure = error(\"not_found\", name, \"absent: {name}\")");
    }

    [Fact]
    public void Error_constructor_kind_must_be_a_literal_from_the_closed_set()
    {
        Checking.AssertMarked(
            "let failure = error(‸\"not_fund\", \"/x\", \"absent\")",
            "SCRIPTS-TYPE-0062");
        Checking.AssertMarked(
            "let kind = \"not_found\"\nlet failure = error(‸kind, \"/x\", \"absent\")",
            "SCRIPTS-TYPE-0062");
        Checking.AssertMarked(
            "let failure = error(‸\"not_found \" + \"x\", \"/x\", \"absent\")",
            "SCRIPTS-TYPE-0062");
        Checking.AssertMarked(
            "let n = 5\nlet failure = error(‸n, \"/x\", \"absent\")",
            "SCRIPTS-TYPE-0062");
        // Every value of the closed set is accepted.
        foreach (var kind in BuiltinTypes.KindsOf("error")!)
        {
            Checking.AssertClean($"let failure = error(\"{kind}\", \"/x\", \"message\")");
        }
    }

    [Fact]
    public void Error_constructor_reports_the_closest_kind_and_the_full_set()
    {
        var result = Checking.Check("let failure = error(\"not_fund\", \"/x\", \"absent\")");
        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorCodes.Type0062, error.Code);
        Assert.Contains("not_found", error.Suggestion, StringComparison.Ordinal);
        Assert.Contains("permission_denied", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Error_constructor_in_a_non_error_result_is_a_type_mismatch()
    {
        Checking.AssertMarked(
            "let r: result<integer, string> = ‸error(\"not_found\", \"/x\", \"absent\")\nio.write_standard_output_line(\"{not r.is_ok()}\")",
            "SCRIPTS-TYPE-0012");
    }

    [Fact]
    public void Error_constructor_subject_and_message_must_be_strings()
    {
        Checking.AssertMarked(
            "let failure = error(\"not_found\", ‸1, \"absent\")",
            "SCRIPTS-TYPE-0012");
        Checking.AssertMarked(
            "let failure = error(\"not_found\", \"/x\", ‸1)",
            "SCRIPTS-TYPE-0012");
    }
}
