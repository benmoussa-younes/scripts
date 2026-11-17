using Scripts.Core;

namespace Scripts.Checker;

internal sealed partial class FileAnalyzer
{
    /// <summary>Checks the statements of one block in order; reports the first unreachable statement (TYPE-0053).</summary>
    private Flow CheckStatementList(IReadOnlyList<Statement> statements, bool isFileLevel)
    {
        var overallFlow = Flow.Continues;
        var unreachableReported = false;
        foreach (var statement in statements)
        {
            if (overallFlow.Diverges && !unreachableReported && statement is not FunctionDeclaration)
            {
                Report(ErrorCodes.Type0053, statement.Span, ("statement", overallFlow.Description));
                unreachableReported = true;
            }

            var flow = CheckStatement(statement, isFileLevel);
            if (!overallFlow.Diverges && flow.Diverges)
            {
                overallFlow = flow;
            }
        }

        return overallFlow;
    }

    private Flow CheckBlock(BlockStatement block)
    {
        var enclosingScope = currentScope;
        currentScope = new Scope(enclosingScope);
        MarkPendingDeclarations(block.Statements, includeFunctions: true);
        var flow = CheckStatementList(block.Statements, isFileLevel: false);
        currentScope = enclosingScope;
        return flow;
    }

    private Flow CheckStatement(Statement statement, bool isFileLevel)
    {
        switch (statement)
        {
            case LetStatement letStatement:
                CheckLetStatement(letStatement, isFileLevel);
                return Flow.Continues;
            case AssignmentStatement assignment:
                CheckAssignment(assignment);
                return Flow.Continues;
            case IfStatement ifStatement:
                return CheckIf(ifStatement);
            case WhileStatement whileStatement:
                return CheckWhile(whileStatement);
            case ForStatement forStatement:
                CheckFor(forStatement);
                return Flow.Continues;
            case ReturnStatement returnStatement:
                CheckReturn(returnStatement);
                return new Flow(true, "`return`");
            case BreakStatement breakStatement:
                CheckLoopControl("break", breakStatement.Span, marksBreak: true);
                return new Flow(true, "`break`");
            case ContinueStatement continueStatement:
                CheckLoopControl("continue", continueStatement.Span, marksBreak: false);
                return new Flow(true, "`continue`");
            case FunctionDeclaration function:
                CheckFunctionStatement(function, isFileLevel);
                return Flow.Continues;
            case ExpressionStatement expressionStatement:
                return CheckExpressionStatement(expressionStatement);
            default:
                return Flow.Continues;
        }
    }

    private void CheckLetStatement(LetStatement letStatement, bool isFileLevel)
    {
        if (isFileLevel && hoistedLets.TryGetValue(letStatement, out var constant))
        {
            if (constant is not null)
            {
                currentScope.Symbols[constant.Name] = constant;
                currentScope.PendingDeclarations.Remove(constant.Name);
            }

            return;
        }

        var bindingType = CheckLetInitializer(letStatement);
        DeclareVariable(
            letStatement.Name,
            letStatement.NameSpan,
            isFileLevel ? VariableKind.ScriptVariable : VariableKind.LocalVariable,
            letStatement.IsMutable,
            bindingType,
            letStatement,
            checkConflicts: !isFileLevel);
    }

    /// <summary>Types the annotation and initializer of a let and returns the type of the binding (DESIGN 3.3 rule 6).</summary>
    private ScriptType CheckLetInitializer(LetStatement letStatement)
    {
        if (letStatement.Annotation is not null)
        {
            var annotated = ResolveType(letStatement.Annotation, unitAllowed: false);
            CheckAgainst(letStatement.Initializer, annotated);
            return annotated;
        }

        var initializerType = TypeOf(letStatement.Initializer, null);
        if (TypeRelations.IsUnit(initializerType) || TypeRelations.IsNever(initializerType))
        {
            if (letStatement.Initializer is PropagateExpression)
            {
                Report(
                    ErrorCodes.Type0034,
                    letStatement.Initializer.Span,
                    ("context", " (here the operand of `?` unwraps to `unit`, result `result<unit, E>`)"));
            }
            else
            {
                Report(ErrorCodes.Type0034, letStatement.Initializer.Span, ("context", ""));
            }

            return ErrorType.Instance;
        }

        return initializerType;
    }

