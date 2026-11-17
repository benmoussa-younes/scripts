using Scripts.Core;

namespace Scripts.Syntax.Tests;

public sealed class StatementShapeTests
{
    [Theory]
    [InlineData("let x = 1", "(let x 1)")]
    [InlineData("let mutable x = 1", "(let mutable x 1)")]
    [InlineData("let _ = f()", "(let _ (call f))")]
    [InlineData("let x: integer = 1", "(let x:integer 1)")]
    [InlineData("let mutable total: integer = 0", "(let mutable total:integer 0)")]
    [InlineData("let l: list<integer> = []", "(let l:list<integer> (list))")]
    [InlineData("let l: list<integer>= []", "(let l:list<integer> (list))")]
    [InlineData("let m: map<string, list<integer>> = {}", "(let m:map<string,list<integer>> (map))")]
    [InlineData("let m: map<string, list<integer>>= {}", "(let m:map<string,list<integer>> (map))")]
    [InlineData("let r: result<unit, error> = ok()", "(let r:result<unit,error> (ok))")]
    [InlineData("let f: function(integer, string) -> boolean = g", "(let f:function(integer,string)->boolean g)")]
    [InlineData("let f: function() -> unit = g", "(let f:function()->unit g)")]
    [InlineData("let f: function(function(integer) -> integer) -> integer = g", "(let f:function(function(integer)->integer)->integer g)")]
    [InlineData("let f: function(integer) -> list<integer> = g", "(let f:function(integer)->list<integer> g)")]
    [InlineData("let x =\n    1", "(let x 1)")]
    [InlineData("let x: \n    integer = 1", "(let x:integer 1)")]
    public void Let_statements(string source, string expected) => Assert.Equal(expected, Parsing.Print(source));

    [Theory]
    [InlineData("x = 1", "(assign = x [] 1)")]
    [InlineData("x += 1", "(assign += x [] 1)")]
    [InlineData("x -= 1", "(assign -= x [] 1)")]
    [InlineData("x *= 2", "(assign *= x [] 2)")]
    [InlineData("x /= 2", "(assign /= x [] 2)")]
    [InlineData("x %= 2", "(assign %= x [] 2)")]
    [InlineData("xs[0] = 1", "(assign = xs [0] 1)")]
    [InlineData("xs[i][j] -= 2", "(assign -= xs [i j] 2)")]
    [InlineData("m[\"k\"] = f(x)", "(assign = m [\"k\"] (call f x))")]
    [InlineData("xs[f(1)] = ys[0]", "(assign = xs [(call f 1)] (idx ys 0))")]
    [InlineData("s += \"x\"", "(assign += s [] \"x\")")]
    [InlineData("x =\n    5", "(assign = x [] 5)")]
    public void Assignments(string source, string expected) => Assert.Equal(expected, Parsing.Print(source));

    [Theory]
    [InlineData("if a {\n}", "(if (a (block)))")]
    [InlineData("if a {\n    f()\n}", "(if (a (block (expr (call f)))))")]
    [InlineData("if a { f() }", "(if (a (block (expr (call f)))))")]
    [InlineData("if a {\n} else {\n}", "(if (a (block)) (else (block)))")]
    [InlineData("if a {\n} else if b {\n} else {\n}", "(if (a (block)) (b (block)) (else (block)))")]
    [InlineData("if a {\n}\nelse {\n}", "(if (a (block)) (else (block)))")]
    [InlineData("if a {\n}\n\n# note\nelse if b {\n}\nelse {\n}", "(if (a (block)) (b (block)) (else (block)))")]
    [InlineData("if a and b {\n}", "(if ((and a b) (block)))")]
    [InlineData("if f(x) == {\"a\": 1} {\n}", "(if ((== (call f x) (map (\"a\" 1))) (block)))")]
    [InlineData("while c {\n    break\n}", "(while c (block break))")]
    [InlineData("while true {\n    continue\n}", "(while true (block continue))")]
    [InlineData("for x in xs {\n}", "(for x xs (block))")]
    [InlineData("for _ in range(0, 3) {\n    f()\n}", "(for _ (call range 0 3) (block (expr (call f))))")]
    [InlineData("for k in m.keys().sorted() {\n}", "(for k (call (. (call (. m keys)) sorted)) (block))")]
    [InlineData("for x in [1, 2] {\n}", "(for x (list 1 2) (block))")]
    [InlineData("for x in {\"a\": 1}.keys() {\n}", "(for x (call (. (map (\"a\" 1)) keys)) (block))")]
    public void Control_flow(string source, string expected) => Assert.Equal(expected, Parsing.Print(source));

