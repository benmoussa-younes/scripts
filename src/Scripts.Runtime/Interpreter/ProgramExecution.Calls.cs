using Scripts.Core;

namespace Scripts.Runtime.Interpreter;

internal sealed partial class ProgramExecution
{
    private Value EvaluateCall(CallExpression call, Scope scope) => EvaluateCallCore(call, scope);

    private Value EvaluateCallCore(CallExpression call, Scope scope)
    {
        switch (call.Callee)
        {
            case IdentifierExpression identifier:
                return CallNamedFunction(call, identifier.Name, scope);
            case MemberAccessExpression member:
                return CallMember(call, member, scope);
            default:
            {
                var callee = Evaluate(call.Callee, scope) as FunctionValue ?? throw InternalError("call of a value that is not a function");
                return InvokeFunction(callee, EvaluateUserArguments(null, null, call.Arguments, NamesFor(callee, call.Arguments), scope));
            }
        }
    }

    /// <summary>Local function value, then function of the current file, then stdlib global (DESIGN 5.3).</summary>
    private Value CallNamedFunction(CallExpression call, string name, Scope scope)
    {
        // `assert` reports the failing call, not the enclosing statement (audit F5): its position is the
        // invariant check. Other runtime failures keep the statement position the spec defines for them.
        var isAssert = name == "assert";
        var savedSpan = currentSpan;
        var savedEnvironment = currentEnvironment;
        if (isAssert)
        {
            // The span of the call belongs to the file that contains it (a lambda declared in another file than
            // the function that invokes it), so the environment must follow it.
            currentSpan = call.Span;
            currentEnvironment = scope.FileEnvironment;
        }

        if (standardCallCache.TryGetValue(call, out var cached))
        {
            var cachedValue = cached.Implementation(context, EvaluateStandardArguments(null, null, call.Arguments, NamesFor(cached.Entry, call.Arguments), scope));
            if (isAssert)
            {
                currentSpan = savedSpan;
                currentEnvironment = savedEnvironment;
            }

            return cachedValue;
        }

        var function = FindVisibleFunction(name, scope);
        if (function is not null)
        {
            return InvokeFunction(function, EvaluateUserArguments(null, null, call.Arguments, NamesFor(function, call.Arguments), scope));
        }

        var entry = StdlibCatalog.FindGlobalFunction(name) ?? throw InternalError($"unknown function {name}");
        var resolved = Remember(call, entry, isMethod: false);
        var value = resolved.Implementation(context, EvaluateStandardArguments(null, null, call.Arguments, NamesFor(entry, call.Arguments), scope));
        if (isAssert)
        {
            currentSpan = savedSpan;
            currentEnvironment = savedEnvironment;
        }

        return value;
    }

    /// <summary>alias.function, environment.x and path.x are module calls; any other NAME.NAME is a method on the evaluated receiver.</summary>
    private Value CallMember(CallExpression call, MemberAccessExpression member, Scope scope)
    {
        if (member.Target is IdentifierExpression qualifier
            && (scope.FileEnvironment.Imports.ContainsKey(qualifier.Name)
                || StdlibCatalog.IsModuleName(qualifier.Name)))
        {
            return CallModuleFunction(call, qualifier.Name, member.MemberName, null, null, call.Arguments, scope);
        }

        var receiver = Evaluate(member.Target, scope);
        return CallMethod(call, member.Target, receiver, member.MemberName, call.Arguments, scope);
    }

    private Value CallModuleFunction(
        Expression node,
        string moduleName,
        string functionName,
        Expression? leadExpression,
        Value? leadValue,
        IReadOnlyList<CallArgument> arguments,
        Scope scope)
    {
        if (scope.FileEnvironment.Imports.TryGetValue(moduleName, out var module))
        {
            return module.Functions.TryGetValue(functionName, out var moduleFunction)
                ? InvokeFunction(moduleFunction, EvaluateUserArguments(leadExpression, leadValue, arguments, NamesFor(moduleFunction, arguments), scope))
                : throw InternalError($"unknown module function {moduleName}.{functionName}");
        }

        // An intrinsic namespace (`io`, `process`, `json`, `filesystem`, `parallel`) is resolved by the catalog.
        var lookupName = moduleName;

        if (!standardCallCache.TryGetValue(node, out var resolved))
        {
            var entry = StdlibCatalog.FindModuleFunction(lookupName, functionName)
                ?? throw InternalError($"unknown module function {moduleName}.{functionName}");
            resolved = Remember(node, entry, isMethod: false);
        }

        return resolved.Implementation(context, EvaluateStandardArguments(leadExpression, leadValue, arguments, NamesFor(resolved.Entry, arguments), scope));
    }

