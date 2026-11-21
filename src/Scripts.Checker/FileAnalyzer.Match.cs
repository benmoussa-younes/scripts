using Scripts.Core;

namespace Scripts.Checker;

internal sealed partial class FileAnalyzer
{
    private const int MatchKeywordLength = 5;

    private ScriptType TypeOfMatch(MatchExpression match, ScriptType? expected)
    {
        var scrutineeType = TypeOf(match.Scrutinee, null);
        var arePatternsChecked = TypeRelations.IsMatchable(scrutineeType);
        if (!arePatternsChecked)
        {
            Report(ErrorCodes.Type0024, match.Scrutinee.Span, ("type", scrutineeType.ToString()));
        }

        // `s.kind` is typed `string`, but its value set is closed (BuiltinTypes.KindsOf/PathKindKindsOf) and
        // already published by describe.types[].kinds: the coverage check uses it, so a match over every value
        // needs no wildcard (P4).
        var closedValues = TryGetKindSet(match.Scrutinee, out _, out var kinds) ? kinds : null;
        var coverage = new PatternCoverage(scrutineeType, closedValues);
        var expectedArmType = match.IsStatement ? null : KnownOrNull(expected);

        // Phase 1: every arm gets its own scope and its pattern bindings, in document order (coverage, reachability).
        var enclosingScope = currentScope;
        var armScopes = new Scope[match.Arms.Count];
        for (var position = 0; position < match.Arms.Count; position++)
        {
            var arm = match.Arms[position];
            currentScope = armScopes[position] = new Scope(enclosingScope);
            if (arePatternsChecked)
            {
                CheckArmPattern(arm.Pattern, match.Scrutinee, scrutineeType, coverage);
            }
            else
            {
                DeclareBindingsAsError(arm.Pattern);
            }
        }

        currentScope = enclosingScope;

        if (arePatternsChecked && scrutineeType is not ErrorType && !coverage.IsFullyCovered)
        {
            Report(
                ErrorCodes.Type0026,
                new Span(match.Span.Start, match.Span.Start + MatchKeywordLength),
                ("missing", coverage.DescribeMissing()));
        }

        // Phase 2: arm bodies. An expression `match` without an expected type anchors its empty literals on the first
        // arm that is self-typed, like a list literal (DESIGN 3.3): in `1 => []` / `_ => [2]` the empty list takes the
        // element type of `[2]` instead of reporting an undetermined type. An arm that always diverges anchors nothing.
        var armTypes = new ScriptType?[match.Arms.Count];
        var anchorType = match.IsStatement || expectedArmType is not null
            ? null
            : FindMatchAnchor(match, armScopes, enclosingScope, armTypes);

        for (var position = 0; position < match.Arms.Count; position++)
        {
            if (armTypes[position] is not null)
            {
                continue;
            }

            var arm = match.Arms[position];
            var body = arm.BodyExpression;
            currentScope = armScopes[position];
            armTypes[position] = anchorType is not null && body is not null && NeedsExpectedType(body)
                ? TypeOfArmBody(arm, isStatement: false, expectedArmType: anchorType)
                : TypeOfArmBody(arm, match.IsStatement, expectedArmType);
            currentScope = enclosingScope;
        }

        var everyArmDiverges = true;
        var typed = new List<ScriptType>(match.Arms.Count);
        foreach (var armType in armTypes)
        {
            everyArmDiverges &= TypeRelations.IsNever(armType!);
            typed.Add(armType!);
        }

        if (match.IsStatement)
        {
            return everyArmDiverges ? ScriptTypes.Never : ScriptTypes.Unit;
        }

        return expectedArmType ?? UnifyArmTypes(match, typed);
    }

