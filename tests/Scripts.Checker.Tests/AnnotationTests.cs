using Scripts.Core;
using Scripts.Syntax;

namespace Scripts.Checker.Tests;

public sealed class AnnotationTests
{
    private static (LoadedProgram Program, CheckResult Result) CheckProgram(string mainText, params (string Path, string Text)[] modules)
    {
        var program = Checking.Load(mainText, modules);
        var result = ProgramChecker.Check(program);
        Assert.True(result.Errors.Count == 0, Checking.Describe(result));
        return (program, result);
    }

    private static CallExpression FindCall(LoadedProgram program, string calleeText)
    {
        var source = program.Entry.Source.Text;
        return AstWalker.Expressions(program.Entry.Program)
            .OfType<CallExpression>()
            .First(call => source.Substring(call.Callee.Span.Start, call.Callee.Span.Length) == calleeText);
    }

    [Fact]
    public void User_function_calls_point_at_the_declaration()
    {
        var (program, _) = CheckProgram("function f() -> integer {\n    return 1\n}\nlet n = f()");
        var target = FindCall(program, "f").Target!;
        Assert.Equal(CallTargetKind.UserFunction, target.Kind);
        Assert.Equal("f", target.Declaration!.Name);
        Assert.Null(target.Stdlib);
    }

    [Fact]
    public void Calls_through_function_values_are_marked()
    {
        var (program, _) = CheckProgram("let g = (x: integer) => x + 1\nlet n = g(1)");
        Assert.Equal(CallTargetKind.FunctionValue, FindCall(program, "g").Target!.Kind);
        Assert.Equal("function(integer) -> integer", FindCall(program, "g").Callee.ResolvedType!.ToString());
    }

    [Fact]
    public void Stdlib_functions_and_module_functions_carry_their_catalog_entry()
    {
        var (program, _) = CheckProgram("io.write_standard_output_line(\"a\")\nlet entries = filesystem.list_directory(p\"a\").value_or([])\nlet home = environment.get_variable(\"HOME\").value_or(\"\")");
        Assert.Equal(("io.write_standard_output_line", CallTargetKind.StdlibFunction), (FindCall(program, "io.write_standard_output_line").Target!.Stdlib!.QualifiedName, FindCall(program, "io.write_standard_output_line").Target!.Kind));
        Assert.Equal("filesystem.list_directory", FindCall(program, "filesystem.list_directory").Target!.Stdlib!.QualifiedName);
        Assert.Equal("environment.get_variable", FindCall(program, "environment.get_variable").Target!.Stdlib!.QualifiedName);
        Assert.Equal(CallTargetKind.StdlibFunction, FindCall(program, "environment.get_variable").Target!.Kind);
    }

    [Fact]
    public void Method_calls_carry_the_method_entry_and_instantiated_callee_type()
    {
        var (program, _) = CheckProgram("let a = \"x\".trim_whitespace()\nlet b = [1].filter(n => n > 0)");
        var trim = FindCall(program, "\"x\".trim_whitespace");
        Assert.Equal((CallTargetKind.StdlibMethod, "string.trim_whitespace"), (trim.Target!.Kind, trim.Target.Stdlib!.QualifiedName));
        var filter = FindCall(program, "[1].filter");
        Assert.Equal("list<T>.filter", filter.Target!.Stdlib!.QualifiedName);
        Assert.Equal("function(function(integer) -> boolean) -> list<integer>", filter.Callee.ResolvedType!.ToString());
    }

    [Fact]
    public void Module_function_calls_carry_declaration_and_alias()
    {
        var (program, _) = CheckProgram(
            "import \"lib.scs\" as helpers\nlet n = helpers.double(2)\nlet m = helpers.double(3)",
            ("lib.scs", "public function double(n: integer) -> integer {\n    return n * 2\n}\n"));
        var source = program.Entry.Source.Text;
        var calls = AstWalker.Expressions(program.Entry.Program)
            .OfType<CallExpression>()
            .Where(call => source.Substring(call.Callee.Span.Start, call.Callee.Span.Length) == "helpers.double")
            .ToList();
        Assert.Equal(2, calls.Count);
        foreach (var call in calls)
        {
            Assert.Equal(CallTargetKind.ModuleFunction, call.Target!.Kind);
            Assert.Equal(("double", "helpers"), (call.Target.Declaration!.Name, call.Target.ModuleAlias));
        }
    }

    [Fact]
    public void Resolved_types_are_recorded_on_expressions()
    {
        var (program, _) = CheckProgram("let xs = [1, 2]\nlet total = xs.fold(0, (acc, n) => acc + n)\nlet m: map<string, list<integer>> = {}\nlet label = \"{total}\"");
        var expressions = AstWalker.Expressions(program.Entry.Program).ToList();
        var lambda = expressions.OfType<LambdaExpression>().Single();
        Assert.Equal("function(integer, integer) -> integer", lambda.ResolvedType!.ToString());
        Assert.Equal("map<string, list<integer>>", expressions.OfType<MapLiteral>().Single().ResolvedType!.ToString());
        Assert.Equal("string", expressions.OfType<InterpolatedString>().Single().ResolvedType!.ToString());
        Assert.Equal("integer", expressions.OfType<CallExpression>().Single(call => call.Target!.Stdlib!.Name == "fold").ResolvedType!.ToString());
    }

