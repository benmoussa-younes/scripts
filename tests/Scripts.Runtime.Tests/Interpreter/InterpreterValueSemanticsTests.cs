namespace Scripts.Runtime.Tests.InterpreterRuns;

/// <summary>Aliasing matrix: a mutation through one name must never be visible through another (DESIGN 3.6).</summary>
public sealed class InterpreterValueSemanticsTests
{
    public static TheoryData<string, string, string> Matrix => new()
    {
        {
            "let copies the value of a mutable source",
            """
            let mutable first = [1, 2]
            let second = first
            first.append(3)
            io.write_standard_output_line("{first.length()} {second.length()}")
            """,
            "3 2"
        },
        {
            "mutable from mutable diverges in both directions",
            """
            let mutable first = [1]
            let mutable second = first
            second.append(2)
            first.append(3)
            io.write_standard_output_line("{first[1]} {second[1]}")
            """,
            "3 2"
        },
        {
            "mutable from immutable never touches the source",
            """
            let source = [1, 2]
            let mutable copy = source
            copy.append(3)
            io.write_standard_output_line("{source.length()} {copy.length()}")
            """,
            "2 3"
        },
        {
            "assignment copies",
            """
            let mutable first = [1]
            let mutable second = [0]
            second = first
            first.append(2)
            io.write_standard_output_line("{first.length()} {second.length()}")
            """,
            "2 1"
        },
        {
            "parameter captured by a closure is not affected by later caller mutation",
            """
            function capture(items: list<integer>) -> function() -> integer {
                return () => items.length()
            }
            let mutable items = [1]
            let reader = capture(items)
            items.append(2)
            io.write_standard_output_line("{reader()}")
            """,
            "1"
        },
        {
            "returning a parameter gives an independent value",
            """
            function same(items: list<integer>) -> list<integer> {
                return items
            }
            let original = [1]
            let mutable alias = same(original)
            alias.append(2)
            io.write_standard_output_line("{original.length()} {alias.length()}")
            """,
            "1 2"
        },
        {
            "returning a parameter bound to a mutable argument gives an independent value",
            """
            function same(items: list<integer>) -> list<integer> {
                return items
            }
            let mutable original = [1]
            let mutable alias = same(original)
            alias.append(2)
            original.append(3)
            io.write_standard_output_line("{original.length()} {alias.length()} {original[1]} {alias[1]}")
            """,
            "2 2 3 2"
        },
        {
            "list literal elements are independent of their sources",
            """
            let mutable inner = [1]
            let mutable outer = [inner, inner]
            inner.append(2)
            outer[0][0] = 9
            io.write_standard_output_line("{inner.length()} {outer[0].length()} {outer[0][0]} {outer[1][0]}")
            """,
            "2 1 9 1"
        },
        {
            "map values are independent of their sources",
            """
            let mutable list_value = [1]
            let mutable table: map<string, list<integer>> = {"k": list_value}
            table["j"] = list_value
            list_value.append(2)
            table["k"][0] = 5
            io.write_standard_output_line("{list_value.length()} {table["k"][0]} {table["j"][0]} {table["j"].length()}")
            """,
            "2 5 1 1"
        },
        {
            "index assignment stores a copy",
            """
            let mutable cells = [[0], [0]]
            let mutable source = [7]
            cells[0] = source
            source.append(8)
            cells[1] = cells[0]
            cells[1][0] = 1
            io.write_standard_output_line("{cells[0].length()} {cells[0][0]} {cells[1][0]} {source.length()}")
            """,
            "1 7 1 2"
        },
        {
            "compound assignment on nested index and map",
            """
            let mutable grid = [[1, 2, 3], [4, 5, 6]]
            grid[1][2] += 5
            grid[0][0] *= 10
            let mutable counts = {"a": 1}
            counts["a"] += 1
            counts["a"] *= 5
            let mutable names = ["x"]
            names[0] += "!"
            io.write_standard_output_line("{grid[1][2]} {grid[0][0]} {counts["a"]} {names[0]}")
            """,
            "11 10 10 x!"
        },
        {
            "result payload is independent of its source",
            """
            let mutable items = [1]
            let wrapped: result<list<integer>, string> = ok(items)
            items.append(2)
            match wrapped {
                ok(inner) => io.write_standard_output_line("{inner.length()} {items.length()}")
                error(_) => io.write_standard_output_line("error")
            }
            """,
            "1 2"
        },
        {
            "binding extracted from a result is copied into a mutable variable",
            """
            let wrapped: result<list<integer>, string> = ok([1])
            match wrapped {
                ok(inner) => {
                    let mutable changed = inner
                    changed.append(2)
                    io.write_standard_output_line("{inner.length()} {changed.length()}")
                }
                error(_) => io.write_standard_output_line("error")
            }
            match wrapped {
                ok(again) => io.write_standard_output_line("{again.length()}")
                error(_) => io.write_standard_output_line("error")
            }
            """,
            "1 2\n1"
        },
        {
            "append stores a copy of the item",
            """
            let mutable items = [1]
            let mutable outer: list<list<integer>> = []
            outer.append(items)
            items.append(2)
            io.write_standard_output_line("{outer[0].length()} {items.length()}")
            """,
            "1 2"
        },
        {
            "unwrap_or default is copied",
            """
            let failure: result<list<integer>, string> = error("x")
            let mutable fallback = [1]
            let mutable chosen = failure.value_or(fallback)
            chosen.append(2)
            io.write_standard_output_line("{fallback.length()} {chosen.length()}")
            """,
            "1 2"
        },
        {
            "get and unwrap return copies",
            """
            let mutable outer = [[1]]
            let mutable first = outer.get_at_index(0).value_or_fail()
            first.append(2)
            let mutable second = outer.get_at_index(0).value_or([])
            second.append(3)
            io.write_standard_output_line("{outer[0].length()} {first.length()} {second.length()}")
            """,
            "1 2 2"
        },
        {
            "for iterates a deep snapshot of a mutable list",
            """
            let mutable rows = [[1], [2]]
            let mutable seen: list<integer> = []
            for row in rows {
                rows[1][0] = 99
                seen.append(row[0])
            }
            io.write_standard_output_line("{seen[0]} {seen[1]} {rows[1][0]}")
            """,
            "1 2 99"
        },
        {
            "transform with an identity lambda copies",
            """
            let mutable lists = [[1], [2]]
            let same_lists = lists.transform(item => item)
            lists[0][0] = 7
            io.write_standard_output_line("{same_lists[0][0]} {lists[0][0]}")
            """,
            "1 7"
        },
        {
            "closure returning a captured list returns a copy",
            """
            let mutable items = [1]
            let read = () => items
            let mutable obtained = read()
            obtained.append(2)
            io.write_standard_output_line("{items.length()} {obtained.length()}")
            """,
            "1 2"
        },
        {
            "constants are never changed through copies",
            """
            let NAMES = ["a"]
            function count() -> integer {
                return NAMES.length()
            }
            let mutable copy = NAMES
            copy.append("b")
            io.write_standard_output_line("{count()} {copy.length()}")
            """,
            "1 2"
        },
        {
            "nested functions and lambdas mutate the captured variable in place",
            """
            function run() -> integer {
                let mutable items = [1]
                function add() {
                    items.append(2)
                }
                add()
                add()
                let record = (value: integer) => items.append(value)
                record(9)
                return items.length()
            }
            io.write_standard_output_line("{run()}")
            """,
            "4"
        },
        {
            "left operand is evaluated before a later operand with side effects",
            """
            function run() -> string {
                let mutable items = [1]
                function grow() -> integer {
                    items.append(2)
                    return 0
                }
                let combined = items + [grow()]
                let wrapped = [items, [grow()]]
                return "{combined.length()} {wrapped[0].length()} {items.length()}"
            }
            io.write_standard_output_line(run())
            """,
            "2 2 3"
        },
        {
            "method receiver is evaluated before the arguments",
            """
            function run() -> boolean {
                let mutable items = [1]
                function grow() -> integer {
                    items.append(2)
                    return 2
                }
                return items.contains(grow())
            }
            io.write_standard_output_line("{run()}")
            """,
            "false"
        },
        {
            "argument is copied when it is evaluated",
            """
            function first_length(first: list<integer>, ignored: integer) -> integer {
                return first.length()
            }
            function run() -> integer {
                let mutable items = [1]
                function grow() -> integer {
                    items.append(2)
                    return 0
                }
                return first_length(items, grow())
            }
            io.write_standard_output_line("{run()}")
            """,
            "1"
        },
        {
            "function results are independent between calls",
            """
            function build() -> list<integer> {
                let mutable items = [1]
                return items
            }
            let mutable first = build()
            let second = build()
            first.append(2)
            io.write_standard_output_line("{first.length()} {second.length()}")
            """,
            "2 1"
        },
        {
            "the same list passed twice stays independent in the callee",
            """
            function sizes(first: list<integer>, second: list<integer>) -> string {
                return "{first.length()} {second.length()}"
            }
            let mutable items = [1]
            io.write_standard_output_line(sizes(items, items))
            items.append(2)
            io.write_standard_output_line(sizes(items, [1, 2, 3]))
            """,
            "1 1\n2 3"
        },
        {
            "immutable binding of a map keeps its own copy",
            """
            let mutable table = {"a": 1}
            let snapshot = table
            table["b"] = 2
            let removed = table.remove_key("a")
            io.write_standard_output_line("{snapshot.length()} {snapshot["a"]} {table.length()} {removed}")
            """,
            "1 1 1 true"
        },
        {
            "nested lists inside a map inside a list",
            """
            let mutable deep: list<map<string, list<integer>>> = [{"k": [1]}]
            let copy = deep
            deep[0]["k"][0] = 5
            io.write_standard_output_line("{copy[0]["k"][0]} {deep[0]["k"][0]}")
            """,
            "1 5"
        },
        {
            "a small integer used as a map key keeps value semantics",
            """
            let mutable table: map<integer, string> = {1: "one", 2: "two"}
            table[1] = "one"
            io.write_standard_output_line("{table[1]} {table[2]} {table.length()}")
            """,
            "one two 2"
        },
        {
            "a mutable string reused as a map key is copied",
            """
            let mutable key = "a"
            let table = {key: 1}
            key += "b"
            io.write_standard_output_line("{table.keys().join(",")}")
            """,
            "a"
        },
        {
            "compound assignment reads the receiver before a side-effecting right operand",
            """
            function f() -> string {
              let mutable x = "a"
              function bump() -> string {
                x += "B"
                return "C"
            }
              x += "Z"
              x += bump()
              return x
            }
            io.write_standard_output_line(f())
            """,
            "aZC"
        },
        {
            // The checker rejects a mutable list as a key (SCRIPTS-TYPE-0035); the interpreter still owes value semantics
            // for any key, so the copy is asserted here without the checker.
            "a mutable value reused as a map key is copied",
            """
            let mutable key = [1]
            let table = {key: "one"}
            key.append(2)
            io.write_standard_output_line("{table.keys()[0].length()}")
            """,
            "1"
        },
        {
            "a small integer bound by match is stable alongside an out-of-range one",
            """
            function label(number: integer) -> string {
                return match number {
                    3 => "three"
                    other => "other:{other}"
                }
            }
            io.write_standard_output_line("{label(3)} {label(1000)}")
            """,
            "three other:1000"
        },
        {
            "compound assignment on a small integer leaves an earlier snapshot unchanged",
            """
            let mutable a = 3
            let b = a
            a += 1
            let mutable table: map<integer, integer> = {3: 7}
            let key_snapshot = 3
            table[3] += 1
            io.write_standard_output_line("{a} {b} {table[3]} {key_snapshot}")
            """,
            "4 3 8 3"
        },
        {
            "an out-of-range integer keeps value semantics too",
            """
            let big = 1000
            let small = -200
            let mutable table: map<integer, integer> = {1000: 1, -200: 2}
            table[1000] += 5
            table[-200] *= 3
            io.write_standard_output_line("{big} {small} {table[1000]} {table[-200]} {table.length()}")
            """,
            "1000 -200 6 6 2"
        },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void AliasingMatrix(string name, string program, string expected)
    {
        Assert.False(string.IsNullOrEmpty(name));
        Assert.Equal(expected + "\n", InterpreterHarness.Output(program));
    }
}
