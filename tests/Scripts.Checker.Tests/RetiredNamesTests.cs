using Scripts.Core;

namespace Scripts.Checker.Tests;

/// <summary>SCRIPTS-TYPE-0068: the earlier-revision retirement map, its fix and its prose-only conversions.</summary>
public sealed class RetiredNamesTests
{
    private static Diagnostic Retired(string source) =>
        Checking.Check(source).Errors.FirstOrDefault(error => error.Code == ErrorCodes.Type0068)
        ?? throw new Xunit.Sdk.XunitException("no SCRIPTS-TYPE-0068 in: " + Checking.Describe(Checking.Check(source)));

    [Theory]
    [InlineData("exit_script(0)", "process.exit_with_code")]
    [InlineData("let x = [1]\nlet y = x.get(0)", "get_at_index")]
    [InlineData("let m = {\"a\": 1}\nlet y = m.get(\"a\")", "get_value")]
    [InlineData("let s = \"a\".trim()", "trim_whitespace")]
    [InlineData("let v = environment.get(\"HOME\")", "get_variable")]
    [InlineData("let v = parallel.map([1], 1, n => n)", "transform")]
    [InlineData("let v = uname()", "system.get_information")]
    [InlineData("let v = write_file(p\"/tmp/x\", \"\")", "filesystem.write_file_overwrite")]
    [InlineData("let v = io.write(\"x\")", "write_standard_output")]
    [InlineData("let v = io.read_line()", "read_standard_input_line")]
    [InlineData("let v = filesystem.write_file_atomically(\"x\", p\"/tmp/x\")", "write_file_overwrite_atomically")]
    public void A_retired_name_is_rejected_with_a_machine_fix(string source, string replacement)
    {
        var diagnostic = Retired(source);

        Assert.Contains(replacement, diagnostic.Message, StringComparison.Ordinal);
        var fix = Assert.IsType<Fix>(diagnostic.Fix);
        Assert.Equal(replacement, fix.Replacement);
    }

    [Fact]
    public void A_retired_type_name_is_rejected_with_a_fix()
    {
        var diagnostic = Retired("let s: file_status = filesystem.get_path_status(p\"/tmp\")?");

        Assert.Contains("path_status", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal("path_status", diagnostic.Fix!.Replacement);
    }

    [Fact]
    public void A_retired_field_name_is_rejected_with_a_fix()
    {
        var diagnostic = Retired("let outcome = process.run(process.request(\"true\", []))?\nlet text = outcome.stdout");

        Assert.Contains("standard_output", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal("standard_output", diagnostic.Fix!.Replacement);
    }

    [Fact]
    public void A_retired_named_argument_is_rejected_with_a_fix()
    {
        var diagnostic = Retired("let part = \"abc\".substring(start_value = 1, count = 1)");

        Assert.Contains("start_index", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal("start_index", diagnostic.Fix!.Replacement);
    }

    [Fact]
    public void A_retired_working_directory_argument_is_rejected_with_a_fix()
    {
        var diagnostic = Retired("let request = process.request(\"pwd\", []).with_working_directory(directory = p\"/tmp\")");

        Assert.Contains("directory_path", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal("directory_path", diagnostic.Fix!.Replacement);
    }

    [Theory]
    [InlineData("let rendered = format(r\"{0}\", [\"a\"])", "format_template")]
    [InlineData("let location = to_path(\"a\")", "to_path")]
    [InlineData("let outcome = exec_shell(\"echo a\")?", "process.run(process.request(program, arguments))")]
    [InlineData("let outcome = process.run_shell(\"echo a\")?", "process.run(process.request(program, arguments))")]
    public void A_global_that_became_a_method_is_prose_only(string source, string hint)
    {
        var diagnostic = Retired(source);

        Assert.Contains(hint, diagnostic.Message + diagnostic.Suggestion, StringComparison.Ordinal);
        Assert.Null(diagnostic.Fix);
    }

    [Fact]
    public void A_user_identifier_may_reuse_a_retired_name()
    {
        var result = Checking.Check("let print = 1\nio.write_standard_output_line(\"{print}\")");

        Assert.DoesNotContain(result.Errors, error => error.Code == ErrorCodes.Type0068);
    }
}
