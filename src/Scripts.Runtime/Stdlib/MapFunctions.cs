using static Scripts.Runtime.ArgumentAccess;

namespace Scripts.Runtime;

/// <summary>The map methods (SPEC-STDLIB 7.8); iteration follows insertion order.</summary>
internal static class MapFunctions
{
    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["map<K,V>.length"] = (context, arguments) => IntegerValue.From(Map(arguments, 0).Count);
        table["map<K,V>.get_value"] = Get;
        table["map<K,V>.contains_key"] = (context, arguments) => BooleanValue.From(Map(arguments, 0).ContainsKey(arguments[1]));
        table["map<K,V>.keys"] = (context, arguments) => new ListValue([.. Map(arguments, 0).Keys]);
        table["map<K,V>.values"] = Values;
        table["map<K,V>.remove_key"] = (context, arguments) => BooleanValue.From(Map(arguments, 0).Remove(arguments[1]));
        table["map<K,V>.merged_with"] = MergedWith;
        table["map<K,V>.filter"] = Filter;
        table["map<K,V>.transform_values"] = TransformValues;
    }

    private static Value MergedWith(RuntimeContext context, Value[] arguments)
    {
        var merged = new MapValue();
        CopyInto(Map(arguments, 0), merged);
        CopyInto(Map(arguments, 1), merged);
        return merged;
    }

    private static void CopyInto(MapValue source, MapValue destination)
    {
        for (var index = 0; index < source.Keys.Count; index++)
        {
            destination.Set(source.Keys[index].DeepCopy(), source.Values[index].DeepCopy());
        }
    }

    private static Value Filter(RuntimeContext context, Value[] arguments)
    {
        var source = Map(arguments, 0);
        var predicate = Function(arguments, 1);
        var kept = new MapValue();
        for (var index = 0; index < source.Keys.Count; index++)
        {
            var key = source.Keys[index];
            var value = source.Values[index];
            if (((BooleanValue)context.Invoke(predicate, key, value)).Flag)
            {
                kept.Set(key.DeepCopy(), value.DeepCopy());
            }
        }

        return kept;
    }

    private static Value TransformValues(RuntimeContext context, Value[] arguments)
    {
        var source = Map(arguments, 0);
        var mapper = Function(arguments, 1);
        var transformed = new MapValue();
        for (var index = 0; index < source.Keys.Count; index++)
        {
            var key = source.Keys[index];
            var value = context.Invoke(mapper, source.Values[index]);
            transformed.Set(key.DeepCopy(), value.DeepCopy());
        }

        return transformed;
    }

    private static Value Get(RuntimeContext context, Value[] arguments)
    {
        if (Map(arguments, 0).TryGet(arguments[1], out var value))
        {
            return ResultValue.Ok(value.DeepCopy());
        }

        return ResultValue.Error(ErrorValues.Error("missing_key", "", $"missing key: {TextRenderer.Render(arguments[1])}"));
    }

    private static Value Values(RuntimeContext context, Value[] arguments)
    {
        var copies = new List<Value>(Map(arguments, 0).Count);
        foreach (var value in Map(arguments, 0).Values)
        {
            copies.Add(value.DeepCopy());
        }

        return new ListValue(copies);
    }
}
