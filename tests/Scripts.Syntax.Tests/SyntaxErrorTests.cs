using Scripts.Core;

namespace Scripts.Syntax.Tests;

public sealed class SyntaxErrorTests
{
    [Theory]
    // generic unexpected token / end of file
    [InlineData("let x = )", ErrorCodes.Parse0023, 1, 9)]
    [InlineData("f(1 2)", ErrorCodes.Parse0023, 1, 5)]
    [InlineData("f(1,,2)", ErrorCodes.Parse0023, 1, 5)]
    [InlineData("f(a = 1,,)", ErrorCodes.Parse0023, 1, 9)]
    [InlineData("f(_ = 1)", ErrorCodes.Parse0023, 1, 3)]
    // a call is fully positional or fully named (DESIGN 5.3): the first argument of the other form is PARSE-0058
    [InlineData("f(1, b = 2)", ErrorCodes.Parse0058, 1, 6)]
    [InlineData("f(a = 1, 2)", ErrorCodes.Parse0058, 1, 10)]
    [InlineData("f(a = 1, b)", ErrorCodes.Parse0058, 1, 10)]
    [InlineData("f(a = 1, b.c = 2)", ErrorCodes.Parse0058, 1, 10)]
    [InlineData("let x = error()", ErrorCodes.Parse0023, 1, 15)]
    [InlineData("else {\n}", ErrorCodes.Parse0023, 1, 1)]
    [InlineData("while c {\n}\nelse {\n}", ErrorCodes.Parse0023, 3, 1)]
    [InlineData("let x = a == not b", ErrorCodes.Parse0023, 1, 14)]
    [InlineData("}", ErrorCodes.Parse0023, 1, 1)]
    [InlineData("let x =", ErrorCodes.Parse0023, 1, 8)]
    [InlineData("let x = 1 +", ErrorCodes.Parse0023, 1, 12)]
    [InlineData("let a = 1 let b = 2", ErrorCodes.Parse0023, 1, 11)]
    [InlineData("let x = if", ErrorCodes.Parse0057, 1, 9)]
    [InlineData("let f = (a, b)", ErrorCodes.Parse0023, 1, 11)]
    [InlineData("for x xs {\n}", ErrorCodes.Parse0023, 1, 7)]
    [InlineData("match x {\n    1 => f() g()\n}", ErrorCodes.Parse0023, 2, 14)]
    // after `=>`, `{` opens a block: a map literal arm is parenthesized; only the last line of a block arm of a `match` expression is a bare value
    [InlineData("let m = match x { _ => {\"a\": 1} }", ErrorCodes.Parse0023, 1, 28)]
    [InlineData("let m = match x { _ => {\n    a: 1\n} }", ErrorCodes.Parse0023, 2, 6)]
    [InlineData("let v = match x {\n    _ => {\n        1\n        2\n    }\n}", ErrorCodes.Parse0039, 3, 9)]
    [InlineData("match x {\n    _ => {\n        1\n    }\n}", ErrorCodes.Parse0039, 3, 9)]
    [InlineData("import \"a.scs\" lib", ErrorCodes.Module0007, 1, 8)]
    [InlineData("import \"a.scs\" as lib extra", ErrorCodes.Parse0023, 1, 23)]
    [InlineData("function f(a b) {\n}", ErrorCodes.Parse0023, 1, 14)]
    [InlineData("let x: = 1", ErrorCodes.Parse0023, 1, 8)]
    [InlineData("let x: list<> = []", ErrorCodes.Parse0023, 1, 13)]
    [InlineData("let x: list<integer = []", ErrorCodes.Parse0023, 1, 21)]
    [InlineData("let x 5", ErrorCodes.Parse0023, 1, 7)]
    [InlineData("let = 1", ErrorCodes.Parse0023, 1, 5)]
    [InlineData("function (a: integer) {\n}", ErrorCodes.Parse0023, 1, 10)]
    // unclosed delimiters
    [InlineData("if true {\n    io.write_standard_output_line(\"a\")\n", ErrorCodes.Parse0024, 1, 9)]
    [InlineData("f(1,", ErrorCodes.Parse0024, 1, 2)]
    [InlineData("let x = [1, 2", ErrorCodes.Parse0024, 1, 9)]
    [InlineData("let m = {\"a\": 1", ErrorCodes.Parse0024, 1, 9)]
    [InlineData("foo(bar(", ErrorCodes.Parse0024, 1, 8)]
    [InlineData("function f() {\n    if a {\n", ErrorCodes.Parse0024, 2, 10)]
    [InlineData("let x = match y {\n    _ => 1\n", ErrorCodes.Parse0024, 1, 17)]
    [InlineData("let x = f(\n", ErrorCodes.Parse0024, 1, 10)]
    // keywords and reserved words
    [InlineData("let while = 1", ErrorCodes.Parse0025, 1, 5)]
    [InlineData("let mutable if = 1", ErrorCodes.Parse0025, 1, 13)]
    [InlineData("let true = 1", ErrorCodes.Parse0025, 1, 5)]
    [InlineData("function if() {\n}", ErrorCodes.Parse0025, 1, 10)]
    [InlineData("function f(in: integer) {\n}", ErrorCodes.Parse0025, 1, 12)]
    [InlineData("for let in xs {\n}", ErrorCodes.Parse0025, 1, 5)]
    [InlineData("import \"a.scs\" as as", ErrorCodes.Parse0025, 1, 19)]
    [InlineData("let f = (match) => 1", ErrorCodes.Parse0025, 1, 10)]
    [InlineData("let f = and => 1", ErrorCodes.Parse0023, 1, 9)]
    [InlineData("match x {\n    while => 1\n}", ErrorCodes.Parse0025, 2, 5)]
    [InlineData("match x {\n    ok(break) => 1\n}", ErrorCodes.Parse0025, 2, 8)]
    [InlineData("let async = 1", ErrorCodes.Parse0026, 1, 5)]
    [InlineData("io.write_standard_output_line(async)", ErrorCodes.Parse0026, 1, 31)]
    [InlineData("function try() {\n}", ErrorCodes.Parse0026, 1, 10)]
    [InlineData("x.catch()", ErrorCodes.Parse0026, 1, 3)]
    [InlineData("let t: type = 1", ErrorCodes.Parse0026, 1, 8)]
    [InlineData("let f = async => 1", ErrorCodes.Parse0026, 1, 9)]
    [InlineData("let x = _", ErrorCodes.Parse0027, 1, 9)]
    [InlineData("f(_)", ErrorCodes.Parse0027, 1, 3)]
    [InlineData("_ = 1", ErrorCodes.Parse0027, 1, 1)]
    [InlineData("let x = 1 + _", ErrorCodes.Parse0027, 1, 13)]
    // line breaks
    [InlineData("if true\n{\n}", ErrorCodes.Parse0028, 1, 8)]
    [InlineData("while c\n{\n}", ErrorCodes.Parse0028, 1, 8)]
    [InlineData("for x in xs\n{\n}", ErrorCodes.Parse0028, 1, 12)]
    [InlineData("match x\n{\n    _ => f()\n}", ErrorCodes.Parse0028, 1, 8)]
    [InlineData("function f()\n{\n}", ErrorCodes.Parse0028, 1, 13)]
    [InlineData("function f() -> integer\n{\n}", ErrorCodes.Parse0028, 1, 24)]
    [InlineData("if a {\n} else\n{\n}", ErrorCodes.Parse0028, 2, 7)]
    [InlineData("if true # c\n{\n}", ErrorCodes.Parse0028, 1, 12)]
    [InlineData("if a {\n} else\nif b {\n}", ErrorCodes.Parse0029, 2, 7)]
    [InlineData("if true\nf()", ErrorCodes.Parse0023, 1, 8)]
    [InlineData("let xs = [\n    1\n    -2\n]", ErrorCodes.Parse0030, 2, 6)]
    [InlineData("foo(1\n    (2))", ErrorCodes.Parse0030, 1, 6)]
    [InlineData("let x = [a\n    [0]]", ErrorCodes.Parse0030, 1, 11)]
    [InlineData("let x = {\"a\": 1\n    -2}", ErrorCodes.Parse0030, 1, 16)]
    [InlineData("let x = [f(y)\n    (z)]", ErrorCodes.Parse0030, 1, 14)]
    [InlineData("let x = [\"a\"\n    -1]", ErrorCodes.Parse0030, 1, 13)]
    [InlineData("let x = [f()?\n    -1]", ErrorCodes.Parse0030, 1, 14)]
    [InlineData("let x = [1, 2\n    [3]]", ErrorCodes.Parse0030, 1, 14)]
    [InlineData("let x = [true\n    -1]", ErrorCodes.Parse0030, 1, 14)]
    public void Syntax_errors_with_their_exact_position(string source, string code, int line, int column) =>
        Parsing.AssertError(source, code, line, column);