    private void CheckAssignment(AssignmentStatement assignment)
    {
        var targetType = ResolveAssignmentTarget(assignment, out var targetIsMutable);
        foreach (var index in assignment.Indexes)
        {
            switch (targetType)
            {
                case ListType list:
                    CheckAgainst(index, ScriptTypes.Integer);
                    targetType = list.Element;
                    break;
                case MapType map:
                    CheckAgainst(index, map.Key);
                    targetType = map.Value;
                    break;
                default:
                    if (targetType is not ErrorType)
                    {
                        Report(ErrorCodes.Type0041, assignment.TargetSpan, ("type", targetType.ToString()));
                    }

                    TypeOf(index, ErrorType.Instance);
                    targetType = ErrorType.Instance;
                    break;
            }
        }

        if (assignment.Operator == AssignOperator.Assign)
        {
            var errorsBefore = sink.Errors.Count;
            CheckAgainst(assignment.Value, targetType);
            if (sink.Errors.Count == errorsBefore)
            {
                ReportQuadraticStringConcatenation(assignment, targetType, targetIsMutable);
            }

            return;
        }

        var binaryOperator = OperatorRules.ToBinaryOperator(assignment.Operator);
        var valueErrorsBefore = sink.Errors.Count;
        var valueType = TypeOf(
            assignment.Value,
            OperatorRules.PropagatesExpectedType(binaryOperator) && NeedsExpectedType(assignment.Value) ? targetType : null);
        var outcome = OperatorRules.Evaluate(binaryOperator, targetType, valueType);
        if (outcome.Failure != BinaryFailure.None || !TypeRelations.IsSame(targetType, outcome.Type))
        {
            Report(
                ErrorCodes.Type0021,
                assignment.OperatorSpan,
                ("operator", OperatorRules.Text(assignment.Operator)),
                ("left", targetType.ToString()),
                ("op", OperatorRules.Text(binaryOperator)),
                ("right", valueType.ToString()));
            return;
        }

        // A rejected operand (SCRIPTS-TYPE-0001, …) is not a cost problem: report the lint only when the assignment is well typed.
        if (sink.Errors.Count == valueErrorsBefore)
        {
            ReportQuadraticStringConcatenation(assignment, targetType, targetIsMutable);
        }

        // An immutable target already gets SCRIPTS-TYPE-0039; report the operand only when the assignment is legal.
        if (assignment.Operator == AssignOperator.DivideAssign
            && targetIsMutable
            && targetType is PrimitiveType { Name: "path" })
        {
            ReportAbsoluteOrEmptyLiteral(assignment.Value);
        }
    }

    /// <summary>
    /// SCRIPTS-LINT-0012 (DESIGN 8.5): a mutable string assigned from itself with `+` (`s = s + …`) or `+=`
    /// (`s += …`) in a `for`/`while` body copies the whole accumulated string per iteration. The rule is
    /// conservative: it looks only at the receiver type and the left spine of the `+` chain, so a single
    /// append outside a loop, a prepend, a `list`/`join`, a different variable and a non-string receiver are
    /// not reported. A lambda body is skipped (deferred execution, not the loop body). A string declared
    /// inside the loop body is rebuilt on every iteration, so its accumulator stays linear and is skipped;
    /// a string declared outside the innermost loop still accumulates across the iterations of that loop.
    /// Called after the assignment's type check, so a rejected assignment does not add the warning.
    /// </summary>
    private void ReportQuadraticStringConcatenation(AssignmentStatement assignment, ScriptType targetType, bool targetIsMutable)
    {
        if (loopStack.Count == 0
            || lambdaDepth > 0
            || !targetIsMutable
            || assignment.Indexes.Count > 0
            || targetType is not PrimitiveType { Name: "string" }
            || IsDeclaredInLoopBody(assignment.TargetName, loopStack[^1].Body))
        {
            return;
        }

        if (assignment.Operator == AssignOperator.AddAssign
            || (assignment.Operator == AssignOperator.Assign && StartsWithSelfConcatenation(assignment.Value, assignment.TargetName)))
        {
            Report(ErrorCodes.Lint0012, assignment.TargetSpan);
        }
    }

    /// <summary>True when the binding is declared inside <paramref name="loopBody"/>, so it is new every iteration.</summary>
    private bool IsDeclaredInLoopBody(string name, Span loopBody) =>
        FindSymbol(name) is VariableSymbol { Declaration: { } declaration }
        && loopBody.Start <= declaration.Span.Start
        && declaration.Span.End <= loopBody.End;

