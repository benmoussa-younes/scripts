using Scripts.Core;

namespace Scripts.Checker;

/// <summary>Type-variable bindings of one generic stdlib call (DESIGN 3.3 rule 4).</summary>
internal sealed class TypeBindings
{
    private readonly Dictionary<string, ScriptType> bound = new(StringComparer.Ordinal);

    public ScriptType Apply(ScriptType type) =>
        type switch
        {
            TypeVariable variable => bound.TryGetValue(variable.Name, out var value) ? value : type,
            ListType list => new ListType(Apply(list.Element)),
            MapType map => new MapType(Apply(map.Key), Apply(map.Value)),
            ResultType result => new ResultType(Apply(result.Success), Apply(result.Failure)),
            FunctionType function => new FunctionType([.. function.Parameters.Select(Apply)], Apply(function.Return)),
            _ => type,
        };

    /// <summary>Matches a signature type against an actual type, binding unbound variables; false on a structural mismatch.</summary>
    public bool TryUnify(ScriptType pattern, ScriptType actual)
    {
        if (actual is ErrorType)
        {
            BindUnboundVariablesToError(pattern);
            return true;
        }

        switch (pattern)
        {
            case TypeVariable variable:
                if (bound.TryGetValue(variable.Name, out var existing))
                {
                    return TypeRelations.IsCompatible(existing, actual);
                }

                if (!TypeRelations.IsNever(actual))
                {
                    bound[variable.Name] = actual;
                }

                return true;
            case ErrorType:
                return true;
        }

        if (TypeRelations.IsNever(actual))
        {
            return true;
        }

        return (pattern, actual) switch
        {
            (ListType patternList, ListType actualList) => TryUnify(patternList.Element, actualList.Element),
            (MapType patternMap, MapType actualMap) => TryUnify(patternMap.Key, actualMap.Key) & TryUnify(patternMap.Value, actualMap.Value),
            (ResultType patternResult, ResultType actualResult) =>
                TryUnify(patternResult.Success, actualResult.Success) & TryUnify(patternResult.Failure, actualResult.Failure),
            (FunctionType patternFunction, FunctionType actualFunction) => TryUnifyFunctions(patternFunction, actualFunction),
            _ => pattern.Equals(actual),
        };
    }

    private bool TryUnifyFunctions(FunctionType pattern, FunctionType actual)
    {
        if (pattern.Parameters.Count != actual.Parameters.Count)
        {
            return false;
        }

        var unified = true;
        for (var position = 0; position < pattern.Parameters.Count; position++)
        {
            unified &= TryUnify(pattern.Parameters[position], actual.Parameters[position]);
        }

        return TryUnify(pattern.Return, actual.Return) & unified;
    }

    private void BindUnboundVariablesToError(ScriptType pattern)
    {
        switch (pattern)
        {
            case TypeVariable variable:
                bound.TryAdd(variable.Name, ErrorType.Instance);
                break;
            case ListType list:
                BindUnboundVariablesToError(list.Element);
                break;
            case MapType map:
                BindUnboundVariablesToError(map.Key);
                BindUnboundVariablesToError(map.Value);
                break;
            case ResultType result:
                BindUnboundVariablesToError(result.Success);
                BindUnboundVariablesToError(result.Failure);
                break;
            case FunctionType function:
                foreach (var parameter in function.Parameters)
                {
                    BindUnboundVariablesToError(parameter);
                }

                BindUnboundVariablesToError(function.Return);
                break;
        }
    }
}
