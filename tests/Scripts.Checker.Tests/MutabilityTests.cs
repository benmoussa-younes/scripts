namespace Scripts.Checker.Tests;

public sealed class MutabilityTests
{
    [Fact]
    public void Immutable_bindings_cannot_be_assigned()
    {
        Checking.AssertMarked("let n = 1\n‸n = 2", "SCRIPTS-TYPE-0039");
        Checking.AssertMarked("let n = 1\n‸n += 2", "SCRIPTS-TYPE-0039");
        Checking.AssertMarked("function f(n: integer) {\n    ‸n = 2\n}", "SCRIPTS-TYPE-0039");
        Checking.AssertMarked("for item in [1] {\n    ‸item = 2\n}", "SCRIPTS-TYPE-0039");
        Checking.AssertMarked("let xs = [1]\n‸xs[0] = 2", "SCRIPTS-TYPE-0039");
        Checking.AssertMarked("let m = {\"a\": 1}\n‸m[\"a\"] = 2", "SCRIPTS-TYPE-0039");
        Checking.AssertMarked("match 1 {\n    other => {\n        ‸other = 2\n    }\n}", "SCRIPTS-TYPE-0039");
    }

    [Fact]
    public void Mutable_variables_accept_every_assignment_form()
    {
        Checking.AssertClean("""
            let mutable n = 1
            n = 2
            n += 3
            n -= 1
            n *= 2
            n /= 2
            n %= 3
            let mutable text = "a"
            text += "b"
            let mutable xs = [1]
            xs[0] = 5
            xs += [2]
            let mutable m = {"a": 1}
            m["a"] = 2
            m["b"] = 3
            let mutable grid = [[1], [2]]
            grid[0][0] = 4
            let mutable nested = {"k": {"j": 1}}
            nested["k"]["j"] = 2
            io.write_standard_output_line("{n}{text}{xs.length()}{m.length()}{grid.length()}{nested.length()}")
            """);
    }

    [Fact]
    public void Assignments_are_type_checked()
    {
        Checking.AssertMarked("let mutable n = 1\nn = ‸\"a\"", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("let mutable xs = [1]\nxs[0] = ‸\"a\"", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("let mutable xs = [1]\nxs[‸\"k\"] = 1", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("let mutable m = {\"a\": 1}\nm[‸1] = 1", "SCRIPTS-TYPE-0012");
        Checking.AssertMarked("let mutable xs: list<integer> = []\nxs = [‸\"a\"]", "SCRIPTS-TYPE-0012");
    }

    [Fact]
    public void Only_variables_can_be_assigned()
    {
        Checking.AssertMarked("function f() {\n}\n‸f = 1", "SCRIPTS-TYPE-0040");
        Checking.AssertMarked("‸assert = 1", "SCRIPTS-TYPE-0040");
        Checking.AssertMarked("‸environment = 1", "SCRIPTS-TYPE-0040");
        Checking.AssertMarked("‸missing = 1", "SCRIPTS-TYPE-0001");
        Checking.AssertMarkedInModules("import \"lib.scs\" as lib\n‸lib = 1", [("lib.scs", "public let value = 1\n")], "SCRIPTS-TYPE-0040");
    }

    [Fact]
    public void Non_indexable_assignment_targets_are_reported()
    {
        Checking.AssertMarked("let mutable n = 1\n‸n[0] = 1", "SCRIPTS-TYPE-0041");
        Checking.AssertMarked("let mutable s = \"abc\"\n‸s[0] = \"x\"", "SCRIPTS-TYPE-0041");
    }

    [Fact]
    public void Mutating_methods_need_an_identifier_bound_by_let_mutable()
    {
        Checking.AssertClean("let mutable xs = [1]\nxs.append(2)\nlet mutable m = {\"a\": 1}\nlet removed = m.remove_key(\"a\")\nio.write_standard_output_line(\"{removed}\")");
        Checking.AssertMarked("let xs = [1]\n‸xs.append(2)", "SCRIPTS-TYPE-0043");
        Checking.AssertMarked("let m = {\"a\": 1}\nlet removed = ‸m.remove_key(\"a\")", "SCRIPTS-TYPE-0043");
        Checking.AssertMarked("function f(xs: list<integer>) {\n    ‸xs.append(2)\n}", "SCRIPTS-TYPE-0043");
        Checking.AssertMarked("let mutable m = {\"k\": [1]}\n‸m[\"k\"].append(2)", "SCRIPTS-TYPE-0042");
        Checking.AssertMarked("function make() -> list<integer> {\n    return [1]\n}\n‸make().append(2)", "SCRIPTS-TYPE-0042");
    }

    [Fact]
    public void The_receiver_shape_is_checked_before_mutability()
    {
        Checking.AssertMarked("let xs = [[1]]\n‸xs[0].append(2)", "SCRIPTS-TYPE-0042");
    }

    [Fact]
    public void Lambdas_may_call_mutating_methods_on_captured_mutable_variables()
    {
        Checking.AssertClean("""
            function for_each(items: list<integer>, action: function(integer) -> unit) {
                for item in items {
                    action(item)
                }
            }
            let mutable seen: list<integer> = []
            for_each([1, 2], n => seen.append(n))
            io.write_standard_output_line("{seen.length()}")
            """);
    }
}
