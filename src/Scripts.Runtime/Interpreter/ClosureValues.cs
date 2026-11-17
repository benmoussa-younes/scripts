using Scripts.Core;

namespace Scripts.Runtime.Interpreter;

/// <summary>Declared function (top-level or nested) together with the scope it closes over.</summary>
internal sealed class UserFunctionValue(string name, IReadOnlyList<Parameter> parameters, BlockStatement body, Scope closureScope) : FunctionValue
{
    public string Name { get; } = name;

    public IReadOnlyList<Parameter> Parameters { get; } = parameters;

    public BlockStatement Body { get; } = body;

    public Scope ClosureScope { get; } = closureScope;

    public override int Arity => Parameters.Count;
}

internal sealed class LambdaValue(IReadOnlyList<LambdaParameter> parameters, Expression body, Scope closureScope) : FunctionValue
{
    public IReadOnlyList<LambdaParameter> Parameters { get; } = parameters;

    public Expression Body { get; } = body;

    public Scope ClosureScope { get; } = closureScope;

    public override int Arity => Parameters.Count;
}