    private Value CallMethod(
        Expression node,
        Expression receiverExpression,
        Value receiver,
        string methodName,
        IReadOnlyList<CallArgument> arguments,
        Scope scope)
    {
        if (!standardCallCache.TryGetValue(node, out var resolved))
        {
            var entry = StdlibCatalog.FindMethod(ReceiverKindOf(receiver), methodName)
                ?? throw InternalError($"unknown method {methodName} on {receiver.GetType().Name}");
            resolved = Remember(node, entry, isMethod: true);
        }

        return CallResolvedMethod(resolved, receiverExpression, receiver, arguments, scope);
    }

    private Value CallResolvedMethod(
        ResolvedStandardCall resolved,
        Expression receiverExpression,
        Value receiver,
        IReadOnlyList<CallArgument> arguments,
        Scope scope)
    {
        if (!resolved.MutatesReceiver)
        {
            receiver = SnapshotBeforeLaterEvaluation(receiverExpression, receiver, arguments, scope);
        }

        var names = NamesFor(resolved.Entry, arguments);
        var values = new Value[arguments.Count + 1];
        values[0] = receiver;
        for (var position = 0; position < arguments.Count; position++)
        {
            var argument = arguments[position];
            values[1 + TargetIndex(argument, names, position)] = EvaluateOwned(argument.Expression, scope);
        }

        return resolved.Implementation(context, values);
    }

    private FunctionValue? FindVisibleFunction(string name, Scope scope)
    {
        if (scope.Find(name) is { } binding)
        {
            return binding.Value as FunctionValue;
        }

        return scope.FileEnvironment.Functions.TryGetValue(name, out var function) ? function : null;
    }

    private ResolvedStandardCall Remember(Expression node, StdlibEntry entry, bool isMethod)
    {
        var resolved = new ResolvedStandardCall(entry, StdlibRegistry.Get(entry.QualifiedName), isMethod);
        standardCallCache[node] = resolved;
        return resolved;
    }

    private static string ReceiverKindOf(Value receiver) => receiver switch
    {
        StringValue => "string",
        IntegerValue => "integer",
        FloatValue => "float",
        PathValue => "path",
        ListValue => "list",
        MapValue => "map",
        ResultValue => "result",
        RecordValue { TypeName: "process_result" } => "process_result",
        RecordValue { TypeName: "pipeline_result" } => "pipeline_result",
        RecordValue { TypeName: "run_request" } => "run_request",
        JsonValue => "json_value",
        FileLockValue => "file_lock",
        _ => throw InternalError($"no methods on {receiver.GetType().Name}"),
    };

    /// <summary>
    /// Arguments of a user function: parameters are immutable, so an immutable source can be shared. Arguments are
    /// evaluated in source order and each value is placed at its parameter position (DESIGN 5.3: a named call maps
    /// by name, never by position).
    /// </summary>
    private Value[] EvaluateUserArguments(
        Expression? leadExpression,
        Value? leadValue,
        IReadOnlyList<CallArgument> arguments,
        IReadOnlyList<string> parameterNames,
        Scope scope)
    {
        var offset = leadExpression is null ? 0 : 1;
        var values = new Value[arguments.Count + offset];
        if (leadExpression is not null)
        {
            values[0] = Prepare(leadExpression, leadValue!, CopyMode.ForImmutableBinding, scope);
        }

        for (var position = 0; position < arguments.Count; position++)
        {
            var argument = arguments[position];
            var value = Prepare(argument.Expression, Evaluate(argument.Expression, scope), CopyMode.ForImmutableBinding, scope);
            values[offset + TargetIndex(argument, parameterNames, position)] = value;
        }

        return values;
    }

    /// <summary>Stdlib functions may return an argument as is (unwrap_or, fold), so every argument is owned.</summary>
    private Value[] EvaluateStandardArguments(
        Expression? leadExpression,
        Value? leadValue,
        IReadOnlyList<CallArgument> arguments,
        IReadOnlyList<string> parameterNames,
        Scope scope)
    {
        var offset = leadExpression is null ? 0 : 1;
        var values = new Value[arguments.Count + offset];
        if (leadExpression is not null)
        {
            values[0] = Prepare(leadExpression, leadValue!, CopyMode.Owned, scope);
        }

        for (var position = 0; position < arguments.Count; position++)
        {
            var argument = arguments[position];
            values[offset + TargetIndex(argument, parameterNames, position)] = EvaluateOwned(argument.Expression, scope);
        }

        return values;
    }

    /// <summary>Positional list for the pipeline stages, whose grammar has no named form (DESIGN 5.4).</summary>
    private static IReadOnlyList<CallArgument> Positional(IReadOnlyList<Expression> arguments) =>
        [.. arguments.Select(argument => new CallArgument(argument.Span, argument, null, null))];