    [Theory]
    [InlineData("function f() {\n}", "(function f () (block))")]
    [InlineData("function f(a: integer, b: string) -> boolean {\n    return true\n}", "(function f (a:integer b:string) -> boolean (block (return true)))")]
    [InlineData("function f(a: integer,) {\n}", "(function f (a:integer) (block))")]
    [InlineData("function f(a: list<integer>, g: function(integer) -> integer) -> result<unit, string> {\n}", "(function f (a:list<integer> g:function(integer)->integer) -> result<unit,string> (block))")]
    [InlineData("function f(\n    a: integer,\n    b: integer,\n) -> integer {\n    return a\n}", "(function f (a:integer b:integer) -> integer (block (return a)))")]
    [InlineData("function f() {\n    function g() {\n    }\n    g()\n}", "(function f () (block (function g () (block)) (expr (call g))))")]
    [InlineData("function f() {\n    return\n}", "(function f () (block (return)))")]
    [InlineData("function f() { return }", "(function f () (block (return)))")]
    [InlineData("function f() -> integer { return 1 }", "(function f () -> integer (block (return 1)))")]
    [InlineData("function f() {\n    return\n    g()\n}", "(function f () (block (return) (expr (call g))))")]
    [InlineData("function f() {\n    return 1 +\n        2\n}", "(function f () (block (return (+ 1 2))))")]
    [InlineData("function f() -> list<integer> {\n}", "(function f () -> list<integer> (block))")]
    // S20: `_` is an anonymous parameter, possibly repeated.
    [InlineData("function f(_: integer) -> integer {\n    return 1\n}", "(function f (_:integer) -> integer (block (return 1)))")]
    [InlineData("function f(_: integer, _: integer) -> integer {\n    return 1\n}", "(function f (_:integer _:integer) -> integer (block (return 1)))")]
    public void Functions_and_returns(string source, string expected) => Assert.Equal(expected, Parsing.Print(source));

    [Theory]
    [InlineData("f()", "(expr (call f))")]
    [InlineData("f()?", "(expr (? (call f)))")]
    [InlineData("a.b(c)", "(expr (call (. a b) c))")]
    [InlineData("filesystem.write_file_overwrite(p, q)?", "(expr (? (call (. filesystem write_file_overwrite) p q)))")]
    [InlineData("filesystem.write_file_overwrite(process.find_program_path(\"echo\")?.standard_output, p)?", "(expr (? (call (. filesystem write_file_overwrite) (. (? (call (. process find_program_path) \"echo\")) standard_output) p)))")]
    [InlineData("\n\n# c\n\nf()\n\n\n# d\ng()\n\n", "(expr (call f))\n(expr (call g))")]
    [InlineData("#!/usr/bin/scripts run\nf()", "(expr (call f))")]
    [InlineData("f()   # trailing\ng() # more", "(expr (call f))\n(expr (call g))")]
    [InlineData("", "")]
    [InlineData("# nothing", "")]
    public void Statements_and_separators(string source, string expected) => Assert.Equal(expected, Parsing.Print(source));

    [Theory]
    [InlineData("match x {\n    1 => {\n        f()\n    }\n    _ => g()\n}", "(expr (match-statement x (1 => (block (expr (call f)))) (_ => (call g))))")]
    [InlineData("match r {\n    ok(_) => f()\n    error(e) => g(e)\n}", "(expr (match-statement r ((ok _) => (call f)) ((error e) => (call g e))))")]
    [InlineData("match x {\n    _ => ({\"a\": 1}.length())\n}", "(expr (match-statement x (_ => (call (. (map (\"a\" 1)) length)))))")]
    [InlineData("match x { _ => { f() } }", "(expr (match-statement x (_ => (block (expr (call f))))))")]
    [InlineData("match x {\n    1 => {\n        match y {\n            _ => f()\n        }\n    }\n    _ => g()\n}", "(expr (match-statement x (1 => (block (expr (match-statement y (_ => (call f)))))) (_ => (call g))))")]
    [InlineData("match x {\n    1 | 2 => f(),\n    _ => g(),\n}", "(expr (match-statement x ((alt 1 2) => (call f)) (_ => (call g))))")]
    [InlineData("match x {\n    _ => match y {\n        _ => 1\n    }\n}", "(expr (match-statement x (_ => (match y (_ => 1)))))")]
    [InlineData("let v = match x {\n    1 => \"a\"\n    _ => \"b\"\n}", "(let v (match x (1 => \"a\") (_ => \"b\")))")]
    [InlineData("return match x {\n    _ => 1\n}", "(return (match x (_ => 1)))")]
    public void Match_in_statement_position_has_block_arms(string source, string expected) => Assert.Equal(expected, Parsing.Print(source));

