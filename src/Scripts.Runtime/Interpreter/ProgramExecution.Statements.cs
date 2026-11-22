using Scripts.Core;

namespace Scripts.Runtime.Interpreter;

internal sealed partial class ProgramExecution
{
    private ExecutionStatus ExecuteStatement(Statement statement, Scope scope)
    {
        // Single read of the interpreter state (a [ThreadStatic] field): two field writes
        // instead of two accesses to the "State" getter per executed statement.
        var state = State;
        state.CurrentEnvironment = scope.FileEnvironment;
        state.CurrentSpan = statement.Span;
        context.ThrowIfInterrupted();
        // A block arm of a `match` expression leaves through ControlSignal (L1); the statement evaluating it completes with its status.
        try
        {
            switch (statement)
            {
                case ExpressionStatement { Expression: MatchExpression { IsStatement: true } match }:
                    return ExecuteMatchStatement(match, scope);
                case ExpressionStatement expressionStatement:
                    return TryEvaluateStatementValue(expressionStatement.Expression, scope, CopyMode.None, out _)
                        ? ExecutionStatus.Normal
                        : ExecutionStatus.Return;
                case LetStatement letStatement:
                    return ExecuteLet(letStatement, scope);
                case AssignmentStatement assignment:
                    return ExecuteAssignment(assignment, scope);
                case IfStatement ifStatement:
                    return ExecuteIf(ifStatement, scope);
                case ReturnStatement returnStatement:
                    return ExecuteReturn(returnStatement, scope);
                case ForStatement forStatement:
                    return ExecuteFor(forStatement, scope);
                case WhileStatement whileStatement:
                    return ExecuteWhile(whileStatement, scope);
                case BreakStatement:
                    return ExecutionStatus.Break;
                case ContinueStatement:
                    return ExecutionStatus.Continue;
                case FunctionDeclaration declaration:
                    scope.Declare(declaration.Name, new Binding(new UserFunctionValue(declaration.Name, declaration.Parameters, declaration.Body, scope), false));
                    return ExecutionStatus.Normal;
                case BlockStatement block:
                    return ExecuteBlock(block, scope);
                default:
                    throw InternalError($"unsupported statement {statement.GetType().Name}");
            }
        }
        catch (ControlSignal signal)
        {
            return signal.Status;
        }
    }

    private ExecutionStatus ExecuteStatements(IReadOnlyList<Statement> statements, Scope scope)
    {
        for (var position = 0; position < statements.Count; position++)
        {
            var status = ExecuteStatement(statements[position], scope);
            if (status != ExecutionStatus.Normal)
            {
                return status;
            }
        }

        return ExecutionStatus.Normal;
    }

    private ExecutionStatus ExecuteBlock(BlockStatement block, Scope scope) =>
        ExecuteStatements(block.Statements, BlockIntroducesBinding(block) ? new Scope(scope, scope.FileEnvironment) : scope);

    /// <summary>
    /// True if the block declares at least one binding in its scope (nested function or named `let`). A block that
    /// introduces no binding does not need its own <see cref="Scope"/>: running its statements in the
    /// current scope is equivalent (an empty scope adds no binding; closures capture the parent).
    /// Deliberate indexed access: a `foreach` would allocate an enumerator on each call (+19% of measured `for` time).
    /// </summary>
    private static bool BlockIntroducesBinding(BlockStatement block)
    {
        var list = block.Statements;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is FunctionDeclaration)
            {
                return true;
            }

