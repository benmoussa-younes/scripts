using Scripts.Core;
using static Scripts.Runtime.Tests.Pure.PureTestSupport;

namespace Scripts.Runtime.Tests.Pure;

public class PureListFunctionsTests
{
    [Fact]
    public void LengthCountsElements()
    {
        Assert.Equal(Integer(0), Call("list<T>.length", List()));
        Assert.Equal(Integer(3), Call("list<T>.length", IntegerList(10, 20, 30)));
    }

    [Theory]
    [InlineData(0L, 10L)]
    [InlineData(1L, 20L)]
    [InlineData(2L, 30L)]
    public void GetReturnsElementInsideBounds(long index, long expected) =>
        Assert.Equal(expected, OkInteger(Call("list<T>.get_at_index", IntegerList(10, 20, 30), Integer(index))));

    [Theory]
    [InlineData(3L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(4294967296L)]
    public void GetReportsMissingIndexWithoutFailing(long index) =>
        AssertValueError(Call("list<T>.get_at_index", IntegerList(10, 20, 30), Integer(index)), "missing_index");

    [Fact]
    public void GetOnEmptyListReportsMissingIndex() =>
        AssertValueError(Call("list<T>.get_at_index", List(), Integer(0)), "missing_index");

    [Fact]
    public void GetReturnsIndependentCopyOfNestedLists()
    {
        var nested = List(IntegerList(1));
        var element = (ListValue)OkPayload(Call("list<T>.get_at_index", nested, Integer(0)));
        element.Items.Add(Integer(2));
        Assert.Single(((ListValue)nested.Items[0]).Items);
    }

    [Fact]
    public void AppendMutatesReceiverInPlace()
    {
        var items = IntegerList(1, 2);
        var snapshot = (ListValue)items.DeepCopy();
        Assert.Same(UnitValue.Instance, Call("list<T>.append", items, Integer(3)));
        Assert.Equal(IntegerList(1, 2, 3), items);
        Assert.Equal(IntegerList(1, 2), snapshot);
    }

    [Fact]
    public void AppendStoresIndependentCopyOfAppendedList()
    {
        var outer = List();
        var inner = IntegerList(1);
        Call("list<T>.append", outer, inner);
        inner.Items.Add(Integer(2));
        Assert.Equal(List(IntegerList(1)), outer);
    }

    [Fact]
    public void ContainsUsesStructuralEquality()
    {
        Assert.Equal(BooleanValue.True, Call("list<T>.contains", IntegerList(1, 2, 3), Integer(2)));
        Assert.Equal(BooleanValue.False, Call("list<T>.contains", IntegerList(1, 2, 3), Integer(9)));
        Assert.Equal(BooleanValue.False, Call("list<T>.contains", List(), Integer(1)));
        Assert.Equal(BooleanValue.True, Call("list<T>.contains", List(IntegerList(1, 2), IntegerList(3)), IntegerList(3)));
        Assert.Equal(BooleanValue.True, Call("list<T>.contains", List(Float(0.0)), Float(-0.0)));
        Assert.Equal(BooleanValue.False, Call("list<T>.contains", TextList("a"), Text("A")));
        Assert.Equal(BooleanValue.True, Call("list<T>.contains", List(Path("/a")), Path("/a")));
    }

    [Theory]
    [InlineData(new[] { "a", "b", "c" }, "-", "a-b-c")]
    [InlineData(new[] { "a", "b" }, "", "ab")]
    [InlineData(new string[0], ",", "")]
    [InlineData(new[] { "x" }, ", ", "x")]
    [InlineData(new[] { "", "" }, "/", "/")]
    [InlineData(new[] { "\U0001F600", "é" }, "\u2603", "\U0001F600\u2603é")]
    public void JoinConcatenatesWithSeparator(string[] items, string separator, string expected) =>
        Assert.Equal(Text(expected), Call("list<string>.join", TextList(items), Text(separator)));

    [Fact]
    public void FilterKeepsMatchingElementsInOrderCallingPredicateOncePerElement()
    {
        var visited = new List<long>();
        var predicate = Function(1, arguments =>
        {
            var number = ((IntegerValue)arguments[0]).Number;
            visited.Add(number);
            return Boolean(number % 2 == 0);
        });
        var original = IntegerList(1, 2, 3, 4);
        Assert.Equal(IntegerList(2, 4), Call("list<T>.filter", original, predicate));
        Assert.Equal([1L, 2L, 3L, 4L], visited);
        Assert.Equal(IntegerList(1, 2, 3, 4), original);
    }

    [Fact]
    public void FilterOnEmptyListNeverCallsPredicate()
    {
        var predicate = Function(1, _ => throw new InvalidOperationException("must not be called"));
        Assert.Equal(List(), Call("list<T>.filter", List(), predicate));
    }

    [Fact]
    public void FilterPropagatesFatalFailuresOfTheLambda()
    {
        var predicate = Function(1, _ => throw new RuntimeFailure(ErrorCodes.Runtime0011, ("function", "f")));
        AssertFailure(ErrorCodes.Runtime0011, () => Call("list<T>.filter", IntegerList(1), predicate));
    }

    [Fact]
    public void FilterIteratesOverSnapshotWhenLambdaMutatesTheList()
    {
        var items = IntegerList(1, 2);
        var predicate = Function(1, arguments =>
        {
            items.Items.Add(Integer(99));
            return BooleanValue.True;
        });
        Assert.Equal(IntegerList(1, 2), Call("list<T>.filter", items, predicate));
    }

    [Fact]
    public void TransformMapsEachElementInOrder()
    {
        var mapper = Function(1, arguments => Text($"n={((IntegerValue)arguments[0]).Number}"));
        Assert.Equal(TextList("n=1", "n=2"), Call("list<T>.transform", IntegerList(1, 2), mapper));
        Assert.Equal(List(), Call("list<T>.transform", List(), mapper));
    }

    [Fact]
    public void FoldReducesFromLeftToRight()
    {
        var concatenate = Function(2, arguments => Text(((StringValue)arguments[0]).Text + ((StringValue)arguments[1]).Text));
        Assert.Equal(Text("abc"), Call("list<T>.fold", TextList("a", "b", "c"), Text(string.Empty), concatenate));
        Assert.Equal(Text("seed"), Call("list<T>.fold", List(), Text("seed"), concatenate));
        var subtract = Function(2, arguments => Integer(((IntegerValue)arguments[0]).Number - ((IntegerValue)arguments[1]).Number));
        Assert.Equal(Integer(10 - 1 - 2 - 3), Call("list<T>.fold", IntegerList(1, 2, 3), Integer(10), subtract));
    }

    [Fact]
    public void SortedIsAscendingStableAndLeavesReceiverUntouched()
    {
        var original = TextList("pomme", "abricot", "cerise");
        Assert.Equal(TextList("abricot", "cerise", "pomme"), Call("list<T>.sorted", original));
        Assert.Equal(TextList("pomme", "abricot", "cerise"), original);
        Assert.Equal(IntegerList(-5, 0, 3, 3, 9), Call("list<T>.sorted", IntegerList(3, 9, -5, 0, 3)));
        Assert.Equal(List(), Call("list<T>.sorted", List()));
    }

    [Fact]
    public void SortedOrdersStringsByUtf16Units()
    {
        var sorted = ToTexts(Call("list<T>.sorted", TextList("\U00010000", "\uFFFF", "B", "a", "")));
        Assert.Equal(["", "B", "a", "\U00010000", "\uFFFF"], sorted);
    }

    [Fact]
    public void SortedOrdersPathsAndFloats()
    {
        Assert.Equal(List(Path("/a"), Path("/b"), Path("a")), Call("list<T>.sorted", List(Path("a"), Path("/b"), Path("/a"))));
        Assert.Equal(List(Float(-1.5), Float(0.5), Float(2.0)), Call("list<T>.sorted", List(Float(2.0), Float(-1.5), Float(0.5))));
    }

    [Fact]
    public void SortedKeepsRelativeOrderOfNegativeAndPositiveZero()
    {
        var sorted = (ListValue)Call("list<T>.sorted", List(Float(0.0), Float(double.NegativeZero), Float(-1.0)));
        Assert.Equal([-1.0, 0.0, double.NegativeZero], sorted.Items.Select(item => ((FloatValue)item).Number));
        Assert.True(double.IsNegative(((FloatValue)sorted.Items[2]).Number));
        Assert.False(double.IsNegative(((FloatValue)sorted.Items[1]).Number));
    }

    [Fact]
    public void ReversedReturnsNewListInReverseOrder()
    {
        var original = IntegerList(1, 2, 3);
        Assert.Equal(IntegerList(3, 2, 1), Call("list<T>.reversed", original));
        Assert.Equal(IntegerList(1, 2, 3), original);
        Assert.Equal(List(), Call("list<T>.reversed", List()));
    }

    [Fact]
    public void AnyShortCircuitsAndEmptyIsFalse()
    {
        var visited = new List<long>();
        var predicate = Function(1, arguments =>
        {
            var number = ((IntegerValue)arguments[0]).Number;
            visited.Add(number);
            return Boolean(number == 2);
        });
        Assert.Equal(BooleanValue.True, Call("list<T>.any_match", IntegerList(1, 2, 3, 4), predicate));
        Assert.Equal([1L, 2L], visited);
        var never = Function(1, _ => throw new InvalidOperationException("must not be called"));
        Assert.Equal(BooleanValue.False, Call("list<T>.any_match", List(), never));
    }

    [Fact]
    public void AllShortCircuitsAndEmptyIsTrue()
    {
        var visited = new List<long>();
        var predicate = Function(1, arguments =>
        {
            var number = ((IntegerValue)arguments[0]).Number;
            visited.Add(number);
            return Boolean(number % 2 == 0);
        });
        Assert.Equal(BooleanValue.False, Call("list<T>.all_match", IntegerList(2, 3, 4), predicate));
        Assert.Equal([2L, 3L], visited);
        var never = Function(1, _ => throw new InvalidOperationException("must not be called"));
        Assert.Equal(BooleanValue.True, Call("list<T>.all_match", List(), never));
    }

    [Fact]
    public void CountVisitsEveryElement()
    {
        var visited = new List<long>();
        var predicate = Function(1, arguments =>
        {
            var number = ((IntegerValue)arguments[0]).Number;
            visited.Add(number);
            return Boolean(number % 2 == 0);
        });
        Assert.Equal(Integer(2), Call("list<T>.count", IntegerList(1, 2, 3, 4), predicate));
        Assert.Equal([1L, 2L, 3L, 4L], visited);
        Assert.Equal(Integer(0), Call("list<T>.count", List(), Function(1, _ => Boolean(true))));
    }

    [Fact]
    public void FindReturnsFirstMatchOrNotFound()
    {
        var predicate = Function(1, arguments => Boolean(((IntegerValue)arguments[0]).Number % 4 == 0));
        Assert.Equal(12L, OkInteger(Call("list<T>.find_first", IntegerList(5, 12, 8), predicate)));
        AssertValueError(Call("list<T>.find_first", IntegerList(5, 7), predicate), "not_found");
        AssertValueError(Call("list<T>.find_first", List(), predicate), "not_found");
    }

    [Fact]
    public void SumAddsIntegersEmptyIsZeroAndOverflowIsFatal()
    {
        Assert.Equal(Integer(10), Call("list<integer>.sum", IntegerList(1, 2, 3, 4)));
        Assert.Equal(Integer(0), Call("list<integer>.sum", List()));
        AssertFailure(ErrorCodes.Runtime0001, () => Call("list<integer>.sum", IntegerList(long.MaxValue, 1)));
    }

    [Fact]
    public void MinMaxReturnExtremumOrNotFound()
    {
        Assert.Equal(1L, OkInteger(Call("list<T>.minimum", IntegerList(3, 1, 2))));
        Assert.Equal(3L, OkInteger(Call("list<T>.maximum", IntegerList(3, 1, 2))));
        Assert.Equal("abricot", OkText(Call("list<T>.minimum", TextList("pomme", "abricot", "cerise"))));
        Assert.Equal("pomme", OkText(Call("list<T>.maximum", TextList("pomme", "abricot", "cerise"))));
        Assert.Equal(1.5, OkFloat(Call("list<T>.minimum", List(Float(2.5), Float(1.5)))));
        AssertValueError(Call("list<T>.minimum", List()), "not_found");
        AssertValueError(Call("list<T>.maximum", List()), "not_found");
    }

    [Fact]
    public void FindReturnsIndependentCopy()
    {
        var nested = List(IntegerList(1));
        var found = (ListValue)OkPayload(Call("list<T>.find_first", nested, Function(1, _ => Boolean(true))));
        found.Items.Add(Integer(99));
        Assert.Single(((ListValue)nested.Items[0]).Items);
    }
}

public class PureMapFunctionsTests
{
    private static MapValue Ages()
    {
        var map = new MapValue();
        map.Set(Text("alice"), Integer(30));
        map.Set(Text("bob"), Integer(25));
        return map;
    }