    /// <summary>Parameter names are materialized only for a named call, so a positional call allocates nothing.</summary>
    private static IReadOnlyList<string> NamesFor(FunctionValue function, IReadOnlyList<CallArgument> arguments) =>
        HasNamedArguments(arguments)
            ? function switch
            {
                UserFunctionValue user => [.. user.Parameters.Select(parameter => parameter.Name)],
                LambdaValue lambda => [.. lambda.Parameters.Select(parameter => parameter.Name)],
                _ => [],
            }
            : [];

    private static IReadOnlyList<string> NamesFor(StdlibEntry entry, IReadOnlyList<CallArgument> arguments) =>
        HasNamedArguments(arguments) ? [.. entry.Parameters.Select(parameter => parameter.Name)] : [];

    private static bool HasNamedArguments(IReadOnlyList<CallArgument> arguments)
    {
        for (var position = 0; position < arguments.Count; position++)
        {
            if (arguments[position].Name is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The value slot of one argument: its source position when positional, its parameter index when named.</summary>
    private static int TargetIndex(CallArgument argument, IReadOnlyList<string> parameterNames, int position)
    {
        if (argument.Name is null)
        {
            return position;
        }

        for (var index = 0; index < parameterNames.Count; index++)
        {
            if (string.Equals(parameterNames[index], argument.Name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw InternalError($"unknown named argument {argument.Name}");
    }

    private Value InvokeFromStandardLibrary(FunctionValue function, IReadOnlyList<Value> arguments)
    {
        context.ThrowIfInterrupted();
        // In-place reuse of the argument array: it is fresh on each call (context.Invoke allocates it via
        // `params`), so replacing its elements with their deep copies is not observed by anyone else. The DeepCopy
        // stays mandatory (a stdlib function may return an argument as is, e.g. unwrap_or/fold).
        if (arguments is Value[] array)
        {
            for (var position = 0; position < array.Length; position++)
            {
                array[position] = array[position].DeepCopy();
            }

            return InvokeFunction(function, array);
        }

        var copies = new Value[arguments.Count];
        for (var position = 0; position < copies.Length; position++)
        {
            copies[position] = arguments[position].DeepCopy();
        }

        return InvokeFunction(function, copies);
    }

    private Value InvokeFunction(FunctionValue function, Value[] arguments)
    {
        // Single read of the interpreter state: save/restore of the environment and of the position, and
        // increment/decrement of the depth, without going through the "State" getter ([ThreadStatic]) on each access.
        var state = State;
        if (state.CallDepth >= Limits.MaximumCallDepth)
        {
            throw new RuntimeFailure(ErrorCodes.Runtime0011, ("function", function is UserFunctionValue named ? named.Name : "lambda"));
        }

        var callerEnvironment = state.CurrentEnvironment;
        var callerSpan = state.CurrentSpan;
        state.CallDepth++;
        var result = function switch
        {
            UserFunctionValue user => RunUserFunction(user, arguments),
            LambdaValue lambda => RunLambda(lambda, arguments),
            _ => throw InternalError($"unsupported function value {function.GetType().Name}"),
        };
        state.CallDepth--;
        state.CurrentEnvironment = callerEnvironment;
        state.CurrentSpan = callerSpan;
        return result;
    }

    private Value RunUserFunction(UserFunctionValue function, Value[] arguments)
    {
        if (arguments.Length != function.Parameters.Count)
        {
            throw InternalError($"{function.Name} expects {function.Parameters.Count} arguments, got {arguments.Length}");
        }

        var functionScope = new Scope(function.ClosureScope, function.ClosureScope.FileEnvironment);
        for (var position = 0; position < arguments.Length; position++)
        {
            functionScope.Declare(function.Parameters[position].Name, new Binding(arguments[position], false));
        }

        try
        {
            return ExecuteStatements(function.Body.Statements, functionScope) == ExecutionStatus.Return
                ? TakeReturnValue()
                : UnitValue.Instance;
        }
        catch (PropagationSignal signal)
        {
            return signal.ErrorResult;
        }
    }

    private Value RunLambda(LambdaValue lambda, Value[] arguments)
    {
        if (arguments.Length != lambda.Parameters.Count)
        {
            throw InternalError($"lambda expects {lambda.Parameters.Count} arguments, got {arguments.Length}");
        }

        var lambdaScope = new Scope(lambda.ClosureScope, lambda.ClosureScope.FileEnvironment);
        for (var position = 0; position < arguments.Length; position++)
        {
            var parameterName = lambda.Parameters[position].Name;
            if (parameterName != "_")
            {
                lambdaScope.Declare(parameterName, new Binding(arguments[position], false));
            }
        }

        return EvaluateOwned(lambda.Body, lambdaScope);
    }
}
