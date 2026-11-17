namespace Scripts.Runtime;

/// <summary>Buffered text sink for io.write_standard_output_line/io.write_standard_error_line. Implementations are provided by the output layer.</summary>
public interface IOutputChannel
{
    /// <summary>Writes text as UTF-8 (no BOM). Raises BrokenPipeTermination on EPIPE and RuntimeFailure(SCRIPTS-RUNTIME-0019) on any other write error.</summary>
    void Write(string text);

    void Flush();
}

/// <summary>Services shared by every stdlib implementation. The interpreter creates one per run.</summary>
public sealed partial class RuntimeContext
{
    private readonly List<FunctionValue> deferredActions = [];

    /// <summary>Set by the interpreter: calls a user function/lambda (counts toward the call depth limit, checks for interruption).</summary>
    public Func<FunctionValue, IReadOnlyList<Value>, Value>? FunctionInvoker { get; set; }

    /// <summary>
    /// Calls <paramref name="function"/> with <paramref name="arguments"/>.
    /// </summary>
    /// <remarks>
    /// The <see cref="FunctionInvoker"/> implementation may <b>reuse the
    /// <paramref name="arguments"/> array in place</b> (replacing its elements with their deep copies) to avoid one
    /// allocation per call. The caller must therefore <b>never</b> share this array: in normal use it comes
    /// from <c>params</c>, hence fresh on each call. Passing a persistent array would corrupt the caller.
    /// </remarks>
    public Value Invoke(FunctionValue function, params Value[] arguments) =>
        (FunctionInvoker ?? throw new InvalidOperationException("FunctionInvoker not initialized"))(function, arguments);

    /// <summary>
    /// Registers <paramref name="action"/> for the end of the script (`process.defer`). Actions are invoked in reverse
    /// registration order (LIFO) by the interpreter, on every termination path, after <c>Shutdown()</c>'s flush.
    /// </summary>
    public void RegisterDeferredAction(FunctionValue action) => deferredActions.Add(action);

    /// <summary>
    /// True when a <c>process.defer</c> action caught a <see cref="BrokenPipeTermination"/> (EPIPE). EPIPE is not a
    /// failure (the <c>io.write_standard_output</c>/<c>io.write_standard_output_line</c> contract makes it a silent 141), so the deferred-action chain keeps
    /// running and the CLI maps this flag to 141 only when the run itself was a success termination.
    /// </summary>
    public bool DeferredBrokenPipe { get; private set; }

    /// <summary>Records an EPIPE swallowed by a deferred action; keeps the remaining LIFO actions running.</summary>
    internal void RecordDeferredBrokenPipe() => DeferredBrokenPipe = true;

    /// <summary>The pending deferred actions in LIFO order, consuming the list; empty when none was registered.</summary>
    internal IReadOnlyList<FunctionValue> TakeDeferredActionsInReverseOrder()
    {
        if (deferredActions.Count == 0)
        {
            return [];
        }

        var actions = new List<FunctionValue>(deferredActions);
        actions.Reverse();
        deferredActions.Clear();
        return actions;
    }

    /// <summary>
    /// Suspends signal interruption for the duration of the returned scope (the deferred-action drain). A pending signal
    /// stays recorded — the run already reports it — but no longer aborts deferred cleaning that calls the stdlib.
    /// </summary>
    internal IDisposable SuspendingInterruptionForDeferredActions() => Interruption.Suspend();

    /// <summary>
    /// Allows subprocesses launched by deferred cleanup actions to run: after a signal, the child table would otherwise
    /// kill any child created during the drain. Called once when the drain starts.
    /// </summary>
    internal void AllowChildrenForDeferredActions() => ChildProcesses.AllowChildrenForDeferredActions();
}

/// <summary>Stdlib implementation: arguments are the receiver first (methods only) then the parameters in catalog order.</summary>
public delegate Value StdlibImplementation(RuntimeContext context, Value[] arguments);

/// <summary>Table from StdlibEntry.QualifiedName (e.g. "filesystem.read_file", "path.join", "list&lt;T&gt;.filter") to its implementation.</summary>
public static class StdlibRegistry
{
    private static readonly Lazy<Dictionary<string, StdlibImplementation>> Table = new(Build);

    public static StdlibImplementation Get(string qualifiedName) =>
        Table.Value.TryGetValue(qualifiedName, out var implementation)
            ? implementation
            : throw new InvalidOperationException($"stdlib function without implementation: {qualifiedName}");

    public static IReadOnlyCollection<string> ImplementedNames => Table.Value.Keys;

    private static Dictionary<string, StdlibImplementation> Build()
    {
        var table = new Dictionary<string, StdlibImplementation>(StringComparer.Ordinal);
        PureStdlib.Register(table);
        SystemStdlib.Register(table);
        return table;
    }
}
