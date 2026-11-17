namespace Scripts.Runtime.Stdlib;

/// <summary>
/// Implementations of the intrinsic <c>io</c> and <c>process</c> namespaces (DESIGN §1.5). They are registered under
/// the catalog name (<c>io.read_standard_input</c>, <c>process.defer</c>); the namespaces are always available.
/// </summary>
internal static class StandardModuleFunctions
{
    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["io.read_standard_input"] = ReadStandardInput;
        table["io.read_standard_input_line"] = ReadLine;
        table["io.write_standard_output"] = Write;
        table["process.defer"] = Defer;
    }

    /// <summary>
    /// Reads the rest of stdin through the run's shared reader, like filesystem.read_file but on file descriptor 0; invalid bytes
    /// give error(invalid_encoding) and an absent descriptor is an empty input.
    /// </summary>
    private static Value ReadStandardInput(RuntimeContext context, Value[] arguments)
    {
        // The prompt (if any) must reach the terminal before the read blocks, as `exec`/`process.run_shell` do.
        context.StandardOutput.Flush();
        return SystemValues.GuardIo(() => SystemValues.Success(new StringValue(context.StandardInput.ReadToEnd())));
    }

    /// <summary>Reads one line through the same shared reader; EOF with no bytes is error(end_of_file).</summary>
    private static Value ReadLine(RuntimeContext context, Value[] arguments)
    {
        context.StandardOutput.Flush();
        return SystemValues.GuardIo(() => SystemValues.Success(new StringValue(context.StandardInput.ReadLine())));
    }

    /// <summary>
    /// Writes the text to stdout without a newline, through the same buffered channel as io.write_standard_output_line: no flush of its own,
    /// so the existing flush points (read_line/read_standard_input/eprint/process.run_inherit/process.run_shell/exit/signal) keep their order.
    /// </summary>
    private static Value Write(RuntimeContext context, Value[] arguments)
    {
        context.StandardOutput.Write(SystemValues.TextAt(arguments, 0));
        return UnitValue.Instance;
    }

    /// <summary>Registers a function to run at the end of the script (LIFO), on every termination path.</summary>
    private static Value Defer(RuntimeContext context, Value[] arguments)
    {
        context.RegisterDeferredAction((FunctionValue)arguments[0]);
        return UnitValue.Instance;
    }
}