            if (list[i] is LetStatement let && let.Name != "_")
            {
                return true;
            }
        }

        return false;
    }

    private ExecutionStatus ExecuteLet(LetStatement letStatement, Scope scope)
    {
        var mode = letStatement.Name == "_"
            ? CopyMode.None
            : letStatement.IsMutable ? CopyMode.Owned : CopyMode.ForImmutableBinding;
        if (!TryEvaluateStatementValue(letStatement.Initializer, scope, mode, out var value))
        {
            return ExecutionStatus.Return;
        }

        if (letStatement.Name != "_")
        {
            scope.Declare(letStatement.Name, new Binding(value, letStatement.IsMutable));
        }

        return ExecutionStatus.Normal;
    }

    private ExecutionStatus ExecuteIf(IfStatement ifStatement, Scope scope)
    {
        var state = State;
        for (var position = 0; position < ifStatement.Branches.Count; position++)
        {
            var branch = ifStatement.Branches[position];
            state.CurrentSpan = new Span(branch.Span.Start, branch.Condition.Span.End);
            if (EvaluateCondition(branch.Condition, scope))
            {
                return ExecuteBlock(branch.Body, scope);
            }
        }

        return ifStatement.ElseBlock is null ? ExecutionStatus.Normal : ExecuteBlock(ifStatement.ElseBlock, scope);
    }

    private ExecutionStatus ExecuteWhile(WhileStatement whileStatement, Scope scope)
    {
        var state = State;
        var headerSpan = new Span(whileStatement.Span.Start, whileStatement.Condition.Span.End);
        // Predicate computed once, outside the loop: the body does not change from one iteration to the next.
        var bodyIntroducesBinding = BlockIntroducesBinding(whileStatement.Body);
        while (true)
        {
            state.CurrentEnvironment = scope.FileEnvironment;
            state.CurrentSpan = headerSpan;
            context.ThrowIfInterrupted();
            if (!EvaluateCondition(whileStatement.Condition, scope))
            {
                return ExecutionStatus.Normal;
            }

            // No new scope if the body binds nothing (otherwise a fresh scope per iteration, for closures).
            var status = bodyIntroducesBinding
                ? ExecuteStatements(whileStatement.Body.Statements, new Scope(scope, scope.FileEnvironment))
                : ExecuteStatements(whileStatement.Body.Statements, scope);
            if (status == ExecutionStatus.Break)
            {
                return ExecutionStatus.Normal;
            }

            if (status == ExecutionStatus.Return)
            {
                return status;
            }
        }
    }

    private ExecutionStatus ExecuteFor(ForStatement forStatement, Scope scope)
    {
        var state = State;
        state.CurrentSpan = new Span(forStatement.Span.Start, forStatement.Iterable.Span.End);
        var headerSpan = state.CurrentSpan;
        var iterable = Prepare(forStatement.Iterable, Evaluate(forStatement.Iterable, scope), CopyMode.ForImmutableBinding, scope);
        var snapshot = iterable as ListValue ?? throw InternalError("for expects a list");
        var bindsVariable = forStatement.VariableName != "_";
        // Predicate computed once, outside the loop. The loop variable requires a fresh scope per iteration;
        // the body too if it binds anything (closures capture the iteration scope by reference).
        var bodyIntroducesBinding = BlockIntroducesBinding(forStatement.Body);
        var needsIterationScope = bindsVariable || bodyIntroducesBinding;
        for (var position = 0; position < snapshot.Items.Count; position++)
        {
            state.CurrentEnvironment = scope.FileEnvironment;
            state.CurrentSpan = headerSpan;
            context.ThrowIfInterrupted();
            Scope iterationScope = scope;
            if (needsIterationScope)
            {
                iterationScope = new Scope(scope, scope.FileEnvironment);
                if (bindsVariable)
                {
                    iterationScope.Declare(forStatement.VariableName, new Binding(snapshot.Items[position], false));
                }
            }

            var status = ExecuteStatements(forStatement.Body.Statements, iterationScope);
            if (status == ExecutionStatus.Break)
            {
                return ExecutionStatus.Normal;
            }

            if (status == ExecutionStatus.Return)
            {
                return status;
            }
        }

        return ExecutionStatus.Normal;
    }

    private ExecutionStatus ExecuteReturn(ReturnStatement returnStatement, Scope scope)
    {
        var state = State;
        if (returnStatement.Value is null)
        {
            state.ReturnValue = UnitValue.Instance;
            return ExecutionStatus.Return;
        }

        if (!TryEvaluateStatementValue(returnStatement.Value, scope, CopyMode.Owned, out var value))
        {
            return ExecutionStatus.Return;
        }

        state.ReturnValue = value;
        return ExecutionStatus.Return;
    }

    /// <summary>
    /// Evaluates the expression of a statement. A top-level <c>operand?</c> inside a function is handled here without an exception:
    /// on an error result the function's return value is set and false is returned.
    /// </summary>
    private bool TryEvaluateStatementValue(Expression expression, Scope scope, CopyMode mode, out Value value)
    {
        var state = State;
        if (expression is PropagateExpression propagate && state.CallDepth > 0)
        {
            var result = AsResult(Evaluate(propagate.Operand, scope));
            if (!result.IsOk)
            {
                state.ReturnValue = Prepare(propagate.Operand, result, CopyMode.Owned, scope);
                value = UnitValue.Instance;
                return false;
            }

            value = Prepare(propagate, result.Payload, mode, scope);
            return true;
        }

        value = Prepare(expression, Evaluate(expression, scope), mode, scope);
        return true;
    }

    private bool EvaluateCondition(Expression expression, Scope scope) =>
        Evaluate(expression, scope) is BooleanValue condition
            ? condition.Flag
            : throw InternalError("condition is not a boolean");

    private static ResultValue AsResult(Value value) =>
        value as ResultValue ?? throw InternalError("? applied to a value that is not a result");
}