    [Theory]
    [InlineData("let x = [\n    1,\n    -2\n]", "(let x (list 1 (neg 2)))")]
    [InlineData("let x = f(\n    -1\n)", "(let x (call f (neg 1)))")]
    [InlineData("let x = [1 +\n    -2]", "(let x (list (+ 1 (neg 2))))")]
    [InlineData("let x = [a\n    .b]", "(let x (list (. a b)))")]
    [InlineData("let x = [a\n    .f()]", "(let x (list (call (. a f))))")]
    [InlineData("let x = [1,\n    [2]]", "(let x (list 1 (list 2)))")]
    public void Line_breaks_that_are_not_ambiguous_are_accepted(string source, string expected) =>
        Assert.Equal(expected, Parsing.Print(source));

    [Theory]
    [InlineData("let x = 1 | 2", ErrorCodes.Parse0031, 1, 11)]
    [InlineData("f(1 | 2)", ErrorCodes.Parse0031, 1, 5)]
    [InlineData("io.write_standard_output_line(\"a\");; io.write_standard_output_line(\"b\")", ErrorCodes.Parse0032, 1, 35)]
    [InlineData(";;", ErrorCodes.Parse0033, 1, 1)]
    [InlineData("io.write_standard_output_line(\"a\")\n;", ErrorCodes.Parse0033, 2, 1)]
    [InlineData(";", ErrorCodes.Parse0033, 1, 1)]
    [InlineData("f(); ;", ErrorCodes.Parse0032, 1, 4)]
    [InlineData("if a { ; }", ErrorCodes.Parse0033, 1, 8)]
    [InlineData("let c = 1 < 2 < 3", ErrorCodes.Parse0034, 1, 15)]
    [InlineData("let c = a == b == c", ErrorCodes.Parse0034, 1, 16)]
    [InlineData("let c = a < b <= c", ErrorCodes.Parse0034, 1, 15)]
    [InlineData("let c = 1 < 2 == true", ErrorCodes.Parse0034, 1, 15)]
    [InlineData("let c = true and false or true", ErrorCodes.Parse0035, 1, 24)]
    [InlineData("let c = a or b and c", ErrorCodes.Parse0035, 1, 16)]
    [InlineData("let c = a and b or c and d", ErrorCodes.Parse0035, 1, 17)]
    [InlineData("let c = a or not b and c", ErrorCodes.Parse0035, 1, 20)]
    [InlineData("let c = a or b == c and d", ErrorCodes.Parse0035, 1, 21)]
    [InlineData("let c = a or b and )", ErrorCodes.Parse0035, 1, 16)]
    public void Operator_ambiguities_are_rejected(string source, string code, int line, int column) =>
        Parsing.AssertError(source, code, line, column);

