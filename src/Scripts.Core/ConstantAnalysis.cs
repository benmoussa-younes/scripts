namespace Scripts.Core;

/// <summary>Syntactic constant analysis of top-level let initializers (DESIGN 4.4); needs no types.</summary>
public static class ConstantAnalysis
{
    /// <summary>
    /// Sets LetStatement.IsConstant on every top-level immutable let whose initializer is a constant expression.
    /// Files are visited in load order so module constants are known before their importers.
    /// </summary>
    public static void MarkConstants(LoadedProgram program)
    {
        foreach (var file in program.AllFilesInLoadOrder())
        {
            var constantNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var statement in file.Program.Statements)
            {
                if (statement is LetStatement letStatement)
                {
                    letStatement.IsConstant = !letStatement.IsMutable
                        && letStatement.Name != "_"
                        && IsConstantExpression(letStatement.Initializer, file, constantNames);
                    if (letStatement.IsConstant)
                    {
                        constantNames.Add(letStatement.Name);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Literals (also interpolated strings, lists, maps, paths), unary minus, binary operators, references to constants declared earlier
    /// in the same file and alias.CONSTANT of an imported module. No call, lambda, match, ok/error, indexing or field access.
    /// </summary>
    public static bool IsConstantExpression(Expression expression, LoadedFile file, ISet<string> earlierConstantNames) =>
        FindNonConstantPart(expression, file, earlierConstantNames) is null;

    /// <summary>Innermost sub-expression that makes the expression non-constant, or null when it is constant.</summary>
    public static Expression? FindNonConstantPart(Expression expression, LoadedFile file, ISet<string> earlierConstantNames)
    {
        switch (expression)
        {
            case IntegerLiteral or FloatLiteral or BooleanLiteral or StringLiteral:
                return null;
            case InterpolatedString interpolated:
                return FindInParts(interpolated.Parts, file, earlierConstantNames);
            case PathLiteral path:
                return FindInParts(path.Parts, file, earlierConstantNames);
            case IdentifierExpression identifier:
                return earlierConstantNames.Contains(identifier.Name) ? null : identifier;
            case ListLiteral list:
                return FindInSequence(list.Elements, file, earlierConstantNames);
            case MapLiteral map:
                foreach (var entry in map.Entries)
                {
                    var offending = FindNonConstantPart(entry.Key, file, earlierConstantNames)
                        ?? FindNonConstantPart(entry.Value, file, earlierConstantNames);
                    if (offending is not null)
                    {
                        return offending;
                    }
                }

                return null;
            case UnaryExpression unary:
                return FindNonConstantPart(unary.Operand, file, earlierConstantNames);
            case BinaryExpression binary:
                return FindNonConstantPart(binary.Left, file, earlierConstantNames)
                    ?? FindNonConstantPart(binary.Right, file, earlierConstantNames);
            case MemberAccessExpression { Target: IdentifierExpression alias } member
                when file.Imports.TryGetValue(alias.Name, out var module) && IsModuleConstant(module, member.MemberName):
                return null;
            default:
                return expression;
        }
    }

    private static bool IsModuleConstant(LoadedFile module, string name)
    {
        foreach (var statement in module.Program.Statements)
        {
            if (statement is LetStatement { IsConstant: true } letStatement && letStatement.Name == name)
            {
                return true;
            }
        }

        return false;
    }

    private static Expression? FindInParts(IReadOnlyList<StringPart> parts, LoadedFile file, ISet<string> earlierConstantNames)
    {
        foreach (var part in parts)
        {
            if (part is InterpolationPart interpolation)
            {
                var offending = FindNonConstantPart(interpolation.Expression, file, earlierConstantNames);
                if (offending is not null)
                {
                    return offending;
                }
            }
        }

        return null;
    }

    private static Expression? FindInSequence(IReadOnlyList<Expression> expressions, LoadedFile file, ISet<string> earlierConstantNames)
    {
        foreach (var element in expressions)
        {
            var offending = FindNonConstantPart(element, file, earlierConstantNames);
            if (offending is not null)
            {
                return offending;
            }
        }

        return null;
    }
}