    [Theory]
    [InlineData("let x = 1 +\n    2", "(let x (+ 1 2))")]
    [InlineData("let x = a and\n    b", "(let x (and a b))")]
    [InlineData("let x = not\n    a", "(let x (not a))")]
    [InlineData("let x = -\n    1", "(let x (neg 1))")]
    [InlineData("let x = f(1,\n    2)", "(let x (call f 1 2))")]
    [InlineData("let x = f(\n    1,\n    2\n)", "(let x (call f 1 2))")]
    [InlineData("let x = [\n    1\n]", "(let x (list 1))")]
    [InlineData("let x = {\n    \"a\":\n        1\n}", "(let x (map (\"a\" 1)))")]
    [InlineData("let x = a\n    .b\n    .c()", "(let x (call (. (. a b) c)))")]
    [InlineData("let x = a. \n    b", "(let x (. a b))")]
    [InlineData("let f = x => \n    x", "(let f (lambda (x) x))")]
    [InlineData("let x = a ==\n    b", "(let x (== a b))")]
    [InlineData("let x = f(a\n    .b)", "(let x (call f (. a b)))")]
    [InlineData("let x = f(\n    a,\n\n    # note\n    b,\n)", "(let x (call f a b))")]
    [InlineData("x +=\n    1", "(assign += x [] 1)")]
    [InlineData("io.write_standard_output_line(\n    \"a\"\n)", "(expr (call (. io write_standard_output_line) \"a\"))")]
    public void Line_breaks_are_ignored_where_the_grammar_says_so(string source, string expected) => Assert.Equal(expected, Parsing.Print(source));

    [Theory]
    [InlineData("let x = 1\r\nlet y = 2\r\n")]
    [InlineData("if a {\r\n    f()\r\n}\r\nelse {\r\n}\r\n")]
    [InlineData("let x = [\r\n    1,\r\n    2\r\n]\r\n")]
    public void Crlf_sources_parse_like_lf_sources(string crlfSource)
    {
        var lfSource = crlfSource.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(Parsing.Print(lfSource), Parsing.Print(crlfSource));
    }

    [Fact]
    public void Imports_come_first_and_are_listed_in_order()
    {
        var program = Parsing.Parse("# header\nimport \"a.scs\" as first\nimport \"sub/b.scs\" as second\n\nlet x = first.value\n");
        Assert.Equal("(import \"a.scs\" first)\n(import \"sub/b.scs\" second)\n(let x (. first value))", AstPrinter.Print(program));
        Assert.Equal(["a.scs", "sub/b.scs"], program.Imports.Select(import => import.RelativePath));
    }

    [Theory]
    [InlineData("public function f() {\n}", "(function public f () (block))")]
    [InlineData("function g(a: integer) -> integer {\n    return a\n}", "(function private g (a:integer) -> integer (block (return a)))")]
    [InlineData("public let value = 1", "(let public value 1)")]
    [InlineData("let names: list<string> = [\"a\"]", "(let private names:list<string> (list \"a\"))")]
    [InlineData("public let mutable x = 1", "(let public mutable x 1)")]
    public void Module_declarations_carry_their_visibility(string source, string expected) =>
        Assert.Equal(expected, Parsing.Print(source, isModule: true));

    [Fact]
    public void Modules_parse_statements_so_the_checker_can_report_them()
    {
        Assert.Equal("(expr (call (. io write_standard_output_line) \"x\"))\n(function public f () (block))", Parsing.Print("io.write_standard_output_line(\"x\")\npublic function f() {\n}", isModule: true));
    }
}

public sealed class SpanTests
{
    private static string Slice(string text, Core.Span span) => text.Substring(span.Start, span.End - span.Start);

    [Fact]
    public void Spans_cover_the_whole_construct_including_parentheses()
    {
        const string text = "let x = (1 + 2) * f(3)?";
        var let = Assert.IsType<LetStatement>(Parsing.Parse(text).Statements.Single());
        Assert.Equal(text, Slice(text, let.Span));
        Assert.Equal("x", Slice(text, let.NameSpan));
        var product = Assert.IsType<BinaryExpression>(let.Initializer);
        Assert.Equal("(1 + 2) * f(3)?", Slice(text, product.Span));
        Assert.Equal("*", Slice(text, product.OperatorSpan));
        Assert.Equal("1 + 2", Slice(text, product.Left.Span));
        var propagate = Assert.IsType<PropagateExpression>(product.Right);
        Assert.Equal("f(3)?", Slice(text, propagate.Span));
        Assert.Equal("f(3)", Slice(text, propagate.Operand.Span));
    }

    [Fact]
    public void Let_remembers_the_mutable_keyword_and_visibility_start()
    {
        const string text = "public let mutable total = 0";
        var let = Assert.IsType<LetStatement>(Parsing.Parse(text, isModule: true).Statements.Single());
        Assert.Equal("public let mutable total = 0", Slice(text, let.Span));
        Assert.Equal("mutable", Slice(text, let.MutableSpan!.Value));
        Assert.Null(Assert.IsType<LetStatement>(Parsing.Parse("let y = 1").Statements.Single()).MutableSpan);
    }

