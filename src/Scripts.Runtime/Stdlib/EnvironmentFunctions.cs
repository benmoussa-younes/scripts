using Scripts.Runtime.Platform;
using Scripts.Runtime.Processes;
using static Scripts.Runtime.ArgumentAccess;

namespace Scripts.Runtime;

/// <summary>environment.get_variable, set, remove and all (SPEC-STDLIB 7.4), backed by the process environment.</summary>
internal static class EnvironmentFunctions
{
    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["environment.get_variable"] = Get;
        table["environment.set_variable"] = Set;
        table["environment.remove_variable"] = Remove;
        table["environment.all_variables"] = All;
    }

    private static Value Get(RuntimeContext context, Value[] arguments)
    {
        var name = Text(arguments, 0);
        if (IsInvalidName(name))
        {
            return InvalidNameFailure(name);
        }

        var value = Environment.GetEnvironmentVariable(name);
        if (value is null && ChildEnvironment.TryFindInvalidName(name, out var rawValue))
        {
            // Consistent with environment.all_variables: the lenient spelling is visible, but set/remove reject it below.
            return ResultValue.Ok(new StringValue(NativeText.DecodeLenient(rawValue!)));
        }

        return value is null
            ? ResultValue.Error(ErrorValues.EnvironmentFailure("not_found", name, "missing environment variable"))
            : ResultValue.Ok(new StringValue(value));
    }

    private static Value Set(RuntimeContext context, Value[] arguments)
    {
        var name = Text(arguments, 0);
        var value = Text(arguments, 1);
        if (IsInvalidName(name))
        {
            return InvalidNameFailure(name);
        }

        if (value.Contains('\0'))
        {
            return ResultValue.Error(ErrorValues.EnvironmentFailure("invalid_argument", name, "the value contains the NUL character, which is forbidden"));
        }

        if (ChildEnvironment.TryFindInvalidName(name, out _))
        {
            // The raw name cannot be addressed by a scripts string: never rewrite it under its U+FFFD spelling.
            return InvalidNameFailure(name);
        }

        Environment.SetEnvironmentVariable(name, value);
        return ResultValue.Ok(UnitValue.Instance);
    }

    private static Value Remove(RuntimeContext context, Value[] arguments)
    {
        var name = Text(arguments, 0);
        if (IsInvalidName(name))
        {
            return InvalidNameFailure(name);
        }

        if (ChildEnvironment.TryFindInvalidName(name, out _))
        {
            return InvalidNameFailure(name);
        }

        Environment.SetEnvironmentVariable(name, null);
        return ResultValue.Ok(UnitValue.Instance);
    }

    private static Value All(RuntimeContext context, Value[] arguments)
    {
        var variables = new List<KeyValuePair<string, string>>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            variables.Add(new((string)entry.Key, (string?)entry.Value ?? string.Empty));
        }

        variables.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
        var map = new MapValue();
        foreach (var variable in variables)
        {
            map.Set(new StringValue(variable.Key), new StringValue(variable.Value));
        }

        return map;
    }

    private static bool IsInvalidName(string name) => name.Length == 0 || name.Contains('=') || name.Contains('\0');

    private static ResultValue InvalidNameFailure(string name) =>
        ResultValue.Error(ErrorValues.EnvironmentFailure("invalid_argument", name, "invalid variable name (empty, or containing `=` or NUL)"));
}