    [Fact]
    public void LengthCountsEntries()
    {
        Assert.Equal(Integer(2), Call("map<K,V>.length", Ages()));
        Assert.Equal(Integer(0), Call("map<K,V>.length", new MapValue()));
    }

    [Fact]
    public void GetReturnsValueOrMissingKey()
    {
        Assert.Equal(30L, OkInteger(Call("map<K,V>.get_value", Ages(), Text("alice"))));
        AssertValueError(Call("map<K,V>.get_value", Ages(), Text("carol")), "missing_key");
        AssertValueError(Call("map<K,V>.get_value", new MapValue(), Text("a")), "missing_key");
    }

    [Fact]
    public void GetSupportsEveryKeyType()
    {
        var map = new MapValue();
        map.Set(Integer(1), Text("one"));
        map.Set(Path("/p"), Text("path"));
        map.Set(Boolean(true), Text("true"));
        Assert.Equal("one", OkText(Call("map<K,V>.get_value", map, Integer(1))));
        Assert.Equal("path", OkText(Call("map<K,V>.get_value", map, Path("/p"))));
        Assert.Equal("true", OkText(Call("map<K,V>.get_value", map, Boolean(true))));
        AssertValueError(Call("map<K,V>.get_value", map, Integer(2)), "missing_key");
        AssertValueError(Call("map<K,V>.get_value", map, Boolean(false)), "missing_key");
    }

