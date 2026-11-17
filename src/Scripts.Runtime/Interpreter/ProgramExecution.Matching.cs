using Scripts.Core;

namespace Scripts.Runtime.Interpreter;

internal sealed partial class ProgramExecution
{
    private Value EvaluateMatchExpression(MatchExpression match, Scope scope)
    {
        var scrutinee = Evaluate(match.Scrutinee, scope);
        var arm = SelectArm(match, scrutinee, scope, out var armScope);
        if (arm.BodyExpression is null)
        {
            throw InternalError("match expression arm without an expression body");
        }

        return EvaluateOwned(arm.BodyExpression, armScope ?? scope);
    }

    private ExecutionStatus ExecuteMatchStatement(MatchExpression match, Scope scope)
    {
        currentSpan = new Span(match.Span.Start, match.Scrutinee.Span.End);
        var scrutinee = Evaluate(match.Scrutinee, scope);
        var arm = SelectArm(match, scrutinee, scope, out var armScope);
        if (arm.BodyBlock is not null)
        {
            return ExecuteStatements(arm.BodyBlock.Statements, armScope ?? new Scope(scope, scope.FileEnvironment));
        }

        if (arm.BodyExpression is null)
        {
            throw InternalError("match arm without a body");
        }

        return TryEvaluateStatementValue(arm.BodyExpression, armScope ?? scope, CopyMode.None, out _)
            ? ExecutionStatus.Normal
            : ExecutionStatus.Return;
    }

    /// <summary>First arm whose pattern matches (DESIGN 5.2); the arm scope holds its bindings and is null when it binds nothing.</summary>
    private static MatchArm SelectArm(MatchExpression match, Value scrutinee, Scope scope, out Scope? armScope)
    {
        foreach (var arm in match.Arms)
        {
            armScope = null;
            if (TryMatch(arm.Pattern, scrutinee, scope, ref armScope))
            {
                return arm;
            }
        }

        throw InternalError("no match arm matched the scrutinee");
    }

    private static bool TryMatch(Pattern pattern, Value value, Scope scope, ref Scope? armScope)
    {
        switch (pattern)
        {
            case WildcardPattern:
                return true;
            case BindingPattern binding:
                armScope ??= new Scope(scope, scope.FileEnvironment);
                armScope.Declare(binding.Name, new Binding(value, false));
                return true;
            case LiteralPattern literal:
                return LiteralEquals(literal.Literal, value);
            case AlternativePattern alternatives:
                foreach (var alternative in alternatives.Alternatives)
                {
                    if (TryMatch(alternative, value, scope, ref armScope))
                    {
                        return true;
                    }
                }

                return false;
            case ResultPattern resultPattern:
                return value is ResultValue result
                    && result.IsOk == resultPattern.IsOk
                    && TryMatch(resultPattern.Inner, result.Payload, scope, ref armScope);
            default:
                throw InternalError($"unsupported pattern {pattern.GetType().Name}");
        }
    }

    private static bool LiteralEquals(Expression literal, Value value) => literal switch
    {
        IntegerLiteral integer => value is IntegerValue integerValue && integerValue.Number == integer.Value,
        StringLiteral text => value is StringValue textValue && string.Equals(textValue.Text, text.Value, StringComparison.Ordinal),
        BooleanLiteral boolean => value is BooleanValue booleanValue && booleanValue.Flag == boolean.Value,
        _ => throw InternalError($"unsupported literal pattern {literal.GetType().Name}"),
    };
}