    [Fact]
    public void Module_declarations_without_visibility_default_to_private()
    {
        var program = Parsing.Parse("let x = 1\nfunction f() {\n}", isModule: true);

        Assert.Equal(Visibility.Private, Assert.IsType<LetStatement>(program.Statements[0]).Visibility);
        Assert.Equal(Visibility.Private, Assert.IsType<FunctionDeclaration>(program.Statements[1]).Visibility);
        Parsing.AssertError("private let x = 1", ErrorCodes.Parse0049, 1, 1, isModule: true);
    }

    [Theory]
    [InlineData("1 + 2", 1, 1)]
    [InlineData("x", 1, 1)]
    [InlineData("f().g", 1, 1)]
    [InlineData("[1]", 1, 1)]
    [InlineData("\"a\"", 1, 1)]
    [InlineData("x => x", 1, 1)]
    [InlineData("f()??", 1, 1)]
    [InlineData("a.b", 1, 1)]
    [InlineData("not f()", 1, 1)]
    [InlineData("{\"a\": 1}", 1, 1)]
    [InlineData("f()\n1 + 2", 2, 1)]
    [InlineData("if a {\n    x\n}", 2, 5)]
    [InlineData("-f()", 1, 1)]
    [InlineData("ok(1)", 1, 1)]
    public void Only_calls_and_matches_can_be_statements(string source, int line, int column) =>
        Parsing.AssertError(source, ErrorCodes.Parse0039, line, column);

    [Theory]
    [InlineData("(f())")]
    [InlineData("f()?")]
    [InlineData("(f(x))?")]
    [InlineData("a.b.c()")]
    [InlineData("f()(1)")]
    [InlineData("f()[0]()")]
    public void Valid_expression_statements(string source) => Assert.Single(Parsing.Parse(source).Statements);

    [Theory]
    [InlineData("1 = 2", 1, 1)]
    [InlineData("f() = 1", 1, 1)]
    [InlineData("a.b = 1", 1, 1)]
    [InlineData("(x) = 1", 1, 1)]
    [InlineData("\"a\" += 1", 1, 1)]
    [InlineData("xs[0].y = 1", 1, 1)]
    [InlineData("f(x)[0] = 1", 1, 1)]
    [InlineData("alias.x = 1", 1, 1)]
    [InlineData("if a {\n    1 = 2\n}", 2, 5)]
    public void Invalid_assignment_targets(string source, int line, int column) =>
        Parsing.AssertError(source, ErrorCodes.Parse0040, line, column);

