namespace Scripts.Checker.Tests;

public sealed class TypeSyntaxTests
{
    [Theory]
    [InlineData("let x: ‸number = 1", "SCRIPTS-TYPE-0036")]
    [InlineData("let x: ‸Foo = 1", "SCRIPTS-TYPE-0036")]
    [InlineData("function f(a: ‸Text) {\n}", "SCRIPTS-TYPE-0036")]
    [InlineData("let x: ‸list<integer, string> = [1]", "SCRIPTS-TYPE-0037")]
    [InlineData("let x: ‸list = [1]", "SCRIPTS-TYPE-0037")]
    [InlineData("let x: ‸map<string> = {}", "SCRIPTS-TYPE-0037")]
    [InlineData("let x: ‸result<integer> = ok(1)", "SCRIPTS-TYPE-0037")]
    [InlineData("let x: ‸integer<string> = 1", "SCRIPTS-TYPE-0037")]
    [InlineData("let m: map<‸float, integer> = {}", "SCRIPTS-TYPE-0035")]
    [InlineData("let m: map<‸list<integer>, integer> = {}", "SCRIPTS-TYPE-0035")]
    [InlineData("let x: ‸never = 1", "SCRIPTS-TYPE-0038")]
    [InlineData("function f() -> ‸never {\n}", "SCRIPTS-TYPE-0038")]
    [InlineData("let x: list<‸unit> = []", "SCRIPTS-TYPE-0034")]
    [InlineData("let x: map<string, ‸unit> = {}", "SCRIPTS-TYPE-0034")]
    [InlineData("let x: result<integer, ‸unit> = ok(1)\nio.write_standard_output_line(\"{x.is_ok()}\")", "SCRIPTS-TYPE-0034")]
    [InlineData("function f(a: ‸unit) {\n}", "SCRIPTS-TYPE-0034")]
    [InlineData("let x: ‸unit = io.write_standard_output_line(\"a\")", "SCRIPTS-TYPE-0034")]
    public void Invalid_types_are_reported_once_on_the_offending_part(string marked, string code)
    {
        Checking.AssertMarked(marked, code);
    }

    [Theory]
    [InlineData("let x: result<unit, string> = ok()\nio.write_standard_output_line(\"{x.is_ok()}\")")]
    [InlineData("let x: function(integer) -> unit = n => io.write_standard_output_line(\"{n}\")")]
    [InlineData("let x: function() -> unit = () => io.write_standard_output_line(\"a\")")]
    [InlineData("function f() -> unit {\n}")]
    [InlineData("function f() -> result<unit, error> {\n    return ok()\n}")]
    [InlineData("let x: list<function(integer) -> integer> = []")]
    [InlineData("let x: map<path, list<result<process_result, error>>> = {}")]
    public void Valid_types_are_accepted(string source)
    {
        Checking.AssertClean(source);
    }

    [Fact]
    public void Type_names_and_value_names_live_in_separate_spaces()
    {
        Checking.AssertClean("let string = 1\nlet integer = 2\nio.write_standard_output_line(\"{string}{integer}\")");
    }

    [Fact]
    public void Record_and_error_type_names_are_valid_annotations()
    {
        Checking.AssertClean("function f(r: process_result, s: path_status, i: system_information, h: process_handle, e: directory_entry, a: error, b: error, c: error, d: error) {\n}");
    }
}
