using Scripts.Core;

namespace Scripts.Checker;

internal sealed partial class FileAnalyzer
{
    private ScriptType TypeOfUnary(UnaryExpression unary)
    {
        var operandType = TypeOf(unary.Operand, null);
        var isNegation = unary.Operator == UnaryOperator.Negate;
        if (operandType is ErrorType)
        {
            return isNegation ? ErrorType.Instance : ScriptTypes.Boolean;
        }

        var isSupported = isNegation
            ? operandType is PrimitiveType { Name: "integer" or "float" }
            : operandType.Equals(ScriptTypes.Boolean);
        if (isSupported)
        {
            return operandType;
        }

        var operatorText = isNegation ? "-" : "not";
        Report(
            ErrorCodes.Type0017,
            new Span(unary.Span.Start, unary.Span.Start + operatorText.Length),
            ("operator", operatorText),
            ("type", operandType.ToString()),
            ("supported", isNegation ? "`integer` or `float`" : "`boolean`"));
        // S19: a failed `not` yields `<error>` (not `boolean`) so that using the result does not add a
        // cascading diagnostic; promises no cascading error. `-` never yields `boolean`.
        return ErrorType.Instance;
    }

    /// <summary>SCRIPTS-TYPE-0069: the literal right operand of <c>/</c> on a path starts with <c>/</c> or is empty.</summary>
    private static bool TryAbsoluteOrEmptyLiteral(Expression operand, out string text)
    {
        switch (operand)
        {
            case StringLiteral literal:
                text = literal.Value;
                break;
            case PathLiteral path when path.Parts.All(part => part is TextPart):
                text = string.Concat(path.Parts.Cast<TextPart>().Select(part => part.Text));
                break;
            default:
                text = string.Empty;
                return false;
        }

        return text.Length == 0 || text.StartsWith('/');
    }

    /// <summary>SCRIPTS-TYPE-0069: reports a literal right operand of `/` or `/=` that is absolute or empty.</summary>
    private void ReportAbsoluteOrEmptyLiteral(Expression operand)
    {
        if (TryAbsoluteOrEmptyLiteral(operand, out var literalText))
        {
            Report(
                ErrorCodes.Type0069,
                operand.Span,
                ("value", Diagnostics.PrintableValue(literalText)));
        }
    }

    private ScriptType TypeOfBinary(BinaryExpression binary)
    {
        var propagatesExpectedType = OperatorRules.PropagatesExpectedType(binary.Operator);
        ScriptType leftType;
        ScriptType rightType;
        if (propagatesExpectedType && NeedsExpectedType(binary.Left) && !NeedsExpectedType(binary.Right))
        {
            rightType = TypeOf(binary.Right, null);
            leftType = TypeOf(binary.Left, rightType);
        }
        else
        {
            leftType = TypeOf(binary.Left, null);
            rightType = TypeOf(binary.Right, propagatesExpectedType && NeedsExpectedType(binary.Right) ? leftType : null);
        }

        var outcome = OperatorRules.Evaluate(binary.Operator, leftType, rightType);
        ReportBinaryFailure(binary, outcome, leftType, rightType);
        if (OperatorRules.IsEquality(binary.Operator))
        {
            CheckKindLiteralComparison(binary);
        }

        if (outcome.Failure == BinaryFailure.None)
        {
            if (binary.Operator == BinaryOperator.Divide
                && leftType is PrimitiveType { Name: "path" })
            {
                ReportAbsoluteOrEmptyLiteral(binary.Right);
            }

            return outcome.Type;
        }

        // S19: after an operator error, return `<error>` instead of `boolean` so that using the result does not
        // trigger a second (cascading) diagnostic; promises no cascading error. A `boolean` result is
        // kept only when an operand was already `<error>` (handled above through `BinaryFailure.None`), which is
        // exactly the boolean-expected context.
        return ErrorType.Instance;
    }

    private void ReportBinaryFailure(BinaryExpression binary, BinaryOutcome outcome, ScriptType leftType, ScriptType rightType)
    {
        var operatorText = OperatorRules.Text(binary.Operator);
        switch (outcome.Failure)
        {
            case BinaryFailure.ForbiddenEquality:
                Report(ErrorCodes.Type0018, binary.OperatorSpan, ("operator", operatorText), ("type", outcome.OffendingType.ToString()));
                break;
            case BinaryFailure.Unordered:
                Report(ErrorCodes.Type0019, binary.OperatorSpan, ("operator", operatorText), ("type", outcome.OffendingType.ToString()));
                break;
            case BinaryFailure.DifferentTypes:
                Report(
                    ErrorCodes.Type0016,
                    binary.OperatorSpan,
                    ("operator", operatorText),
                    ("left", leftType.ToString()),
                    ("right", rightType.ToString()));
                break;
            case BinaryFailure.Undefined:
                Report(
                    ErrorCodes.Type0015,
                    binary.OperatorSpan,
                    ("operator", operatorText),
                    ("left", leftType.ToString()),
                    ("right", rightType.ToString()),
                    ("supported", OperatorRules.DescribeSupportedOperators(leftType)));
                break;
        }
    }

    /// <summary>TYPE-0058: a string literal compared to a `kind` field must belong to the closed set of values.</summary>
    private void CheckKindLiteralComparison(BinaryExpression binary)
    {
        if (binary.Right is StringLiteral rightLiteral)
        {
            CheckKindLiteral(binary.Left, rightLiteral.Value, rightLiteral.Span);
        }
        else if (binary.Left is StringLiteral leftLiteral)
        {
            CheckKindLiteral(binary.Right, leftLiteral.Value, leftLiteral.Span);
        }
    }

    private void CheckKindLiteral(Expression kindCandidate, string literalValue, Span literalSpan)
    {
        if (!TryGetKindSet(kindCandidate, out var typeName, out var kinds) || kinds.Contains(literalValue))
        {
            return;
        }

        var closest = SimilarNames.FindClosest(literalValue, kinds);
        var fix = closest is not null
            ? new Fix($"replace with `{closest}`", source, literalSpan, "\"" + closest + "\"")
            : null;
        Report(
            ErrorCodes.Type0058,
            literalSpan,
            fix,
            ("literal", "\"" + literalValue + "\""),
            ("error_type", typeName),
            ("available", string.Join(", ", kinds)),
            ("closest", closest ?? SimilarNames.NoneText));
    }

    private static bool TryGetKindSet(Expression expression, out string typeName, out IReadOnlyList<string> kinds)
    {
        typeName = string.Empty;
        kinds = [];
        if (expression is not MemberAccessExpression { MemberName: "kind", Target.ResolvedType: NamedType owner })
        {
            return false;
        }

        var knownKinds = BuiltinTypes.PathKindKindsOf(owner.Name) ?? BuiltinTypes.KindsOf(owner.Name);
        if (knownKinds is null)
        {
            return false;
        }

        typeName = owner.Name;
        kinds = knownKinds;
        return true;
    }
}