    [Theory]
    [InlineData("let x", ErrorCodes.Parse0041, 1, 6)]
    [InlineData("let x\n", ErrorCodes.Parse0041, 1, 6)]
    [InlineData("let x: integer", ErrorCodes.Parse0041, 1, 15)]
    [InlineData("let x;", ErrorCodes.Parse0041, 1, 6)]
    [InlineData("if a {\n    let x\n}", ErrorCodes.Parse0041, 2, 10)]
    [InlineData("if a { let x }", ErrorCodes.Parse0041, 1, 14)]
    [InlineData("function f(a) {\n}", ErrorCodes.Parse0042, 1, 12)]
    [InlineData("function f(a, b: integer) {\n}", ErrorCodes.Parse0042, 1, 12)]
    [InlineData("function f(a: integer, b) {\n}", ErrorCodes.Parse0042, 1, 24)]
    [InlineData("let g: function(integer) = 1", ErrorCodes.Parse0043, 1, 26)]
    [InlineData("function f(g: function(integer)) {\n}", ErrorCodes.Parse0043, 1, 32)]
    [InlineData("let g: function() ", ErrorCodes.Parse0043, 1, 19)]
    [InlineData("let ys = [1].transform(n: integer => n)", ErrorCodes.Parse0044, 1, 25)]
    [InlineData("f(x: integer => x)", ErrorCodes.Parse0044, 1, 4)]
    [InlineData("f(x: integer)", ErrorCodes.Parse0023, 1, 4)]
    [InlineData("f(x: )", ErrorCodes.Parse0023, 1, 4)]
    public void Declaration_forms(string source, string code, int line, int column) => Parsing.AssertError(source, code, line, column);

    [Theory]
    [InlineData("match 1 {\n    1.5 => f()\n    _ => g()\n}", ErrorCodes.Parse0045, 2, 5)]
    [InlineData("match 1 {\n    -1.5 => f()\n    _ => g()\n}", ErrorCodes.Parse0045, 2, 5)]
    [InlineData("match s {\n    \"a{x}\" => f()\n    _ => g()\n}", ErrorCodes.Parse0045, 2, 5)]
    [InlineData("match s {\n    r\"a\" => f()\n    _ => g()\n}", ErrorCodes.Parse0045, 2, 5)]
    [InlineData("match s {\n    r\"\"\"a\"\"\" => f()\n    _ => g()\n}", ErrorCodes.Parse0045, 2, 5)]
    [InlineData("match s {\n    p\"a\" => f()\n    _ => g()\n}", ErrorCodes.Parse0045, 2, 5)]
    [InlineData("match r {\n    ok(1) => f()\n    _ => g()\n}", ErrorCodes.Parse0045, 2, 8)]
    [InlineData("match r {\n    error(\"x\") => f()\n    _ => g()\n}", ErrorCodes.Parse0045, 2, 11)]
    [InlineData("match r {\n    ok(n) | error(n) => f()\n}", ErrorCodes.Parse0046, 2, 11)]
    [InlineData("match r {\n    1 | x => f()\n}", ErrorCodes.Parse0046, 2, 7)]
    [InlineData("match r {\n    x | 1 => f()\n}", ErrorCodes.Parse0046, 2, 7)]
    [InlineData("match r {\n    1 | ok(x) => f()\n}", ErrorCodes.Parse0046, 2, 7)]
    [InlineData("match 1 {\n}", ErrorCodes.Parse0047, 2, 1)]
    [InlineData("match 1 {\n\n\n}", ErrorCodes.Parse0047, 4, 1)]
    [InlineData("match x {}", ErrorCodes.Parse0047, 1, 10)]
    [InlineData("match 1 {\n    _ => f()\n}.length()", ErrorCodes.Parse0048, 3, 2)]
    [InlineData("match 1 {\n    _ => f()\n}?", ErrorCodes.Parse0048, 3, 2)]
    [InlineData("match 1 {\n    _ => f()\n}[0]", ErrorCodes.Parse0048, 3, 2)]
    [InlineData("match 1 {\n    _ => f()\n}(1)", ErrorCodes.Parse0048, 3, 2)]
    [InlineData("match 1 {\n    _ => f()\n}\n.length()", ErrorCodes.Parse0048, 4, 1)]
    [InlineData("match x {\n    [1] => f()\n}", ErrorCodes.Parse0023, 2, 5)]
    [InlineData("match x {\n    1 f()\n}", ErrorCodes.Parse0023, 2, 7)]
    public void Match_pattern_rejections(string source, string code, int line, int column) => Parsing.AssertError(source, code, line, column);

