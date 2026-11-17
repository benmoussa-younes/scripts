using Scripts.Core;

namespace Scripts.Runtime.Interpreter;

/// <summary>
/// Value semantics (DESIGN 3.6). Invariant: a container reachable from a mutable variable or stored in another container has exactly one owner.
/// Containers held by immutable bindings are never mutated, so they may be shared between immutable bindings.
/// </summary>
internal sealed partial class ProgramExecution
{
    private Value EvaluateOwned(Expression expression, Scope scope) =>
        Prepare(expression, Evaluate(expression, scope), CopyMode.Owned, scope);

    private Value Prepare(Expression expression, Value value, CopyMode mode, Scope scope)
    {
        if (mode == CopyMode.None || !HoldsMutableContainer(value) || IsFresh(expression))
        {
            return value;
        }

        if (mode == CopyMode.ForImmutableBinding && RootIsImmutable(expression, scope))
        {
            return value;
        }

        return value.DeepCopy();
    }

    /// <summary>Gives a later-evaluated operand with side effects a stable view of an already evaluated container (left-to-right order, DESIGN 3.6).</summary>
    private static Value SnapshotBeforeLaterEvaluation(Expression operand, Value value, Expression laterExpression, Scope scope) =>
        value is ListValue or MapValue && !IsFresh(operand) && MayHaveSideEffects(laterExpression) && !RootIsImmutable(operand, scope)
            ? value.DeepCopy()
            : value;

    private static Value SnapshotBeforeLaterEvaluation(Expression operand, Value value, IReadOnlyList<Expression> laterExpressions, Scope scope)
    {
        if (value is not (ListValue or MapValue) || IsFresh(operand) || RootIsImmutable(operand, scope))
        {
            return value;
        }

        foreach (var laterExpression in laterExpressions)
        {
            if (MayHaveSideEffects(laterExpression))
            {
                return value.DeepCopy();
            }
        }

        return value;
    }

    private static Value SnapshotBeforeLaterEvaluation(Expression operand, Value value, IReadOnlyList<CallArgument> laterArguments, Scope scope)
    {
        if (value is not (ListValue or MapValue) || IsFresh(operand) || RootIsImmutable(operand, scope))
        {
            return value;
        }

        foreach (var laterArgument in laterArguments)
        {
            if (MayHaveSideEffects(laterArgument.Expression))
            {
                return value.DeepCopy();
            }
        }

        return value;
    }

    private static bool HoldsMutableContainer(Value value) => value switch
    {
        // A string may grow an append buffer in place after a compound assignment, so it is treated like a
        // container for aliasing: every non-fresh, non-immutable source is copied on store.
        StringValue or ListValue or MapValue => true,
        ResultValue result => HoldsMutableContainer(result.Payload),
        // Records (process_result, path_status, …) hold immutable-by-construction fields, but a field could in principle
        // be a buffered string; copying is cheap and keeps the aliasing invariant unconditional.
        RecordValue => true,
        _ => false,
    };

    /// <summary>True when the expression yields a value nobody else references (a temporary).</summary>
    private static bool IsFresh(Expression expression) => expression switch
    {
        IdentifierExpression or IndexExpression or MemberAccessExpression => false,
        PropagateExpression propagate => IsFresh(propagate.Operand),
        _ => true,
    };

    private static bool RootIsImmutable(Expression expression, Scope scope) => expression switch
    {
        IdentifierExpression identifier => scope.Find(identifier.Name) is not { IsMutable: true },
        IndexExpression index => RootIsImmutable(index.Target, scope),
        MemberAccessExpression member => (member.Target is IdentifierExpression qualifier && scope.FileEnvironment.Imports.ContainsKey(qualifier.Name))
            || RootIsImmutable(member.Target, scope),
        PropagateExpression propagate => RootIsImmutable(propagate.Operand, scope),
        _ => false,
    };

    private static bool MayHaveSideEffects(Expression expression) => expression switch
    {
        IntegerLiteral or FloatLiteral or BooleanLiteral or StringLiteral or IdentifierExpression or LambdaExpression => false,
        BinaryExpression binary => MayHaveSideEffects(binary.Left) || MayHaveSideEffects(binary.Right),
        UnaryExpression unary => MayHaveSideEffects(unary.Operand),
        IndexExpression index => MayHaveSideEffects(index.Target) || MayHaveSideEffects(index.Index),
        MemberAccessExpression member => MayHaveSideEffects(member.Target),
        ListLiteral list => list.Elements.Any(MayHaveSideEffects),
        MapLiteral map => map.Entries.Any(entry => MayHaveSideEffects(entry.Key) || MayHaveSideEffects(entry.Value)),
        InterpolatedString interpolated => PartsMayHaveSideEffects(interpolated.Parts),
        PathLiteral path => PartsMayHaveSideEffects(path.Parts),
        OkExpression okExpression => okExpression.Value is not null && MayHaveSideEffects(okExpression.Value),
        ErrorExpression errorExpression => MayHaveSideEffects(errorExpression.Value),
        ErrorConstructorExpression errorConstructor => MayHaveSideEffects(errorConstructor.Kind)
            || MayHaveSideEffects(errorConstructor.Subject)
            || MayHaveSideEffects(errorConstructor.Message),
        _ => true,
    };

    private static bool PartsMayHaveSideEffects(IReadOnlyList<StringPart> parts) =>
        parts.Any(part => part is InterpolationPart interpolation && MayHaveSideEffects(interpolation.Expression));
}
