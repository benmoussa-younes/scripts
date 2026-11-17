using Scripts.Core;

namespace Scripts.Checker;

internal static class TypeRelations
{
    public static bool IsNever(ScriptType type) => type is PrimitiveType { Name: "never" };

    public static bool IsUnit(ScriptType type) => type is PrimitiveType { Name: "unit" };

    /// <summary>True when a value of type <paramref name="actual"/> may be used where <paramref name="expected"/> is required.</summary>
    public static bool IsCompatible(ScriptType expected, ScriptType actual)
    {
        if (expected is ErrorType || actual is ErrorType || IsNever(actual))
        {
            return true;
        }

        return (expected, actual) switch
        {
            (ListType expectedList, ListType actualList) => IsCompatible(expectedList.Element, actualList.Element),
            (MapType expectedMap, MapType actualMap) => IsCompatible(expectedMap.Key, actualMap.Key) && IsCompatible(expectedMap.Value, actualMap.Value),
            (ResultType expectedResult, ResultType actualResult) =>
                IsCompatible(expectedResult.Success, actualResult.Success) && IsCompatible(expectedResult.Failure, actualResult.Failure),
            (FunctionType expectedFunction, FunctionType actualFunction) => AreFunctionsCompatible(expectedFunction, actualFunction),
            _ => expected.Equals(actual),
        };
    }

    public static bool IsSame(ScriptType left, ScriptType right) => IsCompatible(left, right) && IsCompatible(right, left);

    public static bool ContainsTypeVariable(ScriptType type) =>
        type switch
        {
            TypeVariable => true,
            ListType list => ContainsTypeVariable(list.Element),
            MapType map => ContainsTypeVariable(map.Key) || ContainsTypeVariable(map.Value),
            ResultType result => ContainsTypeVariable(result.Success) || ContainsTypeVariable(result.Failure),
            FunctionType function => function.Parameters.Any(ContainsTypeVariable) || ContainsTypeVariable(function.Return),
            _ => false,
        };

    /// <summary>
    /// True when <paramref name="type"/> uses <c>unit</c> in a position DESIGN §3.1 forbids — element of
    /// <c>list</c>/<c>map</c>, map key, result failure, function parameter — mirroring the written-annotation
    /// check (<c>resolvedType</c>). <c>unit</c> stays allowed at the top level and as a result success
    /// (<c>result&lt;unit, E&gt;</c>). Guards inferred stdlib return types, e.g.
    /// <c>list&lt;result&lt;T,E&gt;&gt;.collect()</c> with <c>T = unit</c>.
    /// </summary>
    public static bool HasForbiddenUnit(ScriptType type, bool unitAllowed = true) =>
        type switch
        {
            PrimitiveType { Name: "unit" } => !unitAllowed,
            ListType list => HasForbiddenUnit(list.Element, unitAllowed: false),
            MapType map => HasForbiddenUnit(map.Key, unitAllowed: false) || HasForbiddenUnit(map.Value, unitAllowed: false),
            ResultType result => HasForbiddenUnit(result.Success, unitAllowed: true) || HasForbiddenUnit(result.Failure, unitAllowed: false),
            FunctionType function => function.Parameters.Any(parameter => HasForbiddenUnit(parameter, unitAllowed: false))
                || HasForbiddenUnit(function.Return, unitAllowed: true),
            _ => false,
        };

    /// <summary>
    /// True when <paramref name="type"/> is a function or the opaque <c>file_lock</c>, or contains one in a list
    /// element, a map key or value, or a result success or failure. Such a value has no JSON rendering (DESIGN §8.4),
    /// so a top-level return cannot carry it.
    /// </summary>
    public static bool ContainsNonJsonType(ScriptType type) =>
        type switch
        {
            FunctionType => true,
            NamedType { Name: "file_lock" } => true,
            ListType list => ContainsNonJsonType(list.Element),
            MapType map => ContainsNonJsonType(map.Key) || ContainsNonJsonType(map.Value),
            ResultType result => ContainsNonJsonType(result.Success) || ContainsNonJsonType(result.Failure),
            _ => false,
        };

    /// <summary>Lists, maps and results hide a failure that must be handled; function types do not.</summary>
    public static bool ContainsResult(ScriptType type) =>
        type switch
        {
            ResultType => true,
            ListType list => ContainsResult(list.Element),
            MapType map => ContainsResult(map.Value),
            _ => false,
        };

    public static bool IsEquatable(ScriptType type) =>
        type switch
        {
            PrimitiveType { Name: "string" or "integer" or "float" or "boolean" or "path" } => true,
            ListType list => IsEquatable(list.Element),
            MapType map => IsEquatable(map.Key) && IsEquatable(map.Value),
            ErrorType => true,
            _ => false,
        };

    public static bool IsOrdered(ScriptType type) =>
        type is PrimitiveType { Name: "string" or "integer" or "float" or "path" } or ErrorType;

    public static bool IsValidMapKey(ScriptType type) =>
        type is PrimitiveType { Name: "string" or "integer" or "path" or "boolean" } or ErrorType;

    public static bool IsInterpolable(ScriptType type) =>
        type is PrimitiveType { Name: "string" or "integer" or "float" or "boolean" or "path" } or ErrorType;

    public static bool IsMatchable(ScriptType type) =>
        type is PrimitiveType { Name: "string" or "integer" or "boolean" } or ResultType or ErrorType;

    /// <summary>Receiver kind used by <see cref="StdlibCatalog.FindMethod"/>, or null when the type has no methods.</summary>
    public static string? ReceiverKindOf(ScriptType type) =>
        type switch
        {
            PrimitiveType { Name: "string" } => "string",
            PrimitiveType { Name: "integer" } => "integer",
            PrimitiveType { Name: "float" } => "float",
            PrimitiveType { Name: "path" } => "path",
            ListType => "list",
            MapType => "map",
            ResultType => "result",
            NamedType { Name: "process_result" } => "process_result",
            NamedType { Name: "pipeline_result" } => "pipeline_result",
            NamedType { Name: "run_request" } => "run_request",
            NamedType { Name: "json_value" } => "json_value",
            NamedType { Name: "file_lock" } => "file_lock",
            _ => null,
        };

    public static ScriptType ReplaceTypeVariablesWithError(ScriptType type) =>
        type switch
        {
            TypeVariable => ErrorType.Instance,
            ListType list => new ListType(ReplaceTypeVariablesWithError(list.Element)),
            MapType map => new MapType(ReplaceTypeVariablesWithError(map.Key), ReplaceTypeVariablesWithError(map.Value)),
            ResultType result => new ResultType(ReplaceTypeVariablesWithError(result.Success), ReplaceTypeVariablesWithError(result.Failure)),
            FunctionType function => new FunctionType([.. function.Parameters.Select(ReplaceTypeVariablesWithError)], ReplaceTypeVariablesWithError(function.Return)),
            _ => type,
        };

    private static bool AreFunctionsCompatible(FunctionType expected, FunctionType actual)
    {
        if (expected.Parameters.Count != actual.Parameters.Count || !IsCompatible(expected.Return, actual.Return))
        {
            return false;
        }

        for (var position = 0; position < expected.Parameters.Count; position++)
        {
            if (!IsSame(expected.Parameters[position], actual.Parameters[position]))
            {
                return false;
            }
        }

        return true;
    }
}
