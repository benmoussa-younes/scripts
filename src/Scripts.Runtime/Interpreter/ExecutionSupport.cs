using Scripts.Core;

namespace Scripts.Runtime.Interpreter;

/// <summary>Completion of a statement; break, continue and return unwind without exceptions.</summary>
internal enum ExecutionStatus
{
    Normal,
    Break,
    Continue,
    Return,
}

/// <summary>How a value is prepared before it is stored (value semantics, DESIGN 3.6).</summary>
internal enum CopyMode
{
    /// <summary>The value is only read or discarded.</summary>
    None,

    /// <summary>The value goes into a mutable variable or a container: no other place may share it.</summary>
    Owned,

    /// <summary>The value goes into an immutable binding: sharing is fine when its source can never change.</summary>
    ForImmutableBinding,
}

/// <summary>Raised by a nested <c>?</c> on an error result; caught by the enclosing function call.</summary>
internal sealed class PropagationSignal(ResultValue errorResult) : Exception("error propagation")
{
    public ResultValue ErrorResult { get; } = errorResult;
}

/// <summary>
/// Raised by a `return`/`break`/`continue` inside a block arm of a `match` expression (L1): the expression cannot
/// return a status, so the signal unwinds to the statement that evaluates it, which completes with <see cref="Status"/>.
/// The checker rejects these keywords in a lambda (TYPE-0073), so the signal never crosses a function value.
/// </summary>
internal sealed class ControlSignal(ExecutionStatus status) : Exception("control flow")
{
    public ExecutionStatus Status { get; } = status;
}

/// <summary>Stdlib entry resolved once per call site; IsMethod tells whether the first argument is the receiver.</summary>
internal sealed record ResolvedStandardCall(StdlibEntry Entry, StdlibImplementation Implementation, bool IsMethod)
{
    public bool MutatesReceiver => IsMethod && Entry.Name is "append" or "remove_key" or "insert_at" or "remove_at";
}
