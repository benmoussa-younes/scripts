using Scripts.Core;

namespace Scripts.Checker.Tests;

public sealed class ModuleTests
{
    private const string Library = """
        public function double(n: integer) -> integer {
            return n * 2
        }
        public let limit = 10
        public let greeting = "hi" + "!"
        let secret = 1
        function hidden() -> integer {
            return secret
        }
        public function reveal() -> integer {
            return hidden() + limit
        }
        public function log(text: string) {
            io.write_standard_output_line(text)
        }
        """;

    [Fact]
    public void Public_members_are_usable_with_their_declared_types()
    {
        var program = Checking.Load(
            "import \"lib.scs\" as lib\nlet a = lib.double(2)\nlet b = lib.limit + 1\nlet c = lib.greeting + \"?\"\nlet d = lib.double(4)\nlib.log(\"x\")\nlet e = lib.reveal()",
            ("lib.scs", Library));
        var result = ProgramChecker.Check(program);
        Assert.Empty(result.Errors);
        var lets = program.Entry.Program.Statements.OfType<LetStatement>().ToDictionary(letStatement => letStatement.Name);
        Assert.Equal("integer", lets["a"].Initializer.ResolvedType!.ToString());
        Assert.Equal("integer", lets["b"].Initializer.ResolvedType!.ToString());
        Assert.Equal("string", lets["c"].Initializer.ResolvedType!.ToString());
        Assert.Equal("integer", lets["d"].Initializer.ResolvedType!.ToString());
    }

    [Fact]
    public void Private_members_are_rejected_for_calls_and_values()
    {
        var modules = new[] { ("lib.scs", Library) };
        Checking.AssertMarkedInModules("import \"lib.scs\" as lib\nlet n = lib.‸hidden()", modules, "SCRIPTS-MODULE-0004");
        Checking.AssertMarkedInModules("import \"lib.scs\" as lib\nlet n = lib.‸secret", modules, "SCRIPTS-MODULE-0004");
        Checking.AssertMarkedInModules("import \"lib.scs\" as lib\nlet n = lib.‸hidden(1)", modules, "SCRIPTS-MODULE-0004");
        Checking.AssertMarkedInModules("import \"lib.scs\" as lib\nlet n = lib.‸secret(1)", modules, "SCRIPTS-MODULE-0004");
    }

