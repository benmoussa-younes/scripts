using Scripts.Core;

namespace Scripts.Runtime.Stdlib;

internal static class SystemOutputFunctions
{
    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["io.write_standard_output_line"] = Print;
        table["io.write_standard_error_line"] = PrintError;
        table["io.write_standard_error"] = WriteError;
        table["assert"] = Assert;
    }

    private static Value Print(RuntimeContext context, Value[] arguments)
    {
        context.StandardOutput.Write(SystemValues.TextAt(arguments, 0));
        context.StandardOutput.Write("\n");
        return UnitValue.Instance;
    }

    private static Value PrintError(RuntimeContext context, Value[] arguments)
    {
        context.StandardOutput.Flush();
        context.StandardError.Write(SystemValues.TextAt(arguments, 0));
        context.StandardError.Write("\n");
        context.StandardError.Flush();
        return UnitValue.Instance;
    }

    private static Value WriteError(RuntimeContext context, Value[] arguments)
    {
        context.StandardOutput.Flush();
        context.StandardError.Write(SystemValues.TextAt(arguments, 0));
        context.StandardError.Flush();
        return UnitValue.Instance;
    }

    /// <summary>
    /// assert(condition, message): returns unit when the invariant holds; otherwise stops the run with
    /// SCRIPTS-RUNTIME-0021 and the script's own message (SPEC-STDLIB, no call folding: a literal false fails at run time).
    /// </summary>
    private static Value Assert(RuntimeContext context, Value[] arguments)
    {
        if (!((BooleanValue)arguments[0]).Flag)
        {
            throw new RuntimeFailure(ErrorCodes.Runtime0021, ("message", SystemValues.TextAt(arguments, 1)));
        }

        return UnitValue.Instance;
    }
}