    [Fact]
    public void Member_access_kinds_are_recorded()
    {
        var (program, _) = CheckProgram(
            "import \"lib.scs\" as helpers\nlet outcome = process.run(process.request(\"true\", []))?\nlet code = outcome.exit_code\nlet limit = helpers.limit",
            ("lib.scs", "public let limit = 10\n"));
        var members = AstWalker.Expressions(program.Entry.Program).OfType<MemberAccessExpression>().ToList();
        Assert.Equal(MemberAccessKind.RecordField, members.Single(member => member.MemberName == "exit_code").AccessKind);
        var constant = members.Single(member => member.MemberName == "limit");
        Assert.Equal(MemberAccessKind.ModuleConstant, constant.AccessKind);
        Assert.Equal("limit", constant.ConstantDeclaration!.Name);
        Assert.Equal("integer", constant.ResolvedType!.ToString());
    }

    [Fact]
    public void Constants_are_flagged_on_let_statements()
    {
        var (program, _) = CheckProgram("""
            import "lib.scs" as helpers
            let plain = 1
            let combined = plain + helpers.limit * 2
            let text = "n={plain}"
            let from_call = "x".trim_whitespace()
            let mutable counter = 0
            let items = [1, 2]
            let items_copy = [plain, 2]
            function f() {
                let inner = 1
                io.write_standard_output_line("{inner}")
            }
            """, ("lib.scs", "public let limit = 10\n"));
        var lets = program.Entry.Program.Statements.OfType<LetStatement>().ToDictionary(letStatement => letStatement.Name);
        Assert.True(lets["plain"].IsConstant);
        Assert.True(lets["combined"].IsConstant);
        Assert.True(lets["text"].IsConstant);
        Assert.False(lets["from_call"].IsConstant);
        Assert.False(lets["counter"].IsConstant);
        Assert.True(lets["items"].IsConstant);
        Assert.True(lets["items_copy"].IsConstant);
        var inner = program.Entry.Program.Statements.OfType<FunctionDeclaration>().Single().Body.Statements.OfType<LetStatement>().Single();
        Assert.False(inner.IsConstant);
        Assert.True(program.Modules.Single().Program.Statements.OfType<LetStatement>().Single().IsConstant);
    }

    [Fact]
    public void Every_expression_of_a_clean_script_is_fully_annotated()
    {
        var scripts = Directory.GetFiles(Repository.MigrationDirectory, "*.scs");
        Assert.NotEmpty(scripts);
        foreach (var path in scripts)
        {
            var loaded = ProgramLoader.Load(path, new FileSystemSourceProvider());
            var result = ProgramChecker.Check(loaded.Program!);
            Assert.Empty(result.Errors);
            var moduleNames = loaded.Program!.Entry.Imports.Keys.Concat(["environment", "process", "filesystem", "io", "json", "parallel", "time"]).ToHashSet(StringComparer.Ordinal);
            foreach (var expression in AstWalker.Expressions(loaded.Program!.Entry.Program))
            {
                if (expression is IdentifierExpression identifier && moduleNames.Contains(identifier.Name))
                {
                    continue;
                }

                var name = Path.GetFileName(path) + ": " + expression.GetType().Name + " at " + expression.Span.Start;
                Assert.True(expression.ResolvedType is not null, name + " has no type");
                Assert.False(expression.ResolvedType.ToString().Contains("<error>", StringComparison.Ordinal), name + " is poisoned");
                Assert.False(ContainsTypeVariable(expression.ResolvedType), name + " keeps a type variable");
                if (expression is CallExpression call)
                {
                    Assert.True(call.Target is not null, name + " has no target");
                }
            }
        }
    }

    /// <summary>
    /// A stdlib call whose *inferred* return type would place `unit` in a forbidden position must be rejected
    /// (DESIGN §3.1). `result<unit, error>` stays legal, but `list<result<T,E>>.collect()` with `T = unit`
    /// must not synthesize the forbidden `list<unit>` (adversarial audit B2, F1).
    /// </summary>
    [Fact]
    public void Inferred_return_placing_unit_in_a_forbidden_position_is_rejected()
    {
        // `result<unit, error>` is legal: `unit` is allowed as a result success.
        Checking.AssertClean("let r: result<unit, error> = ok()\nio.write_standard_output_line(\"{r.is_ok()}\")");

        // But collect() must not produce `list<unit>`.
        Checking.AssertErrors(
            "let xs: list<result<unit, error>> = [ok()]\nlet v = xs.collect().value_or_fail()",
            "SCRIPTS-TYPE-0034 2:9");
    }

    private static bool ContainsTypeVariable(ScriptType type) =>
        type switch
        {
            TypeVariable => true,
            ListType list => ContainsTypeVariable(list.Element),
            MapType map => ContainsTypeVariable(map.Key) || ContainsTypeVariable(map.Value),
            ResultType result => ContainsTypeVariable(result.Success) || ContainsTypeVariable(result.Failure),
            FunctionType function => function.Parameters.Any(ContainsTypeVariable) || ContainsTypeVariable(function.Return),
            _ => false,
        };
}
