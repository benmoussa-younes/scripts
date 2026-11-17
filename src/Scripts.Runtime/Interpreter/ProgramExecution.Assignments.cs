using System.Globalization;
using Scripts.Core;

namespace Scripts.Runtime.Interpreter;

internal sealed partial class ProgramExecution
{
    private ExecutionStatus ExecuteAssignment(AssignmentStatement assignment, Scope scope)
    {
        var binding = scope.Find(assignment.TargetName) ?? throw InternalError($"unknown variable {assignment.TargetName}");
        if (assignment.Indexes.Count > 0)
        {
            return ExecuteIndexedAssignment(assignment, binding, scope);
        }

        if (assignment.Operator == AssignOperator.Assign)
        {
            if (!TryEvaluateStatementValue(assignment.Value, scope, CopyMode.Owned, out var assigned))
            {
                return ExecutionStatus.Return;
            }

            binding.Value = assigned;
            return ExecutionStatus.Normal;
        }

        var current = binding.Value;
        // `x op= e` reads x before e (DESIGN §3.6): a side-effecting e may mutate the binding through a nested
        // function, so the already-read value must stay independent of that mutation. Strings join lists here
        // because a `+=` may grow an append buffer in place; a safe RHS keeps the buffer and stays linear.
        if (current is ListValue or StringValue && MayHaveSideEffects(assignment.Value))
        {
            current = current.DeepCopy();
        }

        if (!TryEvaluateStatementValue(assignment.Value, scope, CopyMode.None, out var operand))
        {
            return ExecutionStatus.Return;
        }

        if (current is StringValue stringValue && assignment.Operator == AssignOperator.AddAssign)
        {
            // `x += y`: if the binding's value already carries an append buffer, it is provably private (reading a
            // variable anywhere retires the buffer via EvaluateIdentifier), so the append is safe and linear. When it
            // has no buffer, the concatenation below produces a fresh value that is then buffered for the next turns;
            // this never mutates `current`, which may be aliased (e.g. bound by a `match` pattern).
            binding.Value = stringValue.HasAppendBuffer
                ? StringValue.Concatenate(stringValue, (StringValue)operand)
                : StringValue.StartAppendBuffer(StringValue.Concatenate(stringValue, (StringValue)operand));
            return ExecutionStatus.Normal;
        }

        binding.Value = ApplyBinary(ToBinaryOperator(assignment.Operator), current, operand);
        return ExecutionStatus.Normal;
    }

    /// <summary>c[i]...[j] op= e: c and the indexes are evaluated, then the target is read (compound only), then e, then the store.</summary>
    private ExecutionStatus ExecuteIndexedAssignment(AssignmentStatement assignment, Binding binding, Scope scope)
    {
        var indexValues = new Value[assignment.Indexes.Count];
        for (var position = 0; position < indexValues.Length; position++)
        {
            indexValues[position] = Evaluate(assignment.Indexes[position], scope);
        }

        var parent = binding.Value;
        for (var level = 0; level < indexValues.Length - 1; level++)
        {
            parent = ReadIndex(parent, indexValues[level]);
        }

        var lastIndex = indexValues[^1];
        Value newValue;
        if (assignment.Operator == AssignOperator.Assign)
        {
            if (!TryEvaluateStatementValue(assignment.Value, scope, CopyMode.Owned, out newValue))
            {
                return ExecutionStatus.Return;
            }
        }
        else
        {
            var current = ReadIndex(parent, lastIndex);
            if (current is ListValue or MapValue && MayHaveSideEffects(assignment.Value))
            {
                current = current.DeepCopy();
            }

            if (!TryEvaluateStatementValue(assignment.Value, scope, CopyMode.None, out var operand))
            {
                return ExecutionStatus.Return;
            }

            newValue = ApplyBinary(ToBinaryOperator(assignment.Operator), current, operand);
        }

        StoreIndex(parent, lastIndex, newValue);
        return ExecutionStatus.Normal;
    }

    private static void StoreIndex(Value container, Value key, Value newValue)
    {
        switch (container)
        {
            case ListValue list when key is IntegerValue position:
                if (position.Number < 0 || position.Number >= list.Items.Count)
                {
                    throw new RuntimeFailure(
                        ErrorCodes.Runtime0005,
                        ("index", position.Number.ToString(CultureInfo.InvariantCulture)),
                        ("length", list.Items.Count.ToString(CultureInfo.InvariantCulture)));
                }

                list.Items[(int)position.Number] = newValue;
                break;
            case MapValue map:
                if (!map.ContainsKey(key))
                {
                    SizeLimits.EnsureListLength((long)map.Count + 1);
                }

                // The key is copied like every other store point (DESIGN §3.6): `m[s] = v` must not alias a
                // mutable value that the script later reassigns (audit C3, consistency with EvaluateMap).
                map.Set(key.DeepCopy(), newValue);
                break;
            default:
                throw InternalError("index assignment on a value that is not a list or a map");
        }
    }

    private static BinaryOperator ToBinaryOperator(AssignOperator assignOperator) => assignOperator switch
    {
        AssignOperator.AddAssign => BinaryOperator.Add,
        AssignOperator.SubtractAssign => BinaryOperator.Subtract,
        AssignOperator.MultiplyAssign => BinaryOperator.Multiply,
        AssignOperator.DivideAssign => BinaryOperator.Divide,
        AssignOperator.ModuloAssign => BinaryOperator.Modulo,
        _ => throw InternalError($"unsupported assignment operator {assignOperator}"),
    };
}
