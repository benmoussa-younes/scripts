using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Scripts.Core;

namespace Scripts.Syntax.Tests;

/// <summary>Independent measurement of AST depth (program = 1) by reflection over the Core node types.</summary>
internal static class AstDepth
{
    /// <summary>Every Core type that the reflection walker would enter (a node whose public properties hold nodes).</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Scripts.Core is rooted for this independent reflection walker; the test measures AST depth, not trimming.")]
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Scripts.Core is rooted for this independent reflection walker; the test measures AST depth, not trimming.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Scripts.Core is rooted for this independent reflection walker; the test measures AST depth, not trimming.")]
    public static IEnumerable<string> WithChildren() =>
        typeof(ProgramNode).Assembly.GetTypes()
            .Where(type => type.IsPublic && !type.IsAbstract && typeof(Node).IsAssignableFrom(type))
            .Where(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(property => HoldsNodes(property.PropertyType)))
            .Select(type => type.Name);

    private static bool HoldsNodes(Type type) =>
        typeof(Node).IsAssignableFrom(type)
        || (type.IsGenericType
            && type.GetGenericArguments().Any(HoldsNodes)
            && typeof(IEnumerable).IsAssignableFrom(type));

    public static int Of(ProgramNode program) =>
        1 + Math.Max(0, program.Imports.Concat<Node>(program.Statements).Select(Of).DefaultIfEmpty(0).Max());

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Scripts.Core is rooted for this independent reflection walker; the test measures AST depth, not trimming.")]
    public static int Of(Node node)
    {
        var deepestChild = 0;
        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = property.GetValue(node);
            if (value is Node child)
            {
                deepestChild = Math.Max(deepestChild, Of(child));
            }
            else if (value is IEnumerable sequence and not string)
            {
                foreach (var item in sequence)
                {
                    // CallArgument is a wrapper around an expression, not a level of the AST (see SyntaxDepthChecker).
                    var childNode = item is CallArgument argument ? argument.Expression : item as Node;
                    if (childNode is not null)
                    {
                        deepestChild = Math.Max(deepestChild, Of(childNode));
                    }
                }
            }
        }

        return 1 + deepestChild;
    }
}

public sealed class DepthLimitTests
{
    private static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

    public static IEnumerable<object[]> Shapes() =>
        new Dictionary<string, Func<int, string>>
        {
            ["lists"] = count => "let x = " + Repeat("[", count) + "1" + Repeat("]", count),
            ["empty lists"] = count => "let x = " + Repeat("[", count) + Repeat("]", count),
            ["unary minus"] = count => "let x = " + Repeat("-", count) + "1",
            ["not"] = count => "let x = " + Repeat("not ", count) + "true",
            ["lambdas"] = count => "let x = " + Repeat("a => ", count) + "a",
            ["parenthesized lambdas"] = count => "let x = " + Repeat("(a) => ", count) + "a",
            ["calls"] = count => "let x = " + Repeat("f(", count) + "1" + Repeat(")", count),
            ["indexes"] = count => "let x = " + Repeat("x[", count) + "1" + Repeat("]", count),
            ["maps"] = count => "let x = " + Repeat("{\"a\": ", count) + "1" + Repeat("}", count),
            ["ok"] = count => "let x = " + Repeat("ok(", count) + "1" + Repeat(")", count),
            ["error"] = count => "let x = " + Repeat("error(", count) + "1" + Repeat(")", count),
            ["match expressions"] = count => "let x = " + Repeat("match y { _ => ", count) + "1" + Repeat(" }", count),
            ["interpolated strings"] = count => "let x = " + Repeat("\"{", count) + "a" + Repeat("}\"", count),
            ["ifs"] = count => Repeat("if a {\n", count) + Repeat("}\n", count),
            ["whiles"] = count => Repeat("while a {\n", count) + Repeat("}\n", count),
            ["fors"] = count => Repeat("for a in b {\n", count) + Repeat("}\n", count),
            ["functions"] = count => Repeat("function f() {\n", count) + Repeat("}\n", count),
            ["match statements"] = count => Repeat("match y {\n_ => {\n", count) + Repeat("}\n}\n", count),
            ["generic types"] = count => "let x: " + Repeat("list<", count) + "integer" + Repeat(">", count) + " = f",
            ["function types"] = count => "let x: " + Repeat("function() -> ", count) + "integer = f",
            ["left-deep binary chain"] = count => "let x = 1" + Repeat(" + 1", count),
            ["left-deep member chain"] = count => "let x = a" + Repeat(".b", count),
            ["left-deep call chain"] = count => "let x = f" + Repeat("()", count),
            ["left-deep propagation"] = count => "let x = a" + Repeat("?", count),
        }.Select(shape => new object[] { shape.Key, shape.Value });