    /// <summary>
    /// The first arm whose body does not need an expected type and does not diverge: its type becomes the expected
    /// type of the arms that declare an empty literal. The candidate is typed here, once, and stored in
    /// <paramref name="armTypes"/> so that the main loop never types an arm twice.
    /// </summary>
    private ScriptType? FindMatchAnchor(MatchExpression match, Scope[] armScopes, Scope enclosingScope, ScriptType?[] armTypes)
    {
        for (var position = 0; position < match.Arms.Count; position++)
        {
            var body = match.Arms[position].BodyExpression;
            if (body is null || NeedsExpectedType(body))
            {
                continue;
            }

            currentScope = armScopes[position];
            var candidate = TypeOfArmBody(match.Arms[position], isStatement: false, expectedArmType: null);
            currentScope = enclosingScope;
            armTypes[position] = candidate;
            if (!TypeRelations.IsNever(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Type of one arm body; statement arms must be `unit`/`never`, blocks yield `never` when they always diverge.</summary>
    private ScriptType TypeOfArmBody(MatchArm arm, bool isStatement, ScriptType? expectedArmType)
    {
        if (arm.BodyBlock is not null)
        {
            if (!isStatement)
            {
                return TypeOfValueBlock(arm.BodyBlock, expectedArmType);
            }

            return CheckBlock(arm.BodyBlock).Diverges ? ScriptTypes.Never : ScriptTypes.Unit;
        }

        var body = arm.BodyExpression!;
        if (isStatement)
        {
            var statementType = TypeOf(body, null);
            if (!TypeRelations.IsUnit(statementType) && !TypeRelations.IsNever(statementType) && statementType is not ErrorType)
            {
                Report(ErrorCodes.Type0048, body.Span, ("found", statementType.ToString()), ("expression", TextOf(body.Span)));
            }

            return statementType is ErrorType ? ScriptTypes.Unit : statementType;
        }

        return expectedArmType is not null ? CheckAgainst(body, expectedArmType) : TypeOf(body, null);
    }

    /// <summary>
    /// L1: in a `match` expression, the value of a block arm is its last line, which must be an expression; the lines
    /// before it are ordinary statements scoped to the block. A block that always diverges yields `never`; a block that
    /// ends with a statement is SCRIPTS-TYPE-0072. A statement-position `match` on the last line is a statement too.
    /// </summary>
    private ScriptType TypeOfValueBlock(BlockStatement block, ScriptType? expectedArmType)
    {
        var statements = block.Statements;
        var enclosingScope = currentScope;
        currentScope = new Scope(enclosingScope);
        MarkPendingDeclarations(statements, includeFunctions: true);
        var value = statements.Count > 0
            && statements[^1] is ExpressionStatement { Expression: not MatchExpression { IsStatement: true } } last
            ? last.Expression
            : null;
        ScriptType type;
        if (value is null)
        {
            var flow = CheckStatementList(statements, isFileLevel: false);
            if (!flow.Diverges)
            {
                Report(ErrorCodes.Type0072, new Span(block.Span.End - 1, block.Span.End));
            }

            type = flow.Diverges ? ScriptTypes.Never : ErrorType.Instance;
        }
        else
        {
            var flow = CheckStatementList(statements.Take(statements.Count - 1).ToArray(), isFileLevel: false);
            if (flow.Diverges)
            {
                Report(ErrorCodes.Type0053, statements[^1].Span, ("statement", flow.Description));
            }

            var valueType = expectedArmType is not null ? CheckAgainst(value, expectedArmType) : TypeOf(value, null);
            type = flow.Diverges ? ScriptTypes.Never : valueType;
        }

        currentScope = enclosingScope;
        return type;
    }

    /// <summary>Without a full expected type, the first non-diverging arm fixes the type and the others must equal it (TYPE-0028).</summary>
    private ScriptType UnifyArmTypes(MatchExpression match, List<ScriptType> armTypes)
    {
        ScriptType? firstType = null;
        for (var position = 0; position < armTypes.Count; position++)
        {
            var armType = armTypes[position];
            if (TypeRelations.IsNever(armType))
            {
                continue;
            }

            if (firstType is null)
            {
                firstType = armType;
            }
            else if (!TypeRelations.IsCompatible(firstType, armType))
            {
                Report(ErrorCodes.Type0028, ArmExpressionSpan(match.Arms[position]), ("first", firstType.ToString()), ("found", armType.ToString()));
            }
        }

        return firstType ?? ScriptTypes.Never;
    }

    private static Span ArmExpressionSpan(MatchArm arm) =>
        arm.BodyExpression?.Span
        ?? (arm.BodyBlock is { Statements: [.., ExpressionStatement last] } ? last.Expression.Span : arm.Span);

    private void CheckArmPattern(Pattern pattern, Expression scrutinee, ScriptType scrutineeType, PatternCoverage coverage)
    {
        var wasFullyCovered = coverage.IsFullyCovered;
        switch (pattern)
        {
            case WildcardPattern:
                RegisterCatchAll(pattern.Span, wasFullyCovered, coverage);
                break;
            case BindingPattern binding:
                DeclareVariable(binding.Name, binding.Span, VariableKind.PatternBinding, false, scrutineeType, null);
                RegisterCatchAll(pattern.Span, wasFullyCovered, coverage);
                break;
            case LiteralPattern literal:
                if (!CheckLiteralPattern(literal, scrutinee, scrutineeType))
                {
                    break;
                }

                if (wasFullyCovered)
                {
                    ReportUnreachableArm(pattern.Span, coverage);
                }
                else if (!coverage.TryCoverLiteral(literal.Literal))
                {
                    Report(ErrorCodes.Type0027, pattern.Span, ("reason", "this pattern is already covered by an earlier arm"));
                }

                break;
            case AlternativePattern alternative:
                CheckAlternativePattern(alternative, scrutinee, scrutineeType, coverage);
                break;
            case ResultPattern resultPattern:
                CheckResultPattern(resultPattern, scrutineeType, coverage, wasFullyCovered);
                break;
        }
    }

    private void RegisterCatchAll(Span patternSpan, bool wasFullyCovered, PatternCoverage coverage)
    {
        if (wasFullyCovered)
        {
            ReportUnreachableArm(patternSpan, coverage);
        }
        else
        {
            coverage.CoverEverything();
        }
    }

    private void ReportUnreachableArm(Span patternSpan, PatternCoverage coverage) =>
        Report(
            ErrorCodes.Type0027,
            patternSpan,
            ("reason", coverage.HasCatchAll ? "a wildcard or catch-all binding precedes this arm" : "all cases are already covered"));

    private bool CheckLiteralPattern(LiteralPattern literal, Expression scrutinee, ScriptType scrutineeType)
    {
        if (!IsLiteralPatternCompatible(literal, scrutineeType))
        {
            var found = literal.Literal switch
            {
                IntegerLiteral => ScriptTypes.Integer,
                StringLiteral => ScriptTypes.String,
                _ => ScriptTypes.Boolean,
            };
            Report(ErrorCodes.Type0025, literal.Span, ("found", found.ToString()), ("expected", scrutineeType.ToString()));
            return false;
        }

        if (literal.Literal is StringLiteral text)
        {
            CheckKindLiteral(scrutinee, text.Value, literal.Span);
        }

        return true;
    }

    private static bool IsLiteralPatternCompatible(LiteralPattern literal, ScriptType scrutineeType) =>
        scrutineeType is ErrorType
        || literal.Literal switch
        {
            IntegerLiteral => scrutineeType.Equals(ScriptTypes.Integer),
            StringLiteral => scrutineeType.Equals(ScriptTypes.String),
            BooleanLiteral => scrutineeType.Equals(ScriptTypes.Boolean),
            _ => false,
        };

    private void CheckAlternativePattern(AlternativePattern alternative, Expression scrutinee, ScriptType scrutineeType, PatternCoverage coverage)
    {
        var redundantSpans = new List<Span>();
        var wasFullyCovered = coverage.IsFullyCovered;
        foreach (var candidate in alternative.Alternatives)
        {
            var isCoveredBefore = coverage.IsFullyCovered;
            if (candidate is LiteralPattern literal)
            {
                if (CheckLiteralPattern(literal, scrutinee, scrutineeType) && (isCoveredBefore || !coverage.TryCoverLiteral(literal.Literal)))
                {
                    redundantSpans.Add(candidate.Span);
                }
            }
            else if (isCoveredBefore)
            {
                redundantSpans.Add(candidate.Span);
            }
            else
            {
                coverage.CoverEverything();
            }
        }

        if (redundantSpans.Count == alternative.Alternatives.Count)
        {
            if (wasFullyCovered)
            {
                ReportUnreachableArm(alternative.Span, coverage);
            }
            else
            {
                Report(ErrorCodes.Type0027, alternative.Span, ("reason", "all patterns of the alternative are already covered"));
            }

            return;
        }

        foreach (var span in redundantSpans)
        {
            Report(ErrorCodes.Type0027, span, ("reason", "this pattern of the alternative is already covered"));
        }
    }

    private void CheckResultPattern(ResultPattern pattern, ScriptType scrutineeType, PatternCoverage coverage, bool wasFullyCovered)
    {
        ScriptType innerType = ErrorType.Instance;
        if (scrutineeType is ResultType result)
        {
            innerType = pattern.IsOk ? result.Success : result.Failure;
        }
        else if (scrutineeType is not ErrorType)
        {
            Report(ErrorCodes.Type0025, pattern.Span, ("found", "result<_, _>"), ("expected", scrutineeType.ToString()));
        }

        if (pattern.Inner is BindingPattern binding)
        {
            DeclareVariable(binding.Name, binding.Span, VariableKind.PatternBinding, false, innerType, null);
        }

        if (scrutineeType is not ResultType)
        {
            return;
        }

        if (wasFullyCovered)
        {
            ReportUnreachableArm(pattern.Span, coverage);
        }
        else if (!coverage.TryCoverResult(pattern.IsOk))
        {
            Report(ErrorCodes.Type0027, pattern.Span, ("reason", "this pattern is already covered by an earlier arm"));
        }
    }

    private void DeclareBindingsAsError(Pattern pattern)
    {
        switch (pattern)
        {
            case BindingPattern binding:
                DeclareVariable(binding.Name, binding.Span, VariableKind.PatternBinding, false, ErrorType.Instance, null);
                break;
            case ResultPattern { Inner: BindingPattern innerBinding }:
                DeclareVariable(innerBinding.Name, innerBinding.Span, VariableKind.PatternBinding, false, ErrorType.Instance, null);
                break;
        }
    }
}
