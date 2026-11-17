using Scripts.Core;

namespace Scripts.Checker;

/// <summary>Function being checked: its name and declared return type (null function context means the script top level).</summary>
internal sealed class FunctionContext(string name, ScriptType returnType)
{
    public string Name { get; } = name;

    public ScriptType ReturnType { get; } = returnType;
}

/// <summary>Loop being checked; a `while true` without `break` never completes (DESIGN 4.3).</summary>
internal sealed class LoopContext(Span body)
{
    /// <summary>The body span, used to tell a loop-local binding from the accumulator of SCRIPTS-LINT-0012.</summary>
    public Span Body { get; } = body;

    public bool HasBreak { get; set; }
}

/// <summary>Whether control can reach the statement that follows, and what stops it (TYPE-0052, TYPE-0053).</summary>
internal readonly record struct Flow(bool Diverges, string Description)
{
    public static Flow Continues => new(false, string.Empty);
}
