using System.Globalization;
using Scripts.Core;

namespace Scripts.Checker;

internal abstract record ConstantValue;

internal sealed record IntegerConstant(long Value) : ConstantValue;

internal sealed record StringConstant(string Value) : ConstantValue;

internal sealed record BooleanConstant(bool Value) : ConstantValue;

internal sealed record PathConstant(string Value) : ConstantValue;

/// <summary>Compile-time evaluation of constant expressions, used to find duplicate map keys; null when not evaluable.</summary>
internal static class ConstantFolding
{
    public static ConstantValue? TryFold(Expression expression, Func<string, ConstantValue?> lookupConstant)
    {
        switch (expression)
        {
            case IntegerLiteral integer:
                return new IntegerConstant(integer.Value);
            case StringLiteral text:
                return new StringConstant(text.Value);
            case BooleanLiteral boolean:
                return new BooleanConstant(boolean.Value);
            case InterpolatedString interpolated:
                return TryFoldParts(interpolated.Parts, lookupConstant) is { } renderedText ? new StringConstant(renderedText) : null;
            case PathLiteral path:
                return TryFoldParts(path.Parts, lookupConstant) is { } renderedPath ? new PathConstant(renderedPath) : null;
            case IdentifierExpression identifier:
                return lookupConstant(identifier.Name);
            case UnaryExpression unary:
                return TryFoldUnary(unary, lookupConstant);
            case BinaryExpression binary:
                return TryFoldBinary(binary, lookupConstant);
            default:
                return null;
        }
    }

    private static string? TryFoldParts(IReadOnlyList<StringPart> parts, Func<string, ConstantValue?> lookupConstant)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            switch (part)
            {
                case TextPart text:
                    builder.Append(text.Text);
                    break;
                case InterpolationPart interpolation:
                    var rendered = TryFold(interpolation.Expression, lookupConstant) switch
                    {
                        IntegerConstant integer => integer.Value.ToString(CultureInfo.InvariantCulture),
                        StringConstant text => text.Value,
                        BooleanConstant boolean => boolean.Value ? "true" : "false",
                        PathConstant path => path.Value,
                        _ => null,
                    };
                    if (rendered is null)
                    {
                        return null;
                    }

                    builder.Append(rendered);
                    break;
            }
        }

        return builder.ToString();
    }

    private static ConstantValue? TryFoldUnary(UnaryExpression unary, Func<string, ConstantValue?> lookupConstant)
    {
        var operand = TryFold(unary.Operand, lookupConstant);
        return (unary.Operator, operand) switch
        {
            (UnaryOperator.Negate, IntegerConstant { Value: not long.MinValue } integer) => new IntegerConstant(-integer.Value),
            (UnaryOperator.Not, BooleanConstant boolean) => new BooleanConstant(!boolean.Value),
            _ => null,
        };
    }

    private static ConstantValue? TryFoldBinary(BinaryExpression binary, Func<string, ConstantValue?> lookupConstant)
    {
        var left = TryFold(binary.Left, lookupConstant);
        var right = TryFold(binary.Right, lookupConstant);
        if (left is null || right is null)
        {
            return null;
        }

        return (left, right) switch
        {
            (IntegerConstant leftInteger, IntegerConstant rightInteger) => TryFoldIntegers(binary.Operator, leftInteger.Value, rightInteger.Value),
            (StringConstant leftText, StringConstant rightText) when binary.Operator == BinaryOperator.Add => new StringConstant(leftText.Value + rightText.Value),
            (BooleanConstant leftBoolean, BooleanConstant rightBoolean) => binary.Operator switch
            {
                BinaryOperator.And => new BooleanConstant(leftBoolean.Value && rightBoolean.Value),
                BinaryOperator.Or => new BooleanConstant(leftBoolean.Value || rightBoolean.Value),
                _ => null,
            },
            _ => null,
        };
    }

    private static ConstantValue? TryFoldIntegers(BinaryOperator binaryOperator, long left, long right)
    {
        try
        {
            return binaryOperator switch
            {
                BinaryOperator.Add => new IntegerConstant(checked(left + right)),
                BinaryOperator.Subtract => new IntegerConstant(checked(left - right)),
                BinaryOperator.Multiply => new IntegerConstant(checked(left * right)),
                BinaryOperator.Divide when right != 0 && !(left == long.MinValue && right == -1) => new IntegerConstant(left / right),
                BinaryOperator.Modulo when right != 0 && right != -1 => new IntegerConstant(left % right),
                BinaryOperator.Equal => new BooleanConstant(left == right),
                BinaryOperator.NotEqual => new BooleanConstant(left != right),
                BinaryOperator.Less => new BooleanConstant(left < right),
                BinaryOperator.LessOrEqual => new BooleanConstant(left <= right),
                BinaryOperator.Greater => new BooleanConstant(left > right),
                BinaryOperator.GreaterOrEqual => new BooleanConstant(left >= right),
                _ => null,
            };
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}

/// <summary>Explanation of why a top-level initializer is not a constant expression (TYPE-0007, MODULE-0009).</summary>
internal static class ConstantReasons
{
    public static string Describe(LetStatement declaration, LoadedFile file, ISet<string> earlierConstantNames)
    {
        if (declaration.IsMutable)
        {
            return "declared with `mutable`";
        }

        return ConstantAnalysis.FindNonConstantPart(declaration.Initializer, file, earlierConstantNames) switch
        {
            CallExpression => "the initializer contains a call",
            IdentifierExpression identifier => $"the initializer reads `{identifier.Name}`, which is not a constant declared above",
            LambdaExpression => "the initializer is a lambda",
            MatchExpression => "the initializer is a `match`",
            PropagateExpression => "the initializer uses `?`",
            OkExpression or ErrorExpression or ErrorConstructorExpression => "the initializer builds a `result` or an `error`",
            MemberAccessExpression => "the initializer reads a non-constant field or member",
            IndexExpression => "the initializer indexes a value",
            _ => "the initializer is not a constant expression",
        };
    }
}
