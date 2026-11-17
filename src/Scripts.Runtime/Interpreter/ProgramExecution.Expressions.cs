using System.Globalization;
using System.Text;
using Scripts.Core;

namespace Scripts.Runtime.Interpreter;

internal sealed partial class ProgramExecution
{
    /// <summary>Evaluates to the value without copying: the result may alias a variable, see Prepare.</summary>
    private Value Evaluate(Expression expression, Scope scope)
    {
        switch (expression)
        {
            case IdentifierExpression identifier:
                return EvaluateIdentifier(identifier, scope);
            case IntegerLiteral integer:
                return IntegerValue.From(integer.Value);
            case BinaryExpression binary:
                return EvaluateBinary(binary, scope);
            case CallExpression call:
                return EvaluateCall(call, scope);
            case StringLiteral text:
                return new StringValue(text.Value);
            case MemberAccessExpression member:
                return EvaluateMemberAccess(member, scope);
            case IndexExpression index:
                return EvaluateIndex(index, scope);
            case BooleanLiteral boolean:
                return BooleanValue.From(boolean.Value);
            case UnaryExpression unary:
                return EvaluateUnary(unary, scope);
            case InterpolatedString interpolated:
                return new StringValue(RenderParts(interpolated.Parts, scope));
            case PathLiteral path:
                return new PathValue(RenderParts(path.Parts, scope));
            case FloatLiteral number:
                return new FloatValue(number.Value);
            case PropagateExpression propagate:
                return EvaluatePropagate(propagate, scope);
            case ListLiteral list:
                return EvaluateList(list, scope);
            case MapLiteral map:
                return EvaluateMap(map, scope);
            case LambdaExpression lambda:
                return new LambdaValue(lambda.Parameters, lambda.Body, scope);
            case OkExpression okExpression:
                return ResultValue.Ok(okExpression.Value is null ? UnitValue.Instance : EvaluateOwned(okExpression.Value, scope));
            case ErrorExpression errorExpression:
                return ResultValue.Error(EvaluateOwned(errorExpression.Value, scope));
            case ErrorConstructorExpression errorConstructor:
                return EvaluateErrorConstructor(errorConstructor, scope);
            case MatchExpression match:
                return EvaluateMatchExpression(match, scope);
            default:
                throw InternalError($"unsupported expression {expression.GetType().Name}");
        }
    }

    private Value EvaluateErrorConstructor(ErrorConstructorExpression constructor, Scope scope)
    {
        var kind = ((StringValue)EvaluateOwned(constructor.Kind, scope)).Text;
        var subject = ((StringValue)EvaluateOwned(constructor.Subject, scope)).Text;
        var message = ((StringValue)EvaluateOwned(constructor.Message, scope)).Text;
        var error = ErrorValues.Error(kind, subject, message);
        return constructor.ResolvedType is ResultType ? ResultValue.Error(error) : error;
    }

    private Value EvaluateIdentifier(IdentifierExpression identifier, Scope scope)
    {
        var binding = scope.Find(identifier.Name);
        if (binding is not null)
        {
            // Reading a variable makes its string observable through another name, so any in-place append buffer is
            // retired here (the value stays as is, only future growth becomes copy-based). The compound-assignment
            // code path reads binding.Value directly, precisely so `x += y` can keep growing the buffer.
            binding.Value.RetireBufferRoot();
            return binding.Value;
        }

        if (scope.FileEnvironment.Functions.TryGetValue(identifier.Name, out var function))
        {
            return function;
        }

        throw InternalError($"unknown identifier {identifier.Name}");
    }

    private string RenderParts(IReadOnlyList<StringPart> parts, Scope scope)
    {
        if (parts.Count == 1 && parts[0] is TextPart onlyText)
        {
            return onlyText.Text;
        }

        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (part is TextPart text)
            {
                builder.Append(text.Text);
            }
            else if (part is InterpolationPart interpolation)
            {
                builder.Append(TextRenderer.Render(Evaluate(interpolation.Expression, scope)));
            }

            SizeLimits.EnsureStringLength(builder.Length);
        }