    [Fact]
    public void Assignment_remembers_target_and_operator()
    {
        const string text = "xs[0] += 1";
        var assignment = Assert.IsType<AssignmentStatement>(Parsing.Parse(text).Statements.Single());
        Assert.Equal("xs", Slice(text, assignment.TargetSpan));
        Assert.Equal("+=", Slice(text, assignment.OperatorSpan));
        Assert.Equal(text, Slice(text, assignment.Span));
    }

    [Fact]
    public void Calls_end_at_their_closing_parenthesis_and_members_remember_their_name()
    {
        const string text = "a.b(1, 2)";
        var call = Assert.IsType<CallExpression>(((ExpressionStatement)Parsing.Parse(text).Statements.Single()).Expression);
        Assert.Equal(text, Slice(text, call.Span));
        var member = Assert.IsType<MemberAccessExpression>(call.Callee);
        Assert.Equal("b", Slice(text, member.MemberSpan));
        Assert.Equal("1", Slice(text, call.Arguments[0].Span));
    }

    [Fact]
    public void Blocks_functions_and_if_branches_have_exact_spans()
    {
        const string text = "function f(a: integer) -> integer {\n    return a\n}\nif a {\n} else {\n}";
        var program = Parsing.Parse(text);
        var function = Assert.IsType<FunctionDeclaration>(program.Statements[0]);
        Assert.Equal("function f(a: integer) -> integer {\n    return a\n}", Slice(text, function.Span));
        Assert.Equal("f", Slice(text, function.NameSpan));
        Assert.Equal("a: integer", Slice(text, function.Parameters[0].Span));
        Assert.Equal("integer", Slice(text, function.ReturnType!.Span));
        var ifStatement = Assert.IsType<IfStatement>(program.Statements[1]);
        Assert.Equal("if a {\n} else {\n}", Slice(text, ifStatement.Span));
        Assert.Equal("if a {\n}", Slice(text, ifStatement.Branches[0].Span));
    }

    [Fact]
    public void Strings_report_text_and_interpolation_part_spans()
    {
        const string text = "let _ = \"ab{x}cd\"";
        var interpolated = Assert.IsType<InterpolatedString>(((LetStatement)Parsing.Parse(text).Statements.Single()).Initializer);
        Assert.Equal("\"ab{x}cd\"", Slice(text, interpolated.Span));
        Assert.Equal(["ab", "{x}", "cd"], interpolated.Parts.Select(part => Slice(text, part.Span)));
        Assert.Equal("x", Slice(text, ((InterpolationPart)interpolated.Parts[1]).Expression.Span));
    }

    [Fact]
    public void Match_arms_and_patterns_have_spans()
    {
        const string text = "let _ = match x {\n    1 | 2 => \"a\"\n    _ => \"b\"\n}";
        var match = Assert.IsType<MatchExpression>(((LetStatement)Parsing.Parse(text).Statements.Single()).Initializer);
        Assert.False(match.IsStatement);
        Assert.Equal("1 | 2 => \"a\"", Slice(text, match.Arms[0].Span));
        Assert.Equal("1 | 2", Slice(text, match.Arms[0].Pattern.Span));
        Assert.Equal(text.Substring(8), Slice(text, match.Span));
    }

    [Fact]
    public void Negative_pattern_literals_carry_the_negated_value()
    {
        var match = Assert.IsType<MatchExpression>(((LetStatement)Parsing.Parse("let _ = match x { -5 => 1\n _ => 2 }").Statements.Single()).Initializer);
        var literal = Assert.IsType<LiteralPattern>(match.Arms[0].Pattern);
        Assert.Equal(-5, Assert.IsType<IntegerLiteral>(literal.Literal).Value);
    }

    [Fact]
    public void Integer_literals_keep_their_value_and_floats_their_double()
    {
        var program = Parsing.Parse("let a = 0xFF\nlet b = 9223372036854775807\nlet c = 2.5e-3");
        Assert.Equal(255L, ((IntegerLiteral)((LetStatement)program.Statements[0]).Initializer).Value);
        Assert.Equal(long.MaxValue, ((IntegerLiteral)((LetStatement)program.Statements[1]).Initializer).Value);
        Assert.Equal(0.0025, ((FloatLiteral)((LetStatement)program.Statements[2]).Initializer).Value);
    }

    [Fact]
    public void Program_span_covers_the_file_and_comments_do_not_change_statement_spans()
    {
        const string text = "# c\nlet x = 1 # d\n";
        var program = Parsing.Parse(text);
        Assert.Equal(new Core.Span(0, text.Length), program.Span);
        Assert.Equal("let x = 1", Slice(text, program.Statements[0].Span));
    }
}