    [Fact]
    public void Unknown_members_list_the_public_ones()
    {
        var modules = new[] { ("lib.scs", Library) };
        var result = Checking.AssertMarkedInModules("import \"lib.scs\" as lib\nlet n = lib.‸nothing()", modules, "SCRIPTS-MODULE-0006");
        Assert.Contains("double", result.Errors[0].Suggestion, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", result.Errors[0].Suggestion, StringComparison.Ordinal);
        Checking.AssertMarkedInModules("import \"lib.scs\" as lib\nlet n = lib.‸nothing", modules, "SCRIPTS-MODULE-0006");
        Checking.AssertMarkedInModules("import \"lib.scs\" as lib\nlet n = lib.‸nothing(1)", modules, "SCRIPTS-MODULE-0006");
    }

    [Fact]
    public void Module_functions_are_not_values_and_constants_are_not_callable()
    {
        var modules = new[] { ("lib.scs", Library) };
        Checking.AssertMarkedInModules("import \"lib.scs\" as lib\nlet g = lib.‸double", modules, "SCRIPTS-MODULE-0010");
        Checking.AssertMarkedInModules("import \"lib.scs\" as lib\nlet n = ‸lib.limit(1)", modules, "SCRIPTS-TYPE-0014");
        Checking.AssertMarkedInModules("import \"lib.scs\" as lib\nlet ys = [1].transform(x => lib.double(x))\nio.write_standard_output_line(\"{ys.length()}\")", modules);
    }

    [Fact]
    public void Aliases_follow_the_declaration_rules()
    {
        var modules = new[] { ("lib.scs", Library), ("other.scs", "public let value = 1\n") };
        Checking.AssertMarkedInModules("import \"lib.scs\" as lib\nlet ‸lib = 1", modules, "SCRIPTS-TYPE-0003");
        Checking.AssertMarkedInModules("import \"lib.scs\" as lib\nimport \"other.scs\" as ‸lib", modules, "SCRIPTS-TYPE-0003");
        Checking.AssertMarkedInModules("import \"lib.scs\" as ‸environment", modules, "SCRIPTS-TYPE-0004");
        Checking.AssertMarkedInModules("import \"lib.scs\" as ‸assert", modules, "SCRIPTS-TYPE-0005");
        Checking.AssertMarkedInModules("import \"lib.scs\" as lib\nfunction f(‸lib: integer) {\n}", modules, "SCRIPTS-TYPE-0003");
        Checking.AssertMarkedInModules("import \"lib.scs\" as lib\nlet x = ‸lib", modules, "SCRIPTS-TYPE-0059");
    }

    [Fact]
    public void Module_constants_are_constants_in_the_importer()
    {
        var modules = new[] { ("lib.scs", Library) };
        Checking.AssertClean(
            "import \"lib.scs\" as lib\nfunction show() {\n    io.write_standard_output_line(\"{doubled}\")\n}\nlet doubled = lib.limit * 2\nshow()",
            modules);
        Checking.AssertMarkedInModules(
            "import \"lib.scs\" as lib\nlet computed = lib.double(1)\nfunction show() {\n    io.write_standard_output_line(\"{‸computed}\")\n}",
            modules,
            "SCRIPTS-TYPE-0007");
    }

    [Fact]
    public void Modules_may_only_declare_functions_and_constants()
    {
        var result = Checking.AssertErrorsInModules(
            "import \"lib.scs\" as lib\nio.write_standard_output_line(\"a\")",
            [("lib.scs", "io.write_standard_output_line(\"x\")\npublic let a = 1\npublic let mutable b = 2\nif true {\n}\n")],
            "SCRIPTS-MODULE-0008 1:1",
            "SCRIPTS-MODULE-0009 3:12",
            "SCRIPTS-MODULE-0008 4:1");
        Assert.All(result.Errors, error => Assert.Equal("lib.scs", Path.GetFileName(error.FilePath)));
    }

    [Fact]
    public void Non_constant_module_lets_report_the_reason_and_do_not_cascade()
    {
        var modules = new[] { ("lib.scs", "public let value = \"x\".trim_whitespace()\npublic function show() -> string {\n    return value\n}\n") };
        var result = Checking.AssertErrorsInModules("import \"lib.scs\" as lib\nlet v = lib.value + lib.show()", modules, "SCRIPTS-MODULE-0009 1:20");
        Assert.Contains("call", result.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mutable_module_let_is_reported_on_the_mutable_keyword()
    {
        Checking.AssertErrorsInModules(
            "import \"lib.scs\" as lib\nlet v = lib.counter",
            [("lib.scs", "public let mutable counter = 0\n")],
            "SCRIPTS-MODULE-0009 1:12");
    }

    [Fact]
    public void Each_module_is_checked_once_and_errors_follow_the_load_order()
    {
        var modules = new (string Path, string Text)[]
        {
            ("shared.scs", "public function broken() -> integer {\n    return \"a\"\n}\n"),
            ("first.scs", "import \"shared.scs\" as shared\npublic function one() -> integer {\n    return shared.broken()\n}\n"),
            ("second.scs", "import \"shared.scs\" as shared\npublic function two() -> integer {\n    return shared.broken() + missing_in_second\n}\n"),
        };
        var result = Checking.Check(
            "import \"first.scs\" as first\nimport \"second.scs\" as second\nlet n: string = first.one() + second.two()",
            modules);
        var summary = result.Errors.Select(error => $"{Path.GetFileName(error.FilePath)}:{error.Code}:{error.Start.Line}").ToList();
        Assert.Equal(
            ["shared.scs:SCRIPTS-TYPE-0012:2", "second.scs:SCRIPTS-TYPE-0001:3", "main.scs:SCRIPTS-TYPE-0012:3"],
            summary);
    }

    [Fact]
    public void Module_signature_errors_are_reported_in_the_module_only()
    {
        var result = Checking.Check(
            "import \"lib.scs\" as lib\nlet n = lib.f(1)",
            ("lib.scs", "public function f(a: Unknown) -> integer {\n    return 1\n}\n"));
        var error = Assert.Single(result.Errors);
        Assert.Equal(("SCRIPTS-TYPE-0036", "lib.scs"), (error.Code, Path.GetFileName(error.FilePath)));
    }

    [Fact]
    public void Calls_to_module_functions_check_arity_and_argument_types()
    {
        var modules = new[] { ("lib.scs", Library) };
        Checking.AssertMarkedInModules("import \"lib.scs\" as lib\nlet n = lib.double(‸\"a\")", modules, "SCRIPTS-TYPE-0012");
        Checking.AssertMarkedInModules("import \"lib.scs\" as lib\nlet n = lib.double(1, ‸2)", modules, "SCRIPTS-TYPE-0013");
        Checking.AssertMarkedInModules("import \"lib.scs\" as lib\nlet n = ‸lib.log(\"x\")", modules, "SCRIPTS-TYPE-0034");
    }
}