    [Fact]
    public void GetReturnsIndependentCopyOfNestedValue()
    {
        var map = new MapValue();
        map.Set(Text("k"), IntegerList(1));
        var copy = (ListValue)OkPayload(Call("map<K,V>.get_value", map, Text("k")));
        copy.Items.Add(Integer(2));
        Assert.Single(((ListValue)map.Values[0]).Items);
    }

    [Fact]
    public void ContainsKeyTestsPresence()
    {
        Assert.Equal(BooleanValue.True, Call("map<K,V>.contains_key", Ages(), Text("alice")));
        Assert.Equal(BooleanValue.False, Call("map<K,V>.contains_key", Ages(), Text("carol")));
        Assert.Equal(BooleanValue.False, Call("map<K,V>.contains_key", Ages(), Text("ALICE")));
    }

    [Fact]
    public void KeysAndValuesFollowInsertionOrder()
    {
        var map = Ages();
        map.Set(Text("alice"), Integer(31));
        map.Set(Text("zoe"), Integer(1));
        Assert.Equal(TextList("alice", "bob", "zoe"), Call("map<K,V>.keys", map));
        Assert.Equal(IntegerList(31, 25, 1), Call("map<K,V>.values", map));
        Assert.Equal(List(), Call("map<K,V>.keys", new MapValue()));
        Assert.Equal(List(), Call("map<K,V>.values", new MapValue()));
    }

