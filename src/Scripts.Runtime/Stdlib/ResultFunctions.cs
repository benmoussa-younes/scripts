using Scripts.Core;
using static Scripts.Runtime.ArgumentAccess;

namespace Scripts.Runtime;

/// <summary>The 4 result methods (SPEC-STDLIB 7.9).</summary>
internal static class ResultFunctions
{
    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["result<T,E>.is_ok"] = (context, arguments) => BooleanValue.From(Result(arguments, 0).IsOk);
        table["result<T,E>.value_or"] = UnwrapOr;
        table["result<T,E>.value_or_fail"] = Unwrap;
        table["result<T,E>.transform_error"] = MapError;
    }

    private static Value UnwrapOr(RuntimeContext context, Value[] arguments)
    {
        var result = Result(arguments, 0);
        return result.IsOk ? result.Payload.DeepCopy() : arguments[1];
    }

    private static Value Unwrap(RuntimeContext context, Value[] arguments)
    {
        var result = Result(arguments, 0);
        if (!result.IsOk)
        {
            throw new RuntimeFailure(ErrorCodes.Runtime0007, ("error_description", ErrorValues.Describe(result.Payload)));
        }

        return result.Payload.DeepCopy();
    }

    private static Value MapError(RuntimeContext context, Value[] arguments)
    {
        var result = Result(arguments, 0);
        return result.IsOk ? result.DeepCopy() : ResultValue.Error(context.Invoke(Function(arguments, 1), result.Payload));
    }
}