    private static ParseResult Parse(string source) => Parser.Parse(new SourceFile("depth.scs", source), isModule: false);

    [Theory]
    [MemberData(nameof(Shapes))]
    public void The_limit_is_exactly_two_hundred_levels(string name, Func<int, string> build)
    {
        var firstFailing = Enumerable.Range(1, 400).First(count => Parse(build(count)).Error is not null);
        var failure = Parse(build(firstFailing)).Error!;
        Assert.True(failure.Code == ErrorCodes.Parse0005, $"{name}: n={firstFailing}: {Parsing.Describe(failure)}");
        var accepted = Parse(build(firstFailing - 1));
        Assert.True(accepted.Error is null, $"{name}: n={firstFailing - 1}: {Parsing.Describe(accepted.Error)}");
        var levelCost = AstDepth.Of(Parse(build(4)).Program!) - AstDepth.Of(Parse(build(3)).Program!);
        var acceptedDepth = AstDepth.Of(accepted.Program!);
        Assert.True(acceptedDepth <= 200 && 200 - acceptedDepth < levelCost, $"{name}: depth {acceptedDepth} with {levelCost} nodes per level");
        Assert.All(Enumerable.Range(firstFailing, 20), count => Assert.Equal(ErrorCodes.Parse0005, Parse(build(count)).Error?.Code));
    }

    [Fact]
    public void Nested_lists_report_the_first_node_beyond_the_limit()
    {
        Parsing.AssertError("let x = " + Repeat("[", 250) + "1" + Repeat("]", 250), ErrorCodes.Parse0005, 1, 207);
        Parsing.AssertError("let x = " + Repeat("[", 198) + "1" + Repeat("]", 198), ErrorCodes.Parse0005, 1, 207);
        Assert.Null(Parse("let x = " + Repeat("[", 197) + "1" + Repeat("]", 197)).Error);
    }

    /// <summary>
    /// DESIGN §3.2 (S12): parentheses have no node of their own, so they add no depth — a chain of parentheses is
    /// bounded by the parser's stack guard, never by the syntax depth. The depth is measured on the tree.
    /// </summary>
    [Fact]
    public void Parentheses_have_no_ast_node_and_add_no_depth()
    {
        Assert.Null(Parse("let x = " + Repeat("(", 197) + "1" + Repeat(")", 197)).Error);
        Assert.Null(Parse("let x = " + Repeat("(", 3000) + "1" + Repeat(")", 3000)).Error);
    }

    /// <summary>
    /// Before the fix the check ran at the top level only, so a block hid its own statements from the depth limit,
    /// and the parser's internal counter refused some shapes far under the documented 200 (a false positive). The
    /// depth is now measured on the tree of every statement, in blocks and in module files alike.
    /// </summary>
    [Fact]
    public void A_nested_block_is_measured_like_a_top_level_statement()
    {
        static string NestedIfs(int count) => Repeat("if a {\n", count) + Repeat("}\n", count);

        Assert.Null(Parse(NestedIfs(20)).Error);
        Assert.Equal(ErrorCodes.Parse0005, Parse(NestedIfs(300)).Error?.Code);
        Assert.Equal(
            ErrorCodes.Parse0005,
            Parser.Parse(new SourceFile("m.scs", "public let x = 1\n" + NestedIfs(300)), isModule: true).Error?.Code);
    }

    /// <summary>Statement shapes are measured on the tree, so none of them is refused below the documented limit.</summary>
    [Theory]
    [InlineData("if a {\n", 40)]
    [InlineData("while a {\n", 90)]
    [InlineData("for x in b {\n", 90)]
    [InlineData("function f() {\n", 90)]
    [InlineData("match y {\n_ => {\n", 40)]
    public void Statement_shapes_are_not_refused_below_the_documented_limit(string opening, int count)
    {
        var head = Repeat(opening, count);
        var tail = opening.Contains("match", StringComparison.Ordinal) ? Repeat("}\n}\n", count) : Repeat("}\n", count);
        Assert.Null(Parse(head + tail).Error);
    }

