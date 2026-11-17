namespace Scripts.Checker.Tests;

public sealed class OperatorTests
{
    [Theory]
    // arithmetic
    [InlineData("1", "+", "2", "integer")]
    [InlineData("1.5", "+", "2.5", "float")]
    [InlineData("\"a\"", "+", "\"b\"", "string")]
    [InlineData("[1]", "+", "[2]", "list<integer>")]
    [InlineData("p\"a\"", "+", "p\"b\"", "SCRIPTS-TYPE-0015")]
    [InlineData("true", "+", "true", "SCRIPTS-TYPE-0015")]
    [InlineData("true", "+", "1", "SCRIPTS-TYPE-0015")]
    [InlineData("{\"a\": 1}", "+", "{\"a\": 1}", "SCRIPTS-TYPE-0015")]
    [InlineData("1", "+", "1.5", "SCRIPTS-TYPE-0016")]
    [InlineData("1.5", "+", "2", "SCRIPTS-TYPE-0016")]
    [InlineData("\"a\"", "+", "1", "SCRIPTS-TYPE-0016")]
    [InlineData("1", "+", "\"a\"", "SCRIPTS-TYPE-0016")]
    [InlineData("[1]", "+", "[\"a\"]", "SCRIPTS-TYPE-0016")]
    [InlineData("1", "-", "2", "integer")]
    [InlineData("1.5", "-", "2.5", "float")]
    [InlineData("\"a\"", "-", "\"b\"", "SCRIPTS-TYPE-0015")]
    [InlineData("1", "-", "1.5", "SCRIPTS-TYPE-0016")]
    [InlineData("2", "*", "3", "integer")]
    [InlineData("2.5", "*", "2.0", "float")]
    [InlineData("[1]", "*", "[1]", "SCRIPTS-TYPE-0015")]
    [InlineData("1", "*", "1.5", "SCRIPTS-TYPE-0016")]
    [InlineData("4", "/", "2", "integer")]
    [InlineData("4.0", "/", "2.0", "float")]
    [InlineData("p\"a\"", "/", "\"b\"", "path")]
    [InlineData("p\"a\"", "/", "p\"b\"", "path")]
    [InlineData("p\"a\"", "/", "1", "SCRIPTS-TYPE-0016")]
    [InlineData("\"a\"", "/", "\"b\"", "SCRIPTS-TYPE-0015")]
    [InlineData("1", "/", "1.5", "SCRIPTS-TYPE-0016")]
    [InlineData("5", "%", "2", "integer")]
    [InlineData("5.0", "%", "2.0", "SCRIPTS-TYPE-0015")]
    [InlineData("5", "%", "2.0", "SCRIPTS-TYPE-0016")]
    [InlineData("\"a\"", "%", "1", "SCRIPTS-TYPE-0015")]
    // equality
    [InlineData("1", "==", "1", "boolean")]
    [InlineData("\"a\"", "!=", "\"b\"", "boolean")]
    [InlineData("1.5", "==", "2.5", "boolean")]
    [InlineData("true", "==", "false", "boolean")]
    [InlineData("p\"a\"", "==", "p\"a\"", "boolean")]
    [InlineData("[1]", "==", "[2]", "boolean")]
    [InlineData("{\"a\": 1}", "==", "{\"a\": 2}", "boolean")]
    [InlineData("[[1]]", "!=", "[[2]]", "boolean")]
    [InlineData("1", "==", "\"a\"", "SCRIPTS-TYPE-0016")]
    [InlineData("1", "==", "1.5", "SCRIPTS-TYPE-0016")]
    [InlineData("[1]", "==", "[\"a\"]", "SCRIPTS-TYPE-0016")]
    [InlineData("p\"a\"", "==", "\"a\"", "SCRIPTS-TYPE-0016")]
    [InlineData("\"1\".parse_integer()", "==", "\"2\".parse_integer()", "SCRIPTS-TYPE-0018")]
    [InlineData("1", "!=", "\"1\".parse_integer()", "SCRIPTS-TYPE-0018")]
    // ordering
    [InlineData("1", "<", "2", "boolean")]
    [InlineData("1.5", ">=", "2.5", "boolean")]
    [InlineData("\"a\"", "<=", "\"b\"", "boolean")]
    [InlineData("p\"a\"", ">", "p\"b\"", "boolean")]
    [InlineData("true", "<", "false", "SCRIPTS-TYPE-0019")]
    [InlineData("[1]", "<", "[2]", "SCRIPTS-TYPE-0019")]
    [InlineData("1", "<", "1.5", "SCRIPTS-TYPE-0016")]
    [InlineData("1", ">", "\"a\"", "SCRIPTS-TYPE-0016")]
    [InlineData("\"1\".parse_integer()", "<", "1", "SCRIPTS-TYPE-0019")]
    // logic
    [InlineData("true", "and", "false", "boolean")]
    [InlineData("true", "or", "false", "boolean")]
    [InlineData("1", "and", "true", "SCRIPTS-TYPE-0015")]
    [InlineData("true", "and", "1", "SCRIPTS-TYPE-0016")]
    [InlineData("\"a\"", "or", "\"b\"", "SCRIPTS-TYPE-0015")]
    public void Binary_operator_matrix(string left, string binaryOperator, string right, string expected)
    {
        if (expected.StartsWith("SCRIPTS-", StringComparison.Ordinal))
        {
            Checking.AssertMarked($"let _v = {left} {Checking.Mark}{binaryOperator} {right}", expected);
        }
        else
        {
            Assert.Equal(expected, Checking.TypeOfLastLet($"let _v = {left} {binaryOperator} {right}").ToString());
        }
    }