    [Theory]
    [InlineData("public let x = 1", false, ErrorCodes.Parse0049, 1, 1)]
    [InlineData("private function f() {\n}", false, ErrorCodes.Parse0049, 1, 1)]
    [InlineData("function f() {\n    private let y = 1\n}", false, ErrorCodes.Parse0049, 2, 5)]
    [InlineData("public function f() {\n    public let x = 1\n}", true, ErrorCodes.Parse0049, 2, 5)]
    [InlineData("public print(\"x\")", true, ErrorCodes.Parse0049, 1, 1)]
    [InlineData("public if a {\n}", true, ErrorCodes.Parse0049, 1, 1)]
    [InlineData("io.write_standard_output_line(\"a\")\nimport \"lib.scs\" as lib", false, ErrorCodes.Parse0051, 2, 1)]
    [InlineData("if a {\n    import \"x.scs\" as x\n}", false, ErrorCodes.Parse0051, 2, 5)]
    [InlineData("import \"a.scs\" as a\nlet x = 1\nimport \"b.scs\" as b", false, ErrorCodes.Parse0051, 3, 1)]
    [InlineData("import \"a.scs\" as a\nio.write_standard_output_line(\"x\")\nimport \"b.scs\" as b", true, ErrorCodes.Parse0051, 3, 1)]
    [InlineData("import p\"lib.scs\" as lib", false, ErrorCodes.Parse0052, 1, 8)]
    [InlineData("import r\"lib.scs\" as lib", false, ErrorCodes.Parse0052, 1, 8)]
    [InlineData("import \"a{x}.scs\" as lib", false, ErrorCodes.Parse0052, 1, 8)]
    [InlineData("import x as lib", false, ErrorCodes.Parse0052, 1, 8)]
    [InlineData("import as lib", false, ErrorCodes.Parse0052, 1, 8)]
    [InlineData("import\n\"a.scs\" as a", false, ErrorCodes.Parse0052, 1, 7)]
    public void Module_and_import_rules(string source, bool isModule, string code, int line, int column) =>
        Parsing.AssertError(source, code, line, column, isModule);

    [Theory]
    [InlineData("io.write_standard_output_line(\"abc)", ErrorCodes.Parse0016, 1, 31)]
    [InlineData("io.write_standard_output_line(\"a\\qb\")", ErrorCodes.Parse0017, 1, 33)]
    [InlineData("io.write_standard_output_line(\"\\u{110000}\")", ErrorCodes.Parse0018, 1, 32)]
    [InlineData("io.write_standard_output_line(\"a{}b\")", ErrorCodes.Parse0019, 1, 33)]
    [InlineData("io.write_standard_output_line(\"a{x")]
    [InlineData("io.write_standard_output_line(\"a{{x", ErrorCodes.Parse0020, 1, 33)]
    [InlineData("io.write_standard_output_line(\"a}b\")", ErrorCodes.Parse0021, 1, 33)]
    [InlineData("io.write_standard_output_line(\"{0}\")", ErrorCodes.Parse0022, 1, 32)]
    [InlineData("io.write_standard_output_line(\"{true}\")", ErrorCodes.Parse0022, 1, 32)]
    [InlineData("io.write_standard_output_line(\"{-1}\")", ErrorCodes.Parse0022, 1, 32)]
    [InlineData("io.write_standard_output_line(\"{- 1.5}\")", ErrorCodes.Parse0022, 1, 32)]
    [InlineData("io.write_standard_output_line(\"{1.5}\")", ErrorCodes.Parse0022, 1, 32)]
    [InlineData("io.write_standard_output_line(\"{\"x\"}\")", ErrorCodes.Parse0022, 1, 32)]
    [InlineData("io.write_standard_output_line(\"{r\"x\"}\")", ErrorCodes.Parse0022, 1, 32)]
    [InlineData("io.write_standard_output_line(\"a {false} b\")", ErrorCodes.Parse0022, 1, 34)]
    [InlineData("let s = \"{1 +}\"", ErrorCodes.Parse0023, 1, 14)]
    [InlineData("let s = \"{a b}\"", ErrorCodes.Parse0023, 1, 13)]
    [InlineData("let s = \"{f(}\"", ErrorCodes.Parse0024, 1, 12)]
    [InlineData("let s = \"{1 +} \\q\"", ErrorCodes.Parse0023, 1, 14)]
    [InlineData("let s = \"{a} {}\"", ErrorCodes.Parse0019, 1, 14)]
    [InlineData("let s = \"a{1 @ 2}\"", ErrorCodes.Parse0006, 1, 14)]
    [InlineData("let s = \"{x}\" @", ErrorCodes.Parse0006, 1, 15)]
    public void String_errors_reach_the_parser_with_the_right_position(string source, string code = ErrorCodes.Parse0020, int line = 1, int column = 33) =>
        Parsing.AssertError(source, code, line, column);

