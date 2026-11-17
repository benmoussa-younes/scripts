using Scripts.Core;

namespace Scripts.Syntax;

/// <summary>Finds the first node (pre-order) deeper than <see cref="Limits.MaximumSyntaxDepth"/> without recursing.</summary>
internal static class SyntaxDepthChecker
{
    public static Node? FindFirstTooDeepNode(Node root, int rootDepth)
    {
        var pending = new Stack<(Node Node, int Depth)>();
        pending.Push((root, rootDepth));
        var children = new List<Node>();
        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            if (depth > Limits.MaximumSyntaxDepth)
            {
                return node;
            }

            children.Clear();
            AppendChildren(node, children);
            for (var childIndex = children.Count - 1; childIndex >= 0; childIndex--)
            {
                pending.Push((children[childIndex], depth + 1));
            }
        }

        return null;
    }

    /// <summary>
    /// Every node type this walker descends into. A test compares it with the reflection walker of the suite, so a
    /// node added to Core without a case here fails the tests instead of escaping the depth limit silently. It also
    /// states the two deliberate omissions: a <c>ProgramNode</c> is the root (the caller starts at depth 1) and an
    /// <c>ImportDeclaration</c> holds no node of its own, so it adds no level (DESIGN §1.1 counts statements and
    /// sub-expressions).
    /// </summary>
    internal static readonly HashSet<string> HandledNodeTypes = new(StringComparer.Ordinal)
    {
        "NamedTypeSyntax", "FunctionTypeSyntax", "InterpolatedString", "PathLiteral", "InterpolationPart", "ListLiteral",
        "MapLiteral", "MapEntry", "UnaryExpression", "BinaryExpression", "CallExpression", "MemberAccessExpression",
        "IndexExpression", "PropagateExpression", "LambdaParameter", "LambdaExpression", "OkExpression",
        "ErrorExpression", "ErrorConstructorExpression", "MatchExpression", "MatchArm", "LiteralPattern",
        "AlternativePattern", "ResultPattern", "BlockStatement", "LetStatement", "AssignmentStatement", "IfBranch",
        "IfStatement", "WhileStatement", "ForStatement", "ReturnStatement", "Parameter", "FunctionDeclaration",
        "ExpressionStatement",
    };

    private static void AppendChildren(Node node, List<Node> children)
    {
        switch (node)
        {
            case AlternativePattern alternatives:
                children.AddRange(alternatives.Alternatives);
                break;
            case ResultPattern result:
                children.Add(result.Inner);
                break;
            case NamedTypeSyntax named:
                children.AddRange(named.TypeArguments);
                break;
            case FunctionTypeSyntax function:
                children.AddRange(function.ParameterTypes);
                children.Add(function.ReturnType);
                break;
            case InterpolatedString interpolated:
                children.AddRange(interpolated.Parts);
                break;
            case PathLiteral path:
                children.AddRange(path.Parts);
                break;
            case InterpolationPart part:
                children.Add(part.Expression);
                break;
            case ListLiteral list:
                children.AddRange(list.Elements);
                break;
            case MapLiteral map:
                children.AddRange(map.Entries);
                break;
            case MapEntry entry:
                children.Add(entry.Key);
                children.Add(entry.Value);
                break;
            case UnaryExpression unary:
                children.Add(unary.Operand);
                break;
            case BinaryExpression binary:
                children.Add(binary.Left);
                children.Add(binary.Right);
                break;
            case CallExpression call:
                children.Add(call.Callee);
                // CallArgument is a node of its own, but it carries the argument's source text (its name), not a
                // level of expression nesting: walking it directly keeps the documented depth unchanged.
                children.AddRange(call.Arguments.Select(argument => (Node)argument.Expression));
                break;
            case MemberAccessExpression member:
                children.Add(member.Target);
                break;
            case IndexExpression index:
                children.Add(index.Target);
                children.Add(index.Index);
                break;
            case PropagateExpression propagation:
                children.Add(propagation.Operand);
                break;
            case LambdaParameter parameter:
                AddIfPresent(children, parameter.Annotation);
                break;
            case LambdaExpression lambda:
                children.AddRange(lambda.Parameters);
                children.Add(lambda.Body);
                break;
            case OkExpression ok:
                AddIfPresent(children, ok.Value);
                break;
            case ErrorExpression error:
                children.Add(error.Value);
                break;
            case ErrorConstructorExpression errorConstructor:
                children.Add(errorConstructor.Kind);
                children.Add(errorConstructor.Subject);
                children.Add(errorConstructor.Message);
                break;
            case MatchExpression match:
                children.Add(match.Scrutinee);
                children.AddRange(match.Arms);
                break;
            case MatchArm arm:
                children.Add(arm.Pattern);
                AddIfPresent(children, arm.BodyExpression);
                AddIfPresent(children, arm.BodyBlock);
                break;
            case LiteralPattern literal:
                children.Add(literal.Literal);
                break;
            default:
                AppendStatementChildren(node, children);
                break;
        }
    }

    private static void AppendStatementChildren(Node node, List<Node> children)
    {
        switch (node)
        {
            case BlockStatement block:
                children.AddRange(block.Statements);
                break;
            case LetStatement let:
                AddIfPresent(children, let.Annotation);
                children.Add(let.Initializer);
                break;
            case AssignmentStatement assignment:
                children.AddRange(assignment.Indexes);
                children.Add(assignment.Value);
                break;
            case IfBranch branch:
                children.Add(branch.Condition);
                children.Add(branch.Body);
                break;
            case IfStatement ifStatement:
                children.AddRange(ifStatement.Branches);
                AddIfPresent(children, ifStatement.ElseBlock);
                break;
            case WhileStatement whileStatement:
                children.Add(whileStatement.Condition);
                children.Add(whileStatement.Body);
                break;
            case ForStatement forStatement:
                children.Add(forStatement.Iterable);
                children.Add(forStatement.Body);
                break;
            case ReturnStatement returnStatement:
                AddIfPresent(children, returnStatement.Value);
                break;
            case Parameter parameter:
                children.Add(parameter.Type);
                break;
            case FunctionDeclaration function:
                children.AddRange(function.Parameters);
                AddIfPresent(children, function.ReturnType);
                children.Add(function.Body);
                break;
            case ExpressionStatement expressionStatement:
                children.Add(expressionStatement.Expression);
                break;
        }
    }

    private static void AddIfPresent(List<Node> children, Node? child)
    {
        if (child is not null)
        {
            children.Add(child);
        }
    }
}