    /// <summary>True when the left spine of the `+` chain starts at the variable being assigned (`s = s + a + b`).</summary>
    private static bool StartsWithSelfConcatenation(Expression value, string name)
    {
        var current = value;
        while (current is BinaryExpression { Operator: BinaryOperator.Add } addition)
        {
            current = addition.Left;
        }

        return current is IdentifierExpression identifier && identifier.Name == name;
    }

    private ScriptType ResolveAssignmentTarget(AssignmentStatement assignment, out bool isMutable)
    {
        switch (FindSymbol(assignment.TargetName))
        {
            case VariableSymbol variable:
                isMutable = variable.IsMutable;
                if (!variable.IsMutable)
                {
                    Report(ErrorCodes.Type0039, assignment.TargetSpan, ("name", variable.Name));
                }

                return variable.Type;
            case FunctionSymbol or AliasSymbol:
                isMutable = false;
                Report(ErrorCodes.Type0040, assignment.TargetSpan, ("name", assignment.TargetName));
                return ErrorType.Instance;
            default:
                isMutable = false;
                if (LanguageTables.IsReservedIdentifier(assignment.TargetName) || StdlibCatalog.IsGlobalFunctionName(assignment.TargetName))
                {
                    Report(ErrorCodes.Type0040, assignment.TargetSpan, ("name", assignment.TargetName));
                }
                else
                {
                    ReportUnknownName(assignment.TargetName, assignment.TargetSpan);
                }

                return ErrorType.Instance;
        }
    }

    private void CheckCondition(Expression condition)
    {
        var type = TypeOf(condition, ScriptTypes.Boolean);
        if (!TypeRelations.IsCompatible(ScriptTypes.Boolean, type))
        {
            Report(ErrorCodes.Type0020, condition.Span, ("found", type.ToString()));
        }
    }

    private Flow CheckIf(IfStatement ifStatement)
    {
        var everyBranchDiverges = true;
        foreach (var branch in ifStatement.Branches)
        {
            CheckCondition(branch.Condition);
            everyBranchDiverges &= CheckBlock(branch.Body).Diverges;
        }

        if (ifStatement.ElseBlock is null)
        {
            return Flow.Continues;
        }

        everyBranchDiverges &= CheckBlock(ifStatement.ElseBlock).Diverges;
        return everyBranchDiverges ? new Flow(true, "an `if`/`else` whose branches all diverge") : Flow.Continues;
    }

    private Flow CheckWhile(WhileStatement whileStatement)
    {
        CheckCondition(whileStatement.Condition);
        var loop = new LoopContext(whileStatement.Body.Span);
        loopStack.Add(loop);
        CheckBlock(whileStatement.Body);
        loopStack.RemoveAt(loopStack.Count - 1);
        return whileStatement.Condition is BooleanLiteral { Value: true } && !loop.HasBreak
            ? new Flow(true, "`while true` without `break`")
            : Flow.Continues;
    }

    private void CheckFor(ForStatement forStatement)
    {
        var iterableType = TypeOf(forStatement.Iterable, null);
        ScriptType elementType = ErrorType.Instance;
        if (iterableType is ListType list)
        {
            elementType = list.Element;
        }
        else if (iterableType is not ErrorType)
        {
            Report(ErrorCodes.Type0022, forStatement.Iterable.Span, ("found", iterableType.ToString()));
        }

        var enclosingScope = currentScope;
        currentScope = new Scope(enclosingScope);
        DeclareVariable(forStatement.VariableName, forStatement.VariableSpan, VariableKind.ForVariable, false, elementType, null);
        loopStack.Add(new LoopContext(forStatement.Body.Span));
        CheckBlock(forStatement.Body);
        loopStack.RemoveAt(loopStack.Count - 1);
        currentScope = enclosingScope;
    }

    private void CheckLoopControl(string keyword, Span span, bool marksBreak)
    {
        if (loopStack.Count == 0)
        {
            Report(ErrorCodes.Type0050, span, ("keyword", keyword));
        }
        else if (marksBreak)
        {
            loopStack[^1].HasBreak = true;
        }
    }

    private void CheckReturn(ReturnStatement returnStatement)
    {
        if (currentFunction is null)
        {
            CheckTopLevelReturn(returnStatement);
            return;
        }

        var expectedType = currentFunction.ReturnType;
        if (returnStatement.Value is null)
        {
            if (!TypeRelations.IsUnit(expectedType) && expectedType is not ErrorType)
            {
                Report(ErrorCodes.Type0051, returnStatement.Span, ("expected", expectedType.ToString()));
            }

            return;
        }

        var actualType = TypeOf(returnStatement.Value, expectedType);
        if (TypeRelations.IsUnit(actualType))
        {
        Report(ErrorCodes.Type0034, returnStatement.Value.Span, ("context", ""));
        }
        else if (!TypeRelations.IsCompatible(expectedType, actualType))
        {
            Report(ErrorCodes.Type0012, returnStatement.Value.Span, ("expected", expectedType.ToString()), ("found", actualType.ToString()));
        }
    }