    [Theory]
    [InlineData("let x = 007", ErrorCodes.Parse0011, 1, 9)]
    [InlineData("let x = 0XFF", ErrorCodes.Parse0012, 1, 9)]
    [InlineData("let x = 1e999", ErrorCodes.Parse0015, 1, 9)]
    [InlineData("let c = a -b", ErrorCodes.Parse0009, 1, 11)]
    [InlineData("let c = a -(b)", ErrorCodes.Parse0009, 1, 11)]
    [InlineData("let c = [1 -2]", ErrorCodes.Parse0009, 1, 12)]
    [InlineData("f(a -b)", ErrorCodes.Parse0009, 1, 5)]
    [InlineData("let c = a -1", ErrorCodes.Parse0009, 1, 11)]
    [InlineData("let c = a --b", ErrorCodes.Parse0009, 1, 11)]
    [InlineData("let both = true && false", ErrorCodes.Parse0008, 1, 17)]
    [InlineData("let y = i--", ErrorCodes.Parse0008, 1, 10)]
    [InlineData("let y = x++", ErrorCodes.Parse0008, 1, 10)]
    [InlineData("f(i--)", ErrorCodes.Parse0008, 1, 4)]
    [InlineData("let x = !a", ErrorCodes.Parse0008, 1, 9)]
    [InlineData("let x = a || b", ErrorCodes.Parse0008, 1, 11)]
    [InlineData("let x = a & b", ErrorCodes.Parse0008, 1, 11)]
    [InlineData("let x = 1 @ 2", ErrorCodes.Parse0006, 1, 11)]
    [InlineData("let café = 1", ErrorCodes.Parse0007, 1, 8)]
    [InlineData("io.write_standard_output_line(\"a\")\rio.write_standard_output_line(\"b\")", ErrorCodes.Parse0003, 1, 35)]
    [InlineData("\tlet x = @", ErrorCodes.Parse0006, 1, 10)]
    [InlineData("let x = \"😀\" @", ErrorCodes.Parse0006, 1, 13)]
    public void Lexical_errors_reach_the_parser_with_the_right_position(string source, string code, int line, int column) =>
        Parsing.AssertError(source, code, line, column);

    [Theory]
    [InlineData("i--")]
    [InlineData("x++")]
    public void A_decrement_statement_is_an_invalid_statement_before_it_is_a_forbidden_operator(string source)
    {
        // The operator is only seen while looking ahead after the operand, so the statement error (smaller position) is reported first.
        Parsing.AssertError(source, ErrorCodes.Parse0039, 1, 1);
    }

    [Theory]
    [InlineData("let a = b -c")]
    [InlineData("let a = b - c")]
    [InlineData("let a = b -  c")]
    [InlineData("let a = b-c")]
    [InlineData("let a = b - -c")]
    [InlineData("let a = [b, -c]")]
    [InlineData("let a = f(b, -c)")]
    [InlineData("let a = 5--3")]
    public void Minus_spacing_rule_only_rejects_blank_before_and_none_after_in_binary_position(string source)
    {
        var error = Parser.Parse(Parsing.Source(source), isModule: false).Error;
        var shouldFail = source == "let a = b -c";
        Assert.Equal(shouldFail ? ErrorCodes.Parse0009 : null, error?.Code);
    }

