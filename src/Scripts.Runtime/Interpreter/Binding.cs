namespace Scripts.Runtime.Interpreter;

/// <summary>Storage cell of a variable; closures capture the cell, so later assignments are visible to them.</summary>
internal sealed class Binding(Value value, bool isMutable)
{
    public Value Value { get; set; } = value;

    public bool IsMutable { get; } = isMutable;
}
