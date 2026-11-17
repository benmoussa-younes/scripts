using Scripts.Core;

namespace Scripts.Checker.Tests;

/// <summary>Depth-first enumeration of every expression of a program (test helper).</summary>
internal static class AstWalker
{
    public static IEnumerable<Expression> Expressions(ProgramNode program) =>
        program.Statements.SelectMany(Expressions);

    public static IEnumerable<Expression> Expressions(Statement statement)
    {
        switch (statement)
        {
            case BlockStatement block:
                return block.Statements.SelectMany(Expressions);
            case LetStatement letStatement:
                return Expressions(letStatement.Initializer);
            case AssignmentStatement assignment:
                return assignment.Indexes.SelectMany(Expressions).Concat(Expressions(assignment.Value));
            case IfStatement ifStatement:
                return ifStatement.Branches
                    .SelectMany(branch => Expressions(branch.Condition).Concat(Expressions(branch.Body)))
                    .Concat(ifStatement.ElseBlock is null ? [] : Expressions(ifStatement.ElseBlock));
            case WhileStatement whileStatement:
                return Expressions(whileStatement.Condition).Concat(Expressions(whileStatement.Body));
            case ForStatement forStatement:
                return Expressions(forStatement.Iterable).Concat(Expressions(forStatement.Body));
            case ReturnStatement { Value: { } value }:
                return Expressions(value);
            case FunctionDeclaration function:
                return Expressions(function.Body);
            case ExpressionStatement expressionStatement:
                return Expressions(expressionStatement.Expression);
            default:
                return [];
        }
    }

    public static IEnumerable<Expression> Expressions(Expression expression)
    {
        yield return expression;
        foreach (var child in Children(expression).SelectMany(Expressions))
        {
            yield return child;
        }

        if (expression is MatchExpression match)
        {
            foreach (var block in match.Arms.Select(arm => arm.BodyBlock).OfType<BlockStatement>())
            {
                foreach (var inner in Expressions(block))
                {
                    yield return inner;
                }
            }
        }
    }

    private static IEnumerable<Expression> Children(Expression expression) =>
        expression switch
        {
            InterpolatedString interpolated => interpolated.Parts.OfType<InterpolationPart>().Select(part => part.Expression),
            PathLiteral path => path.Parts.OfType<InterpolationPart>().Select(part => part.Expression),
            ListLiteral list => list.Elements,
            MapLiteral map => map.Entries.SelectMany(entry => new[] { entry.Key, entry.Value }),
            UnaryExpression unary => [unary.Operand],
            BinaryExpression binary => [binary.Left, binary.Right],
            CallExpression call => [call.Callee, .. call.Arguments.Select(argument => argument.Expression)],
            MemberAccessExpression member => [member.Target],
            IndexExpression index => [index.Target, index.Index],
            PropagateExpression propagate => [propagate.Operand],
            LambdaExpression lambda => [lambda.Body],
            OkExpression { Value: { } value } => [value],
            ErrorExpression error => [error.Value],
            MatchExpression match => [match.Scrutinee, .. match.Arms.Select(arm => arm.BodyExpression).OfType<Expression>()],
            _ => [],
        };
}