    /// <summary>
    /// The first level at which each statement shape fails, and the tree depth it reaches: the two walkers (the
    /// enforced one and the test oracle) must agree at the boundary, so a shape whose walker under-counts (a map
    /// entry, a function parameter, a pattern) is caught here rather than silently escaping the limit.
    /// </summary>
    [Theory]
    [InlineData("if a {\n", 67)]
    [InlineData("while a {\n", 100)]
    [InlineData("for x in b {\n", 100)]
    [InlineData("function f() {\n", 100)]
    [InlineData("match y {\n_ => {\n", 50)]
    public void The_first_refused_level_holds_the_documented_depth(string opening, int firstFailing)
    {
        string Shape(int count) => Repeat(opening, count)
            + (opening.Contains("match", StringComparison.Ordinal) ? Repeat("}\n}\n", count) : Repeat("}\n", count));

        var refused = Parse(Shape(firstFailing));
        Assert.Equal(ErrorCodes.Parse0005, refused.Error?.Code);

        var accepted = Parse(Shape(firstFailing - 1));
        Assert.Null(accepted.Error);
        var depth = AstDepth.Of(accepted.Program!);
        Assert.InRange(depth, 1, 200);
        // One more level would cross the limit: the accepted tree is within one shape-step of 200.
        var step = AstDepth.Of(Parse(Shape(2)).Program!) - AstDepth.Of(Parse(Shape(1)).Program!);
        Assert.True(200 - depth < step, $"{opening.Trim()}: accepted depth {depth}, step {step}");
    }

    /// <summary>
    /// The depth walker must know every node type: its case list is compared with the runtime property walker of the
    /// test oracle (reflection over the Core node tree), so a node added to Core without a walker case fails here.
    /// </summary>
    [Fact]
    public void The_depth_walker_handles_every_node_type_the_oracle_walks()
    {
        var sources = AstDepth.WithChildren().ToHashSet(StringComparer.Ordinal);
        var missing = sources.Except(SyntaxDepthChecker.HandledNodeTypes).OrderBy(name => name, StringComparer.Ordinal).ToList();
        var extra = SyntaxDepthChecker.HandledNodeTypes.Except(sources).OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "node types the depth walker ignores: " + string.Join(", ", missing));
        Assert.True(extra.Count == 0, "node types named by the depth walker but not walked by the oracle: " + string.Join(", ", extra));
    }

    /// <summary>
    /// Behaviour guard for the whole file, not only the parser: a complete program with a valid header, imported
    /// modules and nested blocks must be accepted by the loader whenever it is under the limit (the false positive
    /// of the old counter refused such a program at about 67 `if` levels).
    /// </summary>
    [Fact]
    public void A_complete_program_with_nested_blocks_loads_through_the_loader()
    {
        static string Program(int count) =>
            "import \"./lib.scs\" as lib\n" + "let a = true\n" + Repeat("if a {\n", count) + Repeat("}\n", count);

        var provider = new InMemorySourceProvider()
            .AddText("/main.scs", Program(60))
            .AddText("/lib.scs", "public let value = 1\n");
        var loaded = ProgramLoader.Load("/main.scs", provider);

        Assert.Null(loaded.Error);
        Assert.NotNull(loaded.Program);
    }

    [Fact]
    public void A_left_deep_chain_is_reported_at_the_start_of_the_chain()
    {
        var error = Parse("let x = 1" + Repeat(" + 1", 300)).Error!;
        Assert.Equal((ErrorCodes.Parse0005, 1, 9), (error.Code, error.Start.Line, error.Start.Column));
    }

    [Fact]
    public void Depth_is_measured_per_top_level_statement()
    {
        var shallow = "let x = [[1]]\n";
        var deep = "let y = " + Repeat("[", 250) + "1" + Repeat("]", 250) + "\n";
        var error = Parse(shallow + shallow + deep).Error!;
        Assert.Equal((ErrorCodes.Parse0005, 3, 207), (error.Code, error.Start.Line, error.Start.Column));
    }

    [Fact]
    public void A_deep_statement_does_not_hide_an_earlier_syntax_error()
    {
        var error = Parse("let a = )\n" + Repeat("[", 250)).Error!;
        Assert.Equal((ErrorCodes.Parse0023, 1, 9), (error.Code, error.Start.Line, error.Start.Column));
    }

    [Fact]
    public void Depth_applies_inside_module_files_too()
    {
        var result = Parser.Parse(new SourceFile("m.scs", "public let x = " + Repeat("[", 250) + "1" + Repeat("]", 250)), isModule: true);
        Assert.Equal(ErrorCodes.Parse0005, result.Error?.Code);
    }
}

