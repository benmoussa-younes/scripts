using Scripts.Core;

namespace Scripts.Checker;

internal enum BinaryFailure
{
    None,
    ForbiddenEquality,
    Unordered,
    DifferentTypes,
    Undefined,
}

/// <summary>Result of typing `left op right`; OffendingType names the operand type for the equality and ordering failures.</summary>
internal readonly record struct BinaryOutcome(BinaryFailure Failure, ScriptType Type, ScriptType OffendingType);

/// <summary>Operator typing matrix of DESIGN 3.2 and the code precedence of.</summary>
internal static class OperatorRules
{
    public static string Text(BinaryOperator binaryOperator) =>
        binaryOperator switch
        {
            BinaryOperator.Add => "+",
            BinaryOperator.Subtract => "-",
            BinaryOperator.Multiply => "*",
            BinaryOperator.Divide => "/",
            BinaryOperator.Modulo => "%",
            BinaryOperator.Equal => "==",
            BinaryOperator.NotEqual => "!=",
            BinaryOperator.Less => "<",
            BinaryOperator.LessOrEqual => "<=",
            BinaryOperator.Greater => ">",
            BinaryOperator.GreaterOrEqual => ">=",
            BinaryOperator.And => "and",
            _ => "or",
        };

    public static string Text(AssignOperator assignOperator) =>
        assignOperator switch
        {
            AssignOperator.AddAssign => "+=",
            AssignOperator.SubtractAssign => "-=",
            AssignOperator.MultiplyAssign => "*=",
            AssignOperator.DivideAssign => "/=",
            AssignOperator.ModuloAssign => "%=",
            _ => "=",
        };

    public static BinaryOperator ToBinaryOperator(AssignOperator assignOperator) =>
        assignOperator switch
        {
            AssignOperator.AddAssign => BinaryOperator.Add,
            AssignOperator.SubtractAssign => BinaryOperator.Subtract,
            AssignOperator.MultiplyAssign => BinaryOperator.Multiply,
            AssignOperator.DivideAssign => BinaryOperator.Divide,
            _ => BinaryOperator.Modulo,
        };

    public static bool IsComparison(BinaryOperator binaryOperator) =>
        binaryOperator is BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Less or BinaryOperator.LessOrEqual
            or BinaryOperator.Greater or BinaryOperator.GreaterOrEqual;

    public static bool IsEquality(BinaryOperator binaryOperator) => binaryOperator is BinaryOperator.Equal or BinaryOperator.NotEqual;

    /// <summary>Operators whose right operand takes the left operand's type as expected type (DESIGN 3.3 rule 2).</summary>
    public static bool PropagatesExpectedType(BinaryOperator binaryOperator) =>
        binaryOperator is BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Add;

    public static BinaryOutcome Evaluate(BinaryOperator binaryOperator, ScriptType left, ScriptType right)
    {
        if (left is ErrorType || right is ErrorType || TypeRelations.IsNever(left) || TypeRelations.IsNever(right))
        {
            ScriptType poisoned = IsComparison(binaryOperator) || binaryOperator is BinaryOperator.And or BinaryOperator.Or ? ScriptTypes.Boolean : ErrorType.Instance;
            return new BinaryOutcome(BinaryFailure.None, poisoned, ErrorType.Instance);
        }

        if (IsEquality(binaryOperator))
        {
            if (!TypeRelations.IsEquatable(left))
            {
                return new BinaryOutcome(BinaryFailure.ForbiddenEquality, ErrorType.Instance, left);
            }

            if (!TypeRelations.IsEquatable(right))
            {
                return new BinaryOutcome(BinaryFailure.ForbiddenEquality, ErrorType.Instance, right);
            }

            return Same(left, right) ? new BinaryOutcome(BinaryFailure.None, ScriptTypes.Boolean, left) : Different();
        }

        if (IsComparison(binaryOperator))
        {
            if (!TypeRelations.IsOrdered(left))
            {
                return new BinaryOutcome(BinaryFailure.Unordered, ErrorType.Instance, left);
            }

            if (!TypeRelations.IsOrdered(right))
            {
                return new BinaryOutcome(BinaryFailure.Unordered, ErrorType.Instance, right);
            }

            return Same(left, right) ? new BinaryOutcome(BinaryFailure.None, ScriptTypes.Boolean, left) : Different();
        }

        if (!IsDefinedBetweenSameTypes(binaryOperator, left))
        {
            return new BinaryOutcome(BinaryFailure.Undefined, ErrorType.Instance, left);
        }

        if (binaryOperator == BinaryOperator.Divide && left.Equals(ScriptTypes.Path))
        {
            return right.Equals(ScriptTypes.Path) || right.Equals(ScriptTypes.String)
                ? new BinaryOutcome(BinaryFailure.None, ScriptTypes.Path, left)
                : Different();
        }

        return Same(left, right) ? new BinaryOutcome(BinaryFailure.None, left, left) : Different();
    }

    public static bool IsDefinedBetweenSameTypes(BinaryOperator binaryOperator, ScriptType type) =>
        binaryOperator switch
        {
            BinaryOperator.Add => type is ListType || type is PrimitiveType { Name: "integer" or "float" or "string" },
            BinaryOperator.Subtract or BinaryOperator.Multiply => type is PrimitiveType { Name: "integer" or "float" },
            BinaryOperator.Divide => type is PrimitiveType { Name: "integer" or "float" or "path" },
            BinaryOperator.Modulo => type is PrimitiveType { Name: "integer" },
            BinaryOperator.And or BinaryOperator.Or => type.Equals(ScriptTypes.Boolean),
            BinaryOperator.Equal or BinaryOperator.NotEqual => TypeRelations.IsEquatable(type),
            _ => TypeRelations.IsOrdered(type),
        };

    /// <summary>Operators that accept the type as left operand, for the SCRIPTS-TYPE-0015 suggestion.</summary>
    public static string DescribeSupportedOperators(ScriptType leftType)
    {
        var supported = Enum.GetValues<BinaryOperator>()
            .Where(candidate => IsDefinedBetweenSameTypes(candidate, leftType))
            .Select(candidate => "`" + Text(candidate) + "`")
            .ToList();
        return supported.Count == 0 ? "none" : string.Join(", ", supported);
    }

    private static bool Same(ScriptType left, ScriptType right) => TypeRelations.IsSame(left, right);

    private static BinaryOutcome Different() => new(BinaryFailure.DifferentTypes, ErrorType.Instance, ErrorType.Instance);
}