    [Fact]
    public void ValuesReturnsIndependentCopiesOfNestedValues()
    {
        var map = new MapValue();
        map.Set(Text("k"), IntegerList(1));
        ((ListValue)((ListValue)Call("map<K,V>.values", map)).Items[0]).Items.Add(Integer(2));
        Assert.Single(((ListValue)map.Values[0]).Items);
    }

    [Fact]
    public void RemoveKeyDeletesInPlaceKeepingOrderOfOthers()
    {
        var map = Ages();
        map.Set(Text("carol"), Integer(40));
        Assert.Equal(BooleanValue.True, Call("map<K,V>.remove_key", map, Text("bob")));
        Assert.Equal(BooleanValue.False, Call("map<K,V>.remove_key", map, Text("bob")));
        Assert.Equal(TextList("alice", "carol"), Call("map<K,V>.keys", map));
        Assert.Equal(Integer(2), Call("map<K,V>.length", map));
        Assert.Equal(40L, OkInteger(Call("map<K,V>.get_value", map, Text("carol"))));
    }
}

public class PureResultFunctionsTests
{
    private static ResultValue Success(Value payload) => ResultValue.Ok(payload);

    private static ResultValue Failure(string kind) => ResultValue.Error(ErrorValues.Error(kind, "", "failed"));