public sealed class StackSafetyTests
{
    private const int HostileInputStackBytes = 256 * 1024;

    /// <summary>200 levels need more than 256 KiB once .NET keeps its own 128 KiB headroom and the JIT has not optimized yet.</summary>
    private const int AtLimitStackBytes = 512 * 1024;

    private static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

    public static IEnumerable<object[]> Shapes() => DepthLimitTests.Shapes();

    private static ParseResult ParseOnSmallStack(string source, int stackBytes = HostileInputStackBytes) =>
        Parsing.OnSmallStack(() => Parser.Parse(new SourceFile("stack.scs", source), isModule: false), stackBytes);

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Inputs_at_the_depth_limit_parse_on_a_512_kib_stack(string name, Func<int, string> build)
    {
        var firstFailing = Enumerable.Range(1, 400).First(count => Parser.Parse(new SourceFile("stack.scs", build(count)), isModule: false).Error is not null);
        var result = ParseOnSmallStack(build(firstFailing - 1), AtLimitStackBytes);
        Assert.True(result.Error is null, $"{name}: {Parsing.Describe(result.Error)}");
        Assert.Equal(ErrorCodes.Parse0005, ParseOnSmallStack(build(firstFailing), AtLimitStackBytes).Error?.Code);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Inputs_at_the_depth_limit_never_overflow_a_256_kib_stack(string name, Func<int, string> build)
    {
        var firstFailing = Enumerable.Range(1, 400).First(count => Parser.Parse(new SourceFile("stack.scs", build(count)), isModule: false).Error is not null);
        var code = ParseOnSmallStack(build(firstFailing - 1)).Error?.Code;
        Assert.True(code is null or ErrorCodes.Parse0005, $"{name}: {code}");
        Assert.Equal(ErrorCodes.Parse0005, ParseOnSmallStack(build(firstFailing)).Error?.Code);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Absurdly_deep_inputs_are_rejected_without_overflowing_a_256_kib_stack(string name, Func<int, string> build)
    {
        var result = ParseOnSmallStack(build(100_000));
        Assert.True(result.Error?.Code == ErrorCodes.Parse0005, $"{name}: {Parsing.Describe(result.Error)}");
    }

    [Fact]
    public void Unbalanced_openers_without_closers_are_rejected_on_a_small_stack()
    {
        foreach (var opener in new[] { "(", "[", "{", "f(", "if a {\n", "\"{" })
        {
            var result = ParseOnSmallStack("let x = " + Repeat(opener, 50_000));
            Assert.NotNull(result.Error);
        }
    }

    [Fact]
    public void Huge_flat_inputs_parse_in_linear_time()
    {
        var list = "let x = [" + Repeat("1, ", 300_000) + "1]\n";
        Assert.Null(ParseOnSmallStack(list).Error);
        var statements = Repeat("f()\n", 200_000);
        Assert.Equal(200_000, ParseOnSmallStack(statements).Program!.Statements.Count);
        var elseIfs = "if a {\n}" + Repeat(" else if a {\n}", 50_000);
        Assert.Null(ParseOnSmallStack(elseIfs).Error);
    }

    [Fact]
    public void Long_flat_chains_are_rejected_by_depth_not_by_overflow()
    {
        Assert.Equal(ErrorCodes.Parse0005, ParseOnSmallStack("let x = 1" + Repeat(" + 1", 200_000)).Error?.Code);
        Assert.Equal(ErrorCodes.Parse0005, ParseOnSmallStack("let x = a" + Repeat(".b", 200_000)).Error?.Code);
    }

    [Fact]
    public void The_loader_also_survives_a_small_stack()
    {
        var provider = new InMemorySourceProvider().AddText("/main.scs", "let x = " + Repeat("[", 250) + "1" + Repeat("]", 250));
        var result = Parsing.OnSmallStack(() => ProgramLoader.Load("/main.scs", provider));
        Assert.Equal(ErrorCodes.Parse0005, result.Error?.Code);
    }
}