        return builder.ToString();
    }

    private ListValue EvaluateList(ListLiteral list, Scope scope)
    {
        var items = new List<Value>(list.Elements.Count);
        for (var position = 0; position < list.Elements.Count; position++)
        {
            items.Add(EvaluateOwned(list.Elements[position], scope));
        }

        return new ListValue(items);
    }

    private MapValue EvaluateMap(MapLiteral map, Scope scope)
    {
        var result = new MapValue();
        foreach (var entry in map.Entries)
        {
            // A map key is copied when it gets a second owner (DESIGN 3.6), like a list element: a mutable value used
            // as a key must never be observed mutated through its variable afterwards.
            var key = Evaluate(entry.Key, scope).DeepCopy();
            result.Set(key, EvaluateOwned(entry.Value, scope));
        }

        return result;
    }

    private Value EvaluateUnary(UnaryExpression unary, Scope scope) => unary.Operator switch
    {
        UnaryOperator.Negate => ArithmeticOperations.Negate(Evaluate(unary.Operand, scope)),
        UnaryOperator.Not => BooleanValue.From(!EvaluateCondition(unary.Operand, scope)),
        _ => throw InternalError($"unsupported unary operator {unary.Operator}"),
    };

    private Value EvaluateBinary(BinaryExpression binary, Scope scope)
    {
        switch (binary.Operator)
        {
            case BinaryOperator.And:
                return BooleanValue.From(EvaluateCondition(binary.Left, scope) && EvaluateCondition(binary.Right, scope));
            case BinaryOperator.Or:
                return BooleanValue.From(EvaluateCondition(binary.Left, scope) || EvaluateCondition(binary.Right, scope));
        }

        var left = Evaluate(binary.Left, scope);
        if (left is ListValue or MapValue)
        {
            left = SnapshotBeforeLaterEvaluation(binary.Left, left, binary.Right, scope);
        }

        return ApplyBinary(binary.Operator, left, Evaluate(binary.Right, scope));
    }

    private static Value ApplyBinary(BinaryOperator binaryOperator, Value left, Value right) => binaryOperator switch
    {
        BinaryOperator.Add => ArithmeticOperations.Add(left, right),
        BinaryOperator.Subtract => ArithmeticOperations.Subtract(left, right),
        BinaryOperator.Multiply => ArithmeticOperations.Multiply(left, right),
        BinaryOperator.Divide => ArithmeticOperations.Divide(left, right),
        BinaryOperator.Modulo => ArithmeticOperations.Remainder(left, right),
        BinaryOperator.Equal => BooleanValue.From(ValueComparison.AreEqual(left, right)),
        BinaryOperator.NotEqual => BooleanValue.From(!ValueComparison.AreEqual(left, right)),
        BinaryOperator.Less => BooleanValue.From(ValueComparison.Compare(left, right) < 0),
        BinaryOperator.LessOrEqual => BooleanValue.From(ValueComparison.Compare(left, right) <= 0),
        BinaryOperator.Greater => BooleanValue.From(ValueComparison.Compare(left, right) > 0),
        BinaryOperator.GreaterOrEqual => BooleanValue.From(ValueComparison.Compare(left, right) >= 0),
        _ => throw InternalError($"unsupported binary operator {binaryOperator}"),
    };

    private Value EvaluateMemberAccess(MemberAccessExpression member, Scope scope)
    {
        if (member.Target is IdentifierExpression qualifier
            && scope.FileEnvironment.Imports.TryGetValue(qualifier.Name, out var module))
        {
            return module.ConstantScope.TryGetLocal(member.MemberName, out var constant)
                ? constant.Value
                : throw InternalError($"unknown constant {qualifier.Name}.{member.MemberName}");
        }

        return Evaluate(member.Target, scope) is RecordValue record
            ? record.Get(member.MemberName)
            : throw InternalError($"field access .{member.MemberName} on a value that is not a record");
    }

    private Value EvaluateIndex(IndexExpression index, Scope scope)
    {
        var container = Evaluate(index.Target, scope);
        container = SnapshotBeforeLaterEvaluation(index.Target, container, index.Index, scope);
        return ReadIndex(container, Evaluate(index.Index, scope));
    }

    private static Value ReadIndex(Value container, Value key)
    {
        switch (container)
        {
            case ListValue list when key is IntegerValue position:
                if (position.Number < 0 || position.Number >= list.Items.Count)
                {
                    throw new RuntimeFailure(
                        ErrorCodes.Runtime0004,
                        ("index", position.Number.ToString(CultureInfo.InvariantCulture)),
                        ("length", list.Items.Count.ToString(CultureInfo.InvariantCulture)));
                }

                return list.Items[(int)position.Number];
            case MapValue map:
                return map.TryGet(key, out var value)
                    ? value
                    : throw new RuntimeFailure(ErrorCodes.Runtime0006, ("key", TextRenderer.Render(key)));
            default:
                throw InternalError("index applied to a value that is not a list or a map");
        }
    }

    private Value EvaluatePropagate(PropagateExpression propagate, Scope scope)
    {
        var state = State;
        var result = AsResult(Evaluate(propagate.Operand, scope));
        if (result.IsOk)
        {
            return result.Payload;
        }

        if (state.CallDepth == 0)
        {
            throw new RuntimeFailure(ErrorCodes.Runtime0017, ("error_description", ErrorValues.Describe(result.Payload)));
        }

        throw new PropagationSignal((ResultValue)Prepare(propagate.Operand, result, CopyMode.Owned, scope));
    }
}