    [Fact]
    public void IsOkTestsVariant()
    {
        Assert.Equal(BooleanValue.True, Call("result<T,E>.is_ok", Success(Integer(1))));
        Assert.Equal(BooleanValue.False, Call("result<T,E>.is_ok", Failure("invalid_integer")));
        Assert.Equal(BooleanValue.True, Call("result<T,E>.is_ok", Call("string.parse_integer", Text("12"))));
        Assert.Equal(BooleanValue.False, Call("result<T,E>.is_ok", Call("string.parse_integer", Text("x"))));
    }

    [Fact]
    public void UnwrapOrReturnsPayloadOrDefault()
    {
        Assert.Equal(Integer(7), Call("result<T,E>.value_or", Success(Integer(7)), Integer(0)));
        Assert.Equal(Integer(0), Call("result<T,E>.value_or", Failure("invalid_integer"), Integer(0)));
        Assert.Equal(Integer(7), Call("result<T,E>.value_or", Call("string.parse_integer", Text("7")), Integer(0)));
    }

    [Fact]
    public void UnwrapReturnsPayloadOfOk() =>
        Assert.Equal(Integer(12), Call("result<T,E>.value_or_fail", Call("string.parse_integer", Text("12"))));

    [Fact]
    public void UnwrapOnValueErrorIsFatalWithDescription()
    {
        var failure = AssertFailure(ErrorCodes.Runtime0007, () => Call("result<T,E>.value_or_fail", Call("string.parse_integer", Text("x"))));
        Assert.Equal("invalid_integer : invalid integer: \"x\"", ArgumentValue(failure, "error_description"));
    }

    [Fact]
    public void UnwrapDescribesEnvironmentAndStringErrors()
    {
        var environmentFailure = AssertFailure(ErrorCodes.Runtime0007, () =>
            Call("result<T,E>.value_or_fail", ResultValue.Error(ErrorValues.EnvironmentFailure("not_found", "NOM", "absente"))));
        Assert.Equal("not_found : NOM : absente", ArgumentValue(environmentFailure, "error_description"));
        var stringFailure = AssertFailure(ErrorCodes.Runtime0007, () => Call("result<T,E>.value_or_fail", ResultValue.Error(Text("boom"))));
        Assert.Equal("boom", ArgumentValue(stringFailure, "error_description"));
        var ioFailure = AssertFailure(ErrorCodes.Runtime0007, () =>
            Call("result<T,E>.value_or_fail", ResultValue.Error(ErrorValues.Error("not_found", "/x", "introuvable"))));
        Assert.Equal("not_found : /x : introuvable", ArgumentValue(ioFailure, "error_description"));
    }

    [Fact]
    public void MapErrorConvertsOnlyTheError()
    {
        var kindOf = Function(1, arguments => ((RecordValue)arguments[0]).Get("kind"));
        var converted = Call("result<T,E>.transform_error", Call("string.parse_integer", Text("x")), kindOf);
        Assert.Equal(ResultValue.Error(Text("invalid_integer")), converted);

        var neverCalled = Function(1, _ => throw new InvalidOperationException("must not be called"));
        Assert.Equal(Success(Integer(5)), Call("result<T,E>.transform_error", Success(Integer(5)), neverCalled));
    }

    [Fact]
    public void MapErrorPropagatesFatalFailuresOfTheLambda()
    {
        var failing = Function(1, _ => throw new RuntimeFailure(ErrorCodes.Runtime0011, ("function", "f")));
        AssertFailure(ErrorCodes.Runtime0011, () => Call("result<T,E>.transform_error", Failure("invalid_integer"), failing));
    }
}

[Collection("ProcessEnvironment")]
public class PureEnvironmentFunctionsTests
{
    private const string VariableName = "SCRIPTS_PURE_TEST_VARIABLE";