    private void CheckTopLevelReturn(ReturnStatement returnStatement)
    {
        if (returnStatement.Value is null)
        {
            return;
        }

        var actualType = TypeOf(returnStatement.Value, null);
        if (TypeRelations.ContainsNonJsonType(actualType))
        {
            Report(ErrorCodes.Type0054, returnStatement.Value.Span);
        }
        else if (TypeRelations.IsUnit(actualType))
        {
        Report(ErrorCodes.Type0034, returnStatement.Value.Span, ("context", ""));
        }
    }

    private Flow CheckExpressionStatement(ExpressionStatement statement)
    {
        var type = TypeOf(statement.Expression, null);
        if (!TypeRelations.IsUnit(type) && !TypeRelations.IsNever(type) && type is not ErrorType)
        {
            Report(
                ErrorCodes.Type0048,
                statement.Span,
                ("found", type.ToString()),
                ("expression", TextOf(statement.Expression.Span)));
        }

        if (!TypeRelations.IsNever(type))
        {
            return Flow.Continues;
        }

        return new Flow(true, statement.Expression is MatchExpression ? "a `match` whose arms all diverge" : "`process.exit_with_code`");
    }

    private void CheckFunctionStatement(FunctionDeclaration function, bool isFileLevel)
    {
        if (isFileLevel)
        {
            CheckHoistedFunction(function);
            return;
        }

        var symbol = CreateFunctionSymbol(function);
        symbol.IsNested = true;
        CheckLocalDeclaration(function.Name, function.NameSpan);
        currentScope.Symbols[function.Name] = symbol;
        currentScope.PendingDeclarations.Remove(function.Name);
        CheckFunctionBody(symbol, currentScope, isTopLevel: false);
    }

    private void CheckFunctionBody(FunctionSymbol symbol, Scope parentScope, bool isTopLevel)
    {
        var declaration = symbol.Declaration;
        var savedScope = currentScope;
        var savedFunction = currentFunction;
        var savedLoops = loopStack;
        var savedLambdaDepth = lambdaDepth;
        var savedTopLevelName = enclosingTopLevelFunctionName;
        currentScope = new Scope(parentScope);
        currentFunction = new FunctionContext(declaration.Name, symbol.ReturnType);
        loopStack = [];
        lambdaDepth = 0;
        if (isTopLevel)
        {
            enclosingTopLevelFunctionName = declaration.Name;
        }

        DeclareParameters(symbol);
        var flow = CheckBlock(declaration.Body);
        if (!flow.Diverges && !TypeRelations.IsUnit(symbol.ReturnType) && symbol.ReturnType is not ErrorType)
        {
            Report(ErrorCodes.Type0052, declaration.NameSpan, ("function", declaration.Name), ("expected", symbol.ReturnType.ToString()));
        }

        currentScope = savedScope;
        currentFunction = savedFunction;
        loopStack = savedLoops;
        lambdaDepth = savedLambdaDepth;
        enclosingTopLevelFunctionName = savedTopLevelName;
    }

    private void DeclareParameters(FunctionSymbol symbol)
    {
        var firstSpans = new Dictionary<string, Span>(StringComparer.Ordinal);
        for (var position = 0; position < symbol.Declaration.Parameters.Count; position++)
        {
            var parameter = symbol.Declaration.Parameters[position];
            var nameSpan = new Span(parameter.Span.Start, parameter.Span.Start + parameter.Name.Length);
            // S20: `_` is a wildcard, not a name, so several `_` parameters are not duplicates (same rule as lambdas).
            if (parameter.Name != "_" && firstSpans.TryGetValue(parameter.Name, out var firstSpan))
            {
                Report(ErrorCodes.Type0006, nameSpan, ("name", parameter.Name), ("line", LineTextOf(firstSpan)));
                continue;
            }

            firstSpans[parameter.Name] = nameSpan;
            DeclareVariable(parameter.Name, nameSpan, VariableKind.Parameter, false, symbol.ParameterTypes[position], null);
        }
    }
}