    [Theory]
    [InlineData("-", "1", "integer")]
    [InlineData("-", "1.5", "float")]
    [InlineData("-", "\"a\"", "SCRIPTS-TYPE-0017")]
    [InlineData("-", "true", "SCRIPTS-TYPE-0017")]
    [InlineData("-", "[1]", "SCRIPTS-TYPE-0017")]
    [InlineData("not", "true", "boolean")]
    [InlineData("not", "1", "SCRIPTS-TYPE-0017")]
    [InlineData("not", "\"a\"", "SCRIPTS-TYPE-0017")]
    public void Unary_operator_matrix(string unaryOperator, string operand, string expected)
    {
        var separator = unaryOperator == "not" ? " " : string.Empty;
        if (expected.StartsWith("SCRIPTS-", StringComparison.Ordinal))
        {
            Checking.AssertMarked($"let _v = {Checking.Mark}{unaryOperator}{separator}{operand}", expected);
        }
        else
        {
            Assert.Equal(expected, Checking.TypeOfLastLet($"let _v = {unaryOperator}{separator}{operand}").ToString());
        }
    }

    [Theory]
    [InlineData("1", "+=", "1", true)]
    [InlineData("1", "-=", "1", true)]
    [InlineData("1", "*=", "2", true)]
    [InlineData("1", "/=", "2", true)]
    [InlineData("1", "%=", "2", true)]
    [InlineData("1", "+=", "1.5", false)]
    [InlineData("1", "-=", "\"a\"", false)]
    [InlineData("1.5", "*=", "2", false)]
    [InlineData("1.5", "%=", "2.0", false)]
    [InlineData("\"a\"", "+=", "\"b\"", true)]
    [InlineData("\"a\"", "-=", "\"b\"", false)]
    [InlineData("\"a\"", "+=", "1", false)]
    [InlineData("p\"a\"", "/=", "\"b\"", true)]
    [InlineData("p\"a\"", "/=", "p\"b\"", true)]
    [InlineData("p\"a\"", "+=", "p\"b\"", false)]
    [InlineData("[1]", "+=", "[2]", true)]
    [InlineData("[1]", "+=", "[]", true)]
    [InlineData("[1]", "+=", "[\"a\"]", false)]
    [InlineData("true", "+=", "true", false)]
    public void Compound_assignment_requires_a_defined_operation_of_the_same_type(string initial, string assignOperator, string value, bool isValid)
    {
        var program = $"let mutable value = {initial}\nvalue {(isValid ? string.Empty : Checking.Mark.ToString())}{assignOperator} {value}";
        if (isValid)
        {
            Checking.AssertClean(program);
        }
        else
        {
            Checking.AssertMarked(program, "SCRIPTS-TYPE-0021");
        }
    }

    [Fact]
    public void Equality_on_records_and_functions_is_forbidden_at_any_depth()
    {
        Checking.AssertMarked("let _v = process.run(process.request(\"true\", []))? ‸== process.run(process.request(\"true\", []))?", "SCRIPTS-TYPE-0018");
        Checking.AssertMarked("let f = (x: integer) => x\nlet _v = f ‸== f", "SCRIPTS-TYPE-0018");
        Checking.AssertMarked("let xs: list<result<integer, string>> = []\nlet _v = xs ‸== xs", "SCRIPTS-TYPE-0018");
        Checking.AssertMarked("let m: map<string, list<result<integer, string>>> = {}\nlet _v = m ‸!= m", "SCRIPTS-TYPE-0018");
        Checking.AssertMarked("let xs: list<result<integer, string>> = []\nlet found = xs.‸contains(ok(1))", "SCRIPTS-TYPE-0057");
    }

    [Fact]
    public void Path_division_is_typed_path()
    {
        Assert.Equal("path", Checking.TypeOfLastLet("let base_path = p\"/tmp\"\nlet _v = base_path / \"a\" / p\"b\"").ToString());
    }

    [Fact]
    public void Conditions_must_be_boolean()
    {
        Checking.AssertMarked("if ‸1 {\n}", "SCRIPTS-TYPE-0020");
        Checking.AssertMarked("let s = \"a\"\nwhile ‸s {\n}", "SCRIPTS-TYPE-0020");
        Checking.AssertMarked("if true {\n} else if ‸\"x\" {\n}", "SCRIPTS-TYPE-0020");
        Checking.AssertClean("let n = 1\nif n > 0 and not (n == 3) {\n}");
    }

    [Fact]
    public void Operands_with_their_own_errors_do_not_cascade()
    {
        Checking.AssertMarked("let a = ‸missing + 1", "SCRIPTS-TYPE-0001");
        Checking.AssertMarked("let a = ‸missing == ‸other", "SCRIPTS-TYPE-0001", "SCRIPTS-TYPE-0001");
        Checking.AssertMarked("if ‸missing {\n}", "SCRIPTS-TYPE-0001");
    }
}