    [Fact]
    public void SetThenGetReturnsLatestValue()
    {
        Assert.True(((ResultValue)Call("environment.set_variable", Text(VariableName), Text("one"))).IsOk);
        Assert.True(((ResultValue)Call("environment.set_variable", Text(VariableName), Text("two"))).IsOk);
        Assert.Equal("two", OkText(Call("environment.get_variable", Text(VariableName))));
        Assert.Equal("two", Environment.GetEnvironmentVariable(VariableName));
        Call("environment.remove_variable", Text(VariableName));
    }

    [Fact]
    public void EmptyValueIsValid()
    {
        Call("environment.set_variable", Text(VariableName), Text(string.Empty));
        Assert.Equal(string.Empty, OkText(Call("environment.get_variable", Text(VariableName))));
        Call("environment.remove_variable", Text(VariableName));
    }

    [Fact]
    public void GetOfAbsentVariableReturnsEnvErrorNamingIt()
    {
        var error = ErrorRecord(Call("environment.get_variable", Text("SCRIPTS_PURE_TEST_ABSENT")));
        Assert.Equal("error", error.TypeName);
        Assert.Equal("SCRIPTS_PURE_TEST_ABSENT", error.GetText("subject"));
        Assert.Equal("not_found", error.GetText("kind"));
        Assert.Equal("not_found", error.GetText("kind"));
        Assert.False(string.IsNullOrEmpty(error.GetText("message")));
    }

    [Fact]
    public void RemoveIsIdempotent()
    {
        Call("environment.set_variable", Text(VariableName), Text("x"));
        Assert.True(((ResultValue)Call("environment.remove_variable", Text(VariableName))).IsOk);
        Assert.True(((ResultValue)Call("environment.remove_variable", Text(VariableName))).IsOk);
        Assert.False(((ResultValue)Call("environment.get_variable", Text(VariableName))).IsOk);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A=B")]
    [InlineData("=")]
    [InlineData("A\0B")]
    public void InvalidNamesYieldEnvErrorForEveryFunction(string name)
    {
        foreach (var result in new[]
        {
            Call("environment.get_variable", Text(name)),
            Call("environment.set_variable", Text(name), Text("v")),
            Call("environment.remove_variable", Text(name)),
        })
        {
            var error = ErrorRecord(result);
            Assert.Equal("error", error.TypeName);
            Assert.Equal(name, error.GetText("subject"));
        }
    }

    [Fact]
    public void ValueContainingNulYieldsEnvErrorAndLeavesVariableUntouched()
    {
        Call("environment.remove_variable", Text(VariableName));
        var error = ErrorRecord(Call("environment.set_variable", Text(VariableName), Text("a\0b")));
        Assert.Equal(VariableName, error.GetText("subject"));
        Assert.Null(Environment.GetEnvironmentVariable(VariableName));
    }

    [Fact]
    public void NamesAreCaseSensitive()
    {
        Call("environment.set_variable", Text(VariableName), Text("x"));
        Assert.False(((ResultValue)Call("environment.get_variable", Text(VariableName.ToLowerInvariant()))).IsOk);
        Call("environment.remove_variable", Text(VariableName));
    }

    [Fact]
    public void AllReturnsOrdinalSortedSnapshotCopy()
    {
        Call("environment.set_variable", Text(VariableName), Text("value"));
        var snapshot = (MapValue)Call("environment.all_variables");
        Call("environment.set_variable", Text(VariableName), Text("changed"));
        Assert.Equal("value", OkText(Call("map<K,V>.get_value", snapshot, Text(VariableName))));
        Assert.Equal(BooleanValue.False, Call("map<K,V>.contains_key", snapshot, Text("SCRIPTS_PURE_TEST_ABSENT")));

        var keys = snapshot.Keys.Select(key => ((StringValue)key).Text).ToArray();
        var expectedOrder = keys.Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedOrder, keys);
        Assert.DoesNotContain(keys, key => key.Length == 0 || key.Contains('\0'));
        Call("environment.remove_variable", Text(VariableName));
    }
}