    [Fact]
    public void The_ambiguous_minus_message_shows_both_operands()
    {
        var error = Parsing.Fail("let c = a -b");
        Assert.Contains("a -b", error.Message, StringComparison.Ordinal);
        Assert.Contains("a - b", error.Suggestion, StringComparison.Ordinal);
        Assert.Contains("a, -b", error.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Unclosed_delimiter_message_names_the_delimiter_and_its_line()
    {
        var error = Parsing.Fail("let x = 1\nif true {\n    f()\n");
        Assert.Equal(ErrorCodes.Parse0024, error.Code);
        Assert.Contains("`{`", error.Message, StringComparison.Ordinal);
        Assert.Contains("line 2", error.Message, StringComparison.Ordinal);
        Assert.Contains("`}`", error.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Unexpected_token_message_names_what_was_found_and_expected()
    {
        var error = Parsing.Fail("let x = )");
        Assert.Contains("`)`", error.Message, StringComparison.Ordinal);
        Assert.Contains("an expression", error.Message, StringComparison.Ordinal);
        Assert.Contains("end of line", Parsing.Fail("if true\nf()").Message, StringComparison.Ordinal);
        Assert.Contains("end of file", Parsing.Fail("let x =").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pipeline_operator_is_rejected_in_expressions()
    {
        Assert.Equal(ErrorCodes.Parse0055, Parsing.Fail("let x = a |> f()").Code);
        Assert.Equal(ErrorCodes.Parse0055, Parsing.Fail("match x { _ => 1 } |> f()").Code);
    }

    [Fact]
    public void A_wildcard_lambda_written_with_an_arrow_is_0056()
    {
        Assert.Equal(ErrorCodes.Parse0056, Parsing.Fail("let f = _ -> 1").Code);
    }

    [Fact]
    public void An_import_rejects_the_pipeline_operator_with_0055()
    {
        Assert.Equal(ErrorCodes.Parse0055, Parsing.Fail("import |> \"m\"").Code);
    }

    [Fact]
    public void A_bare_lambda_head_may_break_before_its_arrow_inside_a_delimiter()
    {
        var program = Parsing.Parse("function apply(f: function(integer) -> integer) -> integer { return f(1) }\nlet n = apply(x\n=> x + 1)");
        Assert.Equal(2, program.Statements.Count);
    }

    [Fact]
    public void The_pipeline_operator_is_rejected_in_statements_patterns_and_comparisons()
    {
        var statement = Parsing.Fail("x |> f()");
        Assert.Equal(ErrorCodes.Parse0055, statement.Code);
        Assert.Contains("there is no pipeline operator", statement.Message);
        Assert.Equal(ErrorCodes.Parse0055, Parsing.Fail("match 1 {\n    1 |> 2 => 0\n    _ => 0\n}").Code);
        Assert.Equal(ErrorCodes.Parse0055, Parsing.Fail("let y = x |> f() == 1").Code);
    }

    [Fact]
    public void A_line_break_after_a_match_arm_arrow_is_rejected()
    {
        var error = Parsing.Fail("match x {\n    _ =>\n    1\n}");
        Assert.Equal(ErrorCodes.Parse0054, error.Code);
        Assert.Equal((2, 9), error.Start);
    }

    [Fact]
    public void A_line_break_after_an_arm_arrow_cannot_swallow_the_next_arm()
    {
        // The hazard the rule exists for: without it, `2, _ => 0` would read as the lambda body of `1 =>`.
        var error = Parsing.Fail("match 1 {\n    1 =>\n    2, _ => 0\n}");
        Assert.Equal(ErrorCodes.Parse0054, error.Code);
        Assert.Equal((2, 9), error.Start);
    }

    [Fact]
    public void A_brace_in_a_path_literal_is_rejected()
    {
        var error = Parsing.Fail("let p = p\"a{b}\"");
        Assert.Equal(ErrorCodes.Parse0053, error.Code);
        Assert.Equal((1, 12), error.Start);
    }

    [Theory]
    // L6: `{{` is not an escape; when the interpolation it opens does not parse, PARSE-0059 replaces its error.
    [InlineData("io.write_standard_output_line(\"valeur {{x}}\")", 1, 39)]
    [InlineData("let s = \"a {{ b\"", 1, 12)]
    [InlineData("let s = \"{{\"", 1, 10)]
    [InlineData("let s = \"{x} {{y}}\"", 1, 14)]
    public void A_double_brace_that_does_not_parse_is_PARSE_0059(string source, int line, int column)
    {
        var error = Parsing.Fail(source);
        Assert.Equal(ErrorCodes.Parse0059, error.Code);
        Assert.Equal((line, column), error.Start);
        Assert.Equal((line, column + 2), error.End);
    }

    [Theory]
    [InlineData("let n = \"{{\"a\": 1}.length()}\"")]
    [InlineData("let n = \"{ {\"a\": 1}.length() }\"")]
    public void A_double_brace_opening_a_valid_map_interpolation_is_unchanged(string source) =>
        Parsing.Parse(source);

    [Theory]
    [InlineData("let s = \"valeur {{x}}\"", "\"valeur \\{x\\}\"")]
    [InlineData("let s = \"\\u{7B}{{x}} \\{\"", "\"\\u{7B}\\{x\\} \\{\"")]
    public void A_double_brace_error_carries_a_fix_scoped_to_the_string(string source, string replacement)
    {
        var fix = Parsing.Fail(source).Fix;
        Assert.NotNull(fix);
        Assert.Equal(replacement, fix.Replacement);
        Assert.Equal(8, fix.Span.Start);
        Assert.Equal(source.Length, fix.Span.End);
    }

    [Fact]
    public void A_double_brace_with_a_lexical_error_inside_has_no_fix()
    {
        var error = Parsing.Fail("let s = \"{{x \\q}}\"");
        Assert.Equal(ErrorCodes.Parse0006, error.Code);
        Assert.Null(error.Fix);
    }

    [Fact]
    public void A_double_brace_in_a_path_literal_keeps_PARSE_0053()
    {
        Assert.Equal(ErrorCodes.Parse0053, Parsing.Fail("let p = p\"a{{b}}\"").Code);
    }

    [Theory]
    [InlineData("let s = \"{{a: 1}.length()} {{y}}\"", "\"{{a: 1}.length()} \\{y\\}\"")]
    [InlineData("let s = \"{{y}} {{a: 1}.length()}\"", "\"\\{y\\} {{a: 1}.length()}\"")]
    [InlineData("let s = \"{{a}} {{b}}\"", "\"\\{a\\} {{b}}\"")]
    [InlineData("let s = \"{{{x}}}\"", "\"\\{\\{x\\}\\}\"")]
    [InlineData("let s = \"{{{a} x}}\"", "\"\\{\\{a\\} x\\}\"")]
    [InlineData("let s = \"{{{cleanup()} x}}\"", "\"\\{\\{cleanup()\\} x\\}\"")]
    [InlineData("let s = \"{{\"", "\"\\{\"")]
    public void A_double_brace_fix_escapes_only_the_failing_segment(string source, string replacement)
    {
        var fix = Parsing.Fail(source).Fix;
        Assert.NotNull(fix);
        Assert.Equal(replacement, fix.Replacement);
    }

    [Fact]
    public void Path_is_a_declarable_identifier()
    {
        Assert.Equal("(let path (path \"/tmp\"))", Parsing.Print("let path = p\"/tmp\""));
    }

    [Fact]
    public void Construct_name_is_given_for_line_break_before_a_brace()
    {
        Assert.Contains("`while`", Parsing.Fail("while c\n{\n}").Message, StringComparison.Ordinal);
        Assert.Contains("`else`", Parsing.Fail("if a {\n} else\n{\n}").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Declaration_messages_carry_names_and_suggestions()
    {
        Assert.Contains("`x`", Parsing.Fail("let x: integer").Message, StringComparison.Ordinal);
        Assert.Contains(": integer", Parsing.Fail("let x: integer").Suggestion, StringComparison.Ordinal);
        Assert.Contains("`a`", Parsing.Fail("function f(a) {\n}").Message, StringComparison.Ordinal);
        Assert.Contains("(n: integer)", Parsing.Fail("f(n: integer => n)").Suggestion, StringComparison.Ordinal);
        Assert.Contains("`public`", Parsing.Fail("public let x = 1").Message, StringComparison.Ordinal);
        Assert.Contains("`while`", Parsing.Fail("let while = 1").Message, StringComparison.Ordinal);
        Assert.Contains("`async`", Parsing.Fail("let async = 1").Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("let x: integer @")]
    [InlineData("let x = 1 @")]
    public void A_deferred_lexical_error_is_reported_when_nothing_earlier_fails(string source)
    {
        Assert.Equal(ErrorCodes.Parse0006, Parsing.Fail(source).Code);
    }

    [Theory]
    [InlineData("f(1 2 @)", ErrorCodes.Parse0023, 1, 5)]
    [InlineData("(a, b @", ErrorCodes.Parse0023, 1, 3)]
    [InlineData("let a b @", ErrorCodes.Parse0023, 1, 7)]
    [InlineData("let x = (1 + 2 3 @", ErrorCodes.Parse0023, 1, 16)]
    [InlineData("1 + 2 @", ErrorCodes.Parse0039, 1, 1)]
    [InlineData("let c = [1\n    -2 @]", ErrorCodes.Parse0030, 1, 11)]
    public void The_first_error_by_position_wins_over_a_later_lexical_error(string source, string code, int line, int column) =>
        Parsing.AssertError(source, code, line, column);

    [Theory]
    // S6: a diagnostic at end of file covers one code point, so `end_column` is `column + 1`.
    [InlineData("let x =", 1, 8, 1, 9)]
    [InlineData("let x", 1, 6, 1, 7)]
    [InlineData("let x =\n", 2, 1, 2, 2)]
    public void An_end_of_file_diagnostic_covers_one_code_point(string source, int line, int column, int endLine, int endColumn)
    {
        var error = Parsing.Fail(source);
        Assert.Equal((line, column), error.Start);
        Assert.Equal((endLine, endColumn), error.End);
        Assert.Equal(column + 1, error.End.Column);
    }

    [Fact]
    public void An_end_of_file_diagnostic_after_a_supplementary_code_point_still_covers_one_point()
    {
        // The last line ends with an astral character: `let x = "😀" +` puts the file cursor right after the emoji.
        var error = Parsing.Fail("let x = \"😀\" +");
        Assert.Equal((1, 14), error.Start);
        Assert.Equal((1, 15), error.End);
    }

    [Fact]
    public void A_bare_interpolation_literal_message_points_to_the_raw_string_fix()
    {
        // Improvement: explain it is a bare literal and point at `r"…"`.
        var error = Parsing.Fail("io.write_standard_output_line(\"{0}\")");
        Assert.Equal(ErrorCodes.Parse0022, error.Code);
        Assert.Contains("bare literal", error.Message, StringComparison.Ordinal);
        Assert.Contains("\"{0}\"", error.Message, StringComparison.Ordinal);
        Assert.Contains("`string.format_template` template", error.Message, StringComparison.Ordinal);
        Assert.Contains("raw string", error.Suggestion, StringComparison.Ordinal);
        Assert.Contains("r\"{0}\"", error.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wildcard_parameter_is_a_valid_name_but_not_readable()
    {
        // S20: `_` is accepted as a parameter of a function and of a lambda.
        Parsing.Parse("function f(_: integer, _: integer) -> integer {\n    return 1\n}");
        Parsing.Parse("let g = (x: integer) => x");
        // ... but reading it is still PARSE-0027.
        Assert.Equal(ErrorCodes.Parse0027, Parsing.Fail("function f(_: integer) -> integer {\n    return _\n}").Code);
    }
}
