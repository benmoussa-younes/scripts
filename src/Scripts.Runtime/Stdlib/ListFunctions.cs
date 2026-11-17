using System.Text;
using Scripts.Core;
using static Scripts.Runtime.ArgumentAccess;

namespace Scripts.Runtime;

/// <summary>The list methods (SPEC-STDLIB 7.7).</summary>
internal static class ListFunctions
{
    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["list<T>.length"] = (context, arguments) => IntegerValue.From(List(arguments, 0).Items.Count);
        table["list<T>.get_at_index"] = Get;
        table["list<T>.append"] = Append;
        table["list<T>.contains"] = Contains;
        table["list<string>.join"] = Join;
        table["list<T>.filter"] = Filter;
        table["list<T>.transform"] = Transform;
        table["list<T>.fold"] = Fold;
        table["list<T>.sorted"] = Sorted;
        table["list<T>.reversed"] = Reversed;
        table["list<T>.sorted_by"] = SortedBy;
        table["list<result<T,E>>.collect"] = Collect;
        table["list<T>.any_match"] = Any;
        table["list<T>.all_match"] = All;
        table["list<T>.count"] = Count;
        table["list<T>.find_first"] = Find;
        table["list<integer>.sum"] = Sum;
        table["list<T>.minimum"] = Min;
        table["list<T>.maximum"] = Max;
        table["list<T>.slice"] = Slice;
        table["list<T>.insert_at"] = InsertAt;
        table["list<T>.remove_at"] = RemoveAt;
        table["list<T>.distinct"] = Distinct;
        table["list<list<T>>.flatten"] = Flatten;
        table["list<T>.group_by"] = GroupBy;
    }

    private static Value Slice(RuntimeContext context, Value[] arguments)
    {
        var items = List(arguments, 0).Items;
        var startIndex = Integer(arguments, 1);
        var count = Integer(arguments, 2);
        if (startIndex < 0 || count < 0 || startIndex > items.Count || count > items.Count - startIndex)
        {
            return ResultValue.Error(ErrorValues.Error("out_of_range", "", $"slice [{startIndex}, {startIndex + count}) is outside a list of length {items.Count}"));
        }

        var result = new List<Value>((int)count);
        for (var index = (int)startIndex; index < startIndex + count; index++)
        {
            result.Add(items[index].DeepCopy());
        }

        return ResultValue.Ok(new ListValue(result));
    }

    private static Value InsertAt(RuntimeContext context, Value[] arguments)
    {
        var items = List(arguments, 0).Items;
        var index = Integer(arguments, 1);
        if (index < 0 || index > items.Count)
        {
            return ResultValue.Error(ErrorValues.Error("out_of_range", "", $"index {index} is outside 0..{items.Count}"));
        }

        SizeLimits.EnsureListLength((long)items.Count + 1);
        items.Insert((int)index, arguments[2].DeepCopy());
        return ResultValue.Ok(UnitValue.Instance);
    }

    private static Value RemoveAt(RuntimeContext context, Value[] arguments)
    {
        var items = List(arguments, 0).Items;
        var index = Integer(arguments, 1);
        if (index < 0 || index >= items.Count)
        {
            return ResultValue.Error(ErrorValues.Error("missing_index", "", $"missing index {index} in a list of length {items.Count}"));
        }

        var removed = items[(int)index].DeepCopy();
        items.RemoveAt((int)index);
        return ResultValue.Ok(removed);
    }

    private static Value Distinct(RuntimeContext context, Value[] arguments)
    {
        var seen = new List<Value>();
        foreach (var item in List(arguments, 0).Items)
        {
            var duplicate = false;
            foreach (var previous in seen)
            {
                if (ValueComparison.AreEqual(previous, item))
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                seen.Add(item.DeepCopy());
            }
        }

        return new ListValue(seen);
    }

    private static Value Flatten(RuntimeContext context, Value[] arguments)
    {
        var flattened = new List<Value>();
        foreach (var inner in List(arguments, 0).Items)
        {
            foreach (var item in ((ListValue)inner).Items)
            {
                flattened.Add(item.DeepCopy());
                SizeLimits.EnsureListLength(flattened.Count);
            }
        }

        return new ListValue(flattened);
    }

    private static Value GroupBy(RuntimeContext context, Value[] arguments)
    {
        var keyFunction = Function(arguments, 1);
        var groups = new MapValue();
        foreach (var item in List(arguments, 0).Items)
        {
            var key = context.Invoke(keyFunction, item);
            if (groups.TryGet(key, out var existing))
            {
                ((ListValue)existing).Items.Add(item.DeepCopy());
                continue;
            }

            groups.Set(key.DeepCopy(), new ListValue([item.DeepCopy()]));
        }

        return groups;
    }

    private static Value Any(RuntimeContext context, Value[] arguments)
    {
        var predicate = Function(arguments, 1);
        foreach (var item in List(arguments, 0).Items.ToArray())
        {
            if (((BooleanValue)context.Invoke(predicate, item)).Flag)
            {
                return BooleanValue.True;
            }
        }

        return BooleanValue.False;
    }

    private static Value All(RuntimeContext context, Value[] arguments)
    {
        var predicate = Function(arguments, 1);
        foreach (var item in List(arguments, 0).Items.ToArray())
        {
            if (!((BooleanValue)context.Invoke(predicate, item)).Flag)
            {
                return BooleanValue.False;
            }
        }

        return BooleanValue.True;
    }

    private static Value Count(RuntimeContext context, Value[] arguments)
    {
        var predicate = Function(arguments, 1);
        var matching = 0L;
        foreach (var item in List(arguments, 0).Items.ToArray())
        {
            if (((BooleanValue)context.Invoke(predicate, item)).Flag)
            {
                matching++;
            }
        }

        return IntegerValue.From(matching);
    }

    private static Value Find(RuntimeContext context, Value[] arguments)
    {
        var predicate = Function(arguments, 1);
        foreach (var item in List(arguments, 0).Items.ToArray())
        {
            if (((BooleanValue)context.Invoke(predicate, item)).Flag)
            {
                return ResultValue.Ok(item.DeepCopy());
            }
        }

        return ResultValue.Error(ErrorValues.Error("not_found", "", "no element satisfies the predicate"));
    }

    /// <summary>Integer sum (SPEC-STDLIB 7.7); overflow reuses the checked `+` (SCRIPTS-RUNTIME-0001), empty → 0.</summary>
    private static Value Sum(RuntimeContext context, Value[] arguments)
    {
        Value total = IntegerValue.From(0);
        foreach (var item in List(arguments, 0).Items)
        {
            total = ArithmeticOperations.Add(total, item);
        }

        return total;
    }

    private static Value Min(RuntimeContext context, Value[] arguments) =>
        Extremum(List(arguments, 0).Items, smaller: true);

    private static Value Max(RuntimeContext context, Value[] arguments) =>
        Extremum(List(arguments, 0).Items, smaller: false);

    private static Value Extremum(IReadOnlyList<Value> items, bool smaller)
    {
        if (items.Count == 0)
        {
            return ResultValue.Error(ErrorValues.Error("not_found", "", "the list is empty"));
        }

        var chosen = items[0];
        for (var index = 1; index < items.Count; index++)
        {
            var ordering = ValueComparison.Ordering.Compare(items[index], chosen);
            if (smaller ? ordering < 0 : ordering > 0)
            {
                chosen = items[index];
            }
        }

        return ResultValue.Ok(chosen.DeepCopy());
    }

    /// <summary>
    /// <c>collect()</c> (SPEC-STDLIB 7.7): all-ok → <c>ok(list of payloads)</c>, otherwise the <b>first</b>
    /// error in list order, with no partial list built. Payloads are copied (value semantics §3.6).
    /// </summary>
    private static Value Collect(RuntimeContext context, Value[] arguments)
    {
        var items = List(arguments, 0).Items;
        var collected = new List<Value>(items.Count);
        foreach (var item in items)
        {
            var element = (ResultValue)item;
            if (!element.IsOk)
            {
                return element.DeepCopy();
            }

            collected.Add(element.Payload.DeepCopy());
        }

        return ResultValue.Ok(new ListValue(collected));
    }

    private static Value Get(RuntimeContext context, Value[] arguments)
    {
        var items = List(arguments, 0).Items;
        var index = Integer(arguments, 1);
        if (index >= 0 && index < items.Count)
        {
            return ResultValue.Ok(items[(int)index].DeepCopy());
        }

        return ResultValue.Error(ErrorValues.Error(
            "missing_index", "",
            $"missing index: {index} for a list of length {items.Count}"));
    }

    private static Value Append(RuntimeContext context, Value[] arguments)
    {
        var items = List(arguments, 0).Items;
        SizeLimits.EnsureListLength((long)items.Count + 1);
        items.Add(arguments[1].DeepCopy());
        return UnitValue.Instance;
    }

    private static Value Contains(RuntimeContext context, Value[] arguments)
    {
        var searched = arguments[1];
        foreach (var item in List(arguments, 0).Items)
        {
            if (ValueComparison.AreEqual(item, searched))
            {
                return BooleanValue.True;
            }
        }

        return BooleanValue.False;
    }

    private static Value Join(RuntimeContext context, Value[] arguments)
    {
        var items = List(arguments, 0).Items;
        var separator = Text(arguments, 1);
        var totalLength = items.Count == 0 ? 0L : (long)separator.Length * (items.Count - 1);
        foreach (var item in items)
        {
            totalLength += ((StringValue)item).Text.Length;
        }

        SizeLimits.EnsureStringLength(totalLength);
        var builder = new StringBuilder((int)totalLength);
        for (var index = 0; index < items.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(separator);
            }

            builder.Append(((StringValue)items[index]).Text);
        }

        return new StringValue(builder.ToString());
    }

    private static Value Filter(RuntimeContext context, Value[] arguments)
    {
        var predicate = Function(arguments, 1);
        var kept = new List<Value>();
        foreach (var item in List(arguments, 0).Items.ToArray())
        {
            if (((BooleanValue)context.Invoke(predicate, item)).Flag)
            {
                kept.Add(item.DeepCopy());
            }
        }

        return new ListValue(kept);
    }

    private static Value Transform(RuntimeContext context, Value[] arguments)
    {
        var mapper = Function(arguments, 1);
        var source = List(arguments, 0).Items.ToArray();
        var converted = new List<Value>(source.Length);
        foreach (var item in source)
        {
            converted.Add(context.Invoke(mapper, item));
        }

        return new ListValue(converted);
    }

    private static Value Fold(RuntimeContext context, Value[] arguments)
    {
        var combiner = Function(arguments, 2);
        var accumulator = arguments[1];
        foreach (var item in List(arguments, 0).Items.ToArray())
        {
            accumulator = context.Invoke(combiner, accumulator, item);
        }

        return accumulator;
    }

    private static Value Sorted(RuntimeContext context, Value[] arguments) =>
        new ListValue(List(arguments, 0).Items.OrderBy(item => item, ValueComparison.Ordering).ToList());

    /// <summary>Stable ascending sort by a computed key (SPEC-STDLIB 7.7); the key type is ordered (checked statically).</summary>
    private static Value SortedBy(RuntimeContext context, Value[] arguments)
    {
        var key = Function(arguments, 1);
        var items = List(arguments, 0).Items.ToArray();

        // Precompute the key once per element, then sort stably (LINQ OrderBy is stable) by the ordinal value ordering.
        var keyed = new (Value Item, Value Key)[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            keyed[index] = (items[index], context.Invoke(key, items[index]));
        }

        return new ListValue(
            keyed.OrderBy(entry => entry.Key, ValueComparison.Ordering).Select(entry => entry.Item.DeepCopy()).ToList());
    }

    private static Value Reversed(RuntimeContext context, Value[] arguments)
    {
        var items = List(arguments, 0).Items;
        var reversed = new List<Value>(items.Count);
        for (var index = items.Count - 1; index >= 0; index--)
        {
            reversed.Add(items[index].DeepCopy());
        }

        return new ListValue(reversed);
    }
}
