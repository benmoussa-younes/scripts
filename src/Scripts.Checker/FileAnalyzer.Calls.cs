using System.Globalization;
using Scripts.Core;
using CoreCallArgument = Scripts.Core.CallArgument;

namespace Scripts.Checker;

/// <summary>ParameterNames is empty for a function value, whose type publishes no names (DESIGN 5.3).</summary>
internal sealed record CallSignature(
    string DisplayName,
    IReadOnlyList<ScriptType> Parameters,
    IReadOnlyList<string> ParameterNames,
    ScriptType Return,
    string SignatureText);

/// <summary>An argument; the data operand of a pipeline stage arrives already typed.</summary>
internal readonly record struct CallArgument(Expression Expression, ScriptType? PreTypedType);

internal readonly record struct MethodCallSite(
    string MethodName,
    Span NameSpan,
    Expression Receiver,
    ScriptType ReceiverType,
    IReadOnlyList<CoreCallArgument> Arguments,
    Span ClosingParenthesis,
    Span CallSpan,
    Expression? Callee);

internal sealed partial class FileAnalyzer
{
    private const string LauncherTargetPlaceholder = "target_program";

    private static Span ClosingParenthesisOf(Expression expression) => new(expression.Span.End - 1, expression.Span.End);

    private static ScriptType? HintFor(ScriptType expectedType) => expectedType is TypeVariable ? null : expectedType;

    private static List<CallArgument> Wrap(IReadOnlyList<Expression> arguments) =>
        [.. arguments.Select(argument => new CallArgument(argument, null))];

    private static List<CallArgument> Wrap(IReadOnlyList<CoreCallArgument> arguments) =>
        [.. arguments.Select(argument => new CallArgument(argument.Expression, null))];

    private static IReadOnlyList<Expression> ExpressionsOf(IReadOnlyList<CallArgument> arguments) =>
        [.. arguments.Select(argument => argument.Expression)];

    /// <summary>A pipeline stage is positional by construction; DESIGN 5.4 keeps the stage's extra arguments in order.</summary>
    private static List<CoreCallArgument> Positional(IReadOnlyList<Expression> arguments) =>
        [.. arguments.Select(argument => new CoreCallArgument(argument.Span, argument, null, null))];

    private static CallSignature UserSignature(FunctionSymbol function, string displayName) =>
        new(
            displayName,
            function.ParameterTypes,
            [.. function.Declaration.Parameters.Select(parameter => parameter.Name)],
            function.ReturnType,
            function.SignatureText);

    private static CallSignature StdlibSignature(StdlibEntry entry, ParsedSignature signature) =>
        new(
            entry.QualifiedName,
            signature.Parameters,
            [.. entry.Parameters.Select(parameter => parameter.Name)],
            signature.Return,
            entry.Signature);

    private void SilenceArguments(IEnumerable<CallArgument> arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument.PreTypedType is null)
            {
                TypeOf(argument.Expression, ErrorType.Instance);
            }
        }
    }

    private ScriptType TypeOfCall(CallExpression call)
    {
        switch (call.Callee)
        {
            case IdentifierExpression identifier:
                return TypeOfIdentifierCall(call, identifier);
            case MemberAccessExpression member:
                return TypeOfMemberCall(call, member);
            default:
                var calleeType = TypeOf(call.Callee, null);
                return TypeOfFunctionValueCall(call, calleeType, call.Callee.Span, TextOf(call.Callee.Span));
        }
    }

    private ScriptType TypeOfIdentifierCall(CallExpression call, IdentifierExpression identifier)
    {
        switch (FindSymbol(identifier.Name))
        {
            case FunctionSymbol function:
                function.IsCalled = true;
                RejectCapturedReferenceInParallelWorker(function.IsNested, function.Name, identifier.Span);
                identifier.ResolvedType = function.Type;
                call.Target = new CallTarget(CallTargetKind.UserFunction, declaration: function.Declaration);
                var userSignature = UserSignature(function, function.Name);
                return TryBindCallArguments(call.Arguments, userSignature, call.Span, out var userArguments)
                    ? CheckSignatureCall(
                        userSignature,
                        new TypeBindings(),
                        userArguments,
                        ClosingParenthesisOf(call),
                        identifier.Span,
                        call.Span)
                    : ErrorType.Instance;
            case VariableSymbol variable:
                variable.IsRead = true;
                RejectCapturedReferenceInParallelWorker(variable.Type is FunctionType, variable.Name, identifier.Span);
                identifier.ResolvedType = variable.Type;
                return TypeOfFunctionValueCall(call, variable.Type, identifier.Span, variable.Name);
            case AliasSymbol:
                Report(ErrorCodes.Type0059, identifier.Span, ("name", identifier.Name));
                SilenceArguments(Wrap(call.Arguments));
                return ErrorType.Instance;
        }

        if (StdlibCatalog.FindGlobalFunction(identifier.Name) is { } entry)
        {
            var parsedSignature = SignatureCatalog.Get(entry);
            identifier.ResolvedType = parsedSignature.ToFunctionType();
            call.Target = new CallTarget(CallTargetKind.StdlibFunction, stdlib: entry);
            var signature = StdlibSignature(entry, parsedSignature);
            if (!TryBindCallArguments(call.Arguments, signature, call.Span, out var bound))
            {
                return ErrorType.Instance;
            }

            ApplyLint(entry, ExpressionsOf(bound), signature.Parameters.Count, identifier.Span);
            return CheckSignatureCall(
                signature,
                new TypeBindings(),
                bound,
                ClosingParenthesisOf(call),
                identifier.Span,
                call.Span);
        }

        if (StdlibCatalog.IsModuleName(identifier.Name))
        {
            Report(ErrorCodes.Type0059, identifier.Span, ("name", identifier.Name));
        }
        else
        {
            ReportUnknownName(identifier.Name, identifier.Span);
        }

        SilenceArguments(Wrap(call.Arguments));
        return ErrorType.Instance;
    }

    private ScriptType TypeOfFunctionValueCall(CallExpression call, ScriptType calleeType, Span calleeSpan, string displayName)
    {
        if (calleeType is FunctionType function)
        {
            call.Target = new CallTarget(CallTargetKind.FunctionValue);
            // A function type carries parameter types only: a named call has no published names to bind to.
            var signature = new CallSignature(displayName, function.Parameters, [], function.Return, function.ToString());
            return TryBindCallArguments(call.Arguments, signature, call.Span, out var bound)
                ? CheckSignatureCall(signature, new TypeBindings(), bound, ClosingParenthesisOf(call), calleeSpan, call.Span)
                : ErrorType.Instance;
        }

        if (calleeType is not ErrorType)
        {
            Report(ErrorCodes.Type0014, calleeSpan, ("type", calleeType.ToString()));
        }

        SilenceArguments(Wrap(call.Arguments));
        return ErrorType.Instance;
    }

    private ScriptType TypeOfMemberCall(CallExpression call, MemberAccessExpression member)
    {
        if (member.Target is IdentifierExpression targetIdentifier)
        {
            var symbol = FindSymbol(targetIdentifier.Name);
            if (symbol is AliasSymbol alias)
            {
                return TypeOfModuleCall(call, member, alias);
            }

            if (symbol is null && StdlibCatalog.IsModuleName(targetIdentifier.Name))
            {
                return TypeOfStandardModuleCall(call, member, targetIdentifier.Name);
            }
        }

        var receiverType = TypeOf(member.Target, null);
        var site = new MethodCallSite(
            member.MemberName,
            member.MemberSpan,
            member.Target,
            receiverType,
            call.Arguments,
            ClosingParenthesisOf(call),
            call.Span,
            member);
        var (type, target) = ResolveMethodCall(site);
        call.Target = target;
        return type;
    }

    private ScriptType TypeOfModuleCall(CallExpression call, MemberAccessExpression member, AliasSymbol alias)
    {
        if (alias.Info.Functions.TryGetValue(member.MemberName, out var function))
        {
            if (function.Declaration.Visibility == Visibility.Private)
            {
                ReportPrivateMember(member.MemberName, member.MemberSpan, alias);
                SilenceArguments(Wrap(call.Arguments));
                return ErrorType.Instance;
            }

            member.ResolvedType = function.Type;
            call.Target = new CallTarget(CallTargetKind.ModuleFunction, declaration: function.Declaration, moduleAlias: alias.Name);
            var signature = UserSignature(function, alias.Name + "." + function.Name);
            return TryBindCallArguments(call.Arguments, signature, call.Span, out var bound)
                ? CheckSignatureCall(
                    signature,
                    new TypeBindings(),
                    bound,
                    ClosingParenthesisOf(call),
                    member.MemberSpan,
                    call.Span)
                : ErrorType.Instance;
        }

        if (alias.Info.Constants.TryGetValue(member.MemberName, out var constant))
        {
            if (constant.Declaration?.Visibility == Visibility.Private)
            {
                ReportPrivateMember(member.MemberName, member.MemberSpan, alias);
                SilenceArguments(Wrap(call.Arguments));
                return ErrorType.Instance;
            }

            member.AccessKind = MemberAccessKind.ModuleConstant;
            member.ConstantDeclaration = constant.Declaration;
            member.ResolvedType = constant.Type;
            return TypeOfFunctionValueCall(call, constant.Type, member.Span, TextOf(member.Span));
        }

        ReportUnknownModuleMember(member.MemberName, member.MemberSpan, alias);
        SilenceArguments(Wrap(call.Arguments));
        return ErrorType.Instance;
    }

    private ScriptType TypeOfStandardModuleCall(CallExpression call, MemberAccessExpression member, string moduleName)
    {
        if (StdlibCatalog.FindModuleFunction(moduleName, member.MemberName) is not { } entry)
        {
            ReportUnknownStandardMember(moduleName, member.MemberName, member.MemberSpan);
            SilenceArguments(Wrap(call.Arguments));
            return ErrorType.Instance;
        }

        var parsedSignature = SignatureCatalog.Get(entry);
        member.ResolvedType = parsedSignature.ToFunctionType();
        call.Target = new CallTarget(CallTargetKind.StdlibFunction, stdlib: entry);
        var signature = StdlibSignature(entry, parsedSignature);
        if (!TryBindCallArguments(call.Arguments, signature, call.Span, out var bound))
        {
            return ErrorType.Instance;
        }

        ApplyLint(entry, ExpressionsOf(bound), signature.Parameters.Count, member.MemberSpan);
        return CheckSignatureCall(
            signature,
            new TypeBindings(),
            bound,
            ClosingParenthesisOf(call),
            member.MemberSpan,
            call.Span,
            lintHost: entry);
    }

    private (ScriptType Type, CallTarget? Target) ResolveMethodCall(MethodCallSite site)
    {
        var arguments = Wrap(site.Arguments);
        if (site.ReceiverType is ErrorType)
        {
            SilenceArguments(arguments);
            return (ErrorType.Instance, null);
        }

        var receiverKind = TypeRelations.ReceiverKindOf(site.ReceiverType);
        var entry = receiverKind is null ? null : StdlibCatalog.FindMethod(receiverKind, site.MethodName);
        if (entry is null)
        {
            if (receiverKind is not null
                && RetiredNames.TryMethod(receiverKind, site.MethodName, out var retiredMethod)
                && StdlibCatalog.FindMethod(receiverKind, retiredMethod) is { } retiredEntry)
            {
                var retiredFix = SignatureCatalog.Get(retiredEntry).Parameters.Count == site.Arguments.Count
                    ? new Fix($"rename to `{retiredMethod}`", source, site.NameSpan, retiredMethod)
                    : null;
                Report(
                    ErrorCodes.Type0068,
                    site.NameSpan,
                    retiredFix,
                    ("old", site.MethodName),
                    ("new", $"`{retiredMethod}`"));
                SilenceArguments(arguments);
                return (ErrorType.Instance, null);
            }

            var available = receiverKind is null ? [] : SignatureCatalog.MethodNames(receiverKind);
            var closestMethod = SimilarNames.FindFixCandidate(site.MethodName, available, sorted: true);
            // Only a rename that keeps the call's arity is a safe fix: `lenght(1)` -> `length` (0 parameters)
            // would replace a 0008 with a 0013 (audit F6).
            var closestEntry = closestMethod is null ? null : StdlibCatalog.FindMethod(receiverKind!, closestMethod);
            var methodFix = closestEntry is not null
                && SignatureCatalog.Get(closestEntry).Parameters.Count == site.Arguments.Count
                ? new Fix($"rename to `{closestMethod}`", source, site.NameSpan, closestMethod!)
                : null;
            Report(
                ErrorCodes.Type0008,
                site.NameSpan,
                methodFix,
                ("method", site.MethodName),
                ("type", site.ReceiverType.ToString()),
                ("available", SimilarNames.JoinAvailableWithClosest(available, site.MethodName)));
            SilenceArguments(arguments);
            return (ErrorType.Instance, null);
        }

        var signature = SignatureCatalog.Get(entry);
        var bindings = new TypeBindings();
        if (!MatchesMethodReceiver(entry, signature, site, bindings))
        {
            SilenceArguments(arguments);
            return (ErrorType.Instance, null);
        }

        CheckMutatingReceiver(entry, site);
        var callSignature = StdlibSignature(entry, signature);
        if (!TryBindCallArguments(site.Arguments, callSignature, site.CallSpan, out var bound))
        {
            return (ErrorType.Instance, null);
        }

        CheckEmptySeparator(entry, ExpressionsOf(bound));
        // SCRIPTS-TYPE-0064: the template received by string.format_template must not be interpolated (its braces
        // are evaluated before the method sees them). It runs only once the arguments agree with the signature.
        if (entry.QualifiedName == "string.format_template" && site.Receiver is InterpolatedString templateReceiver)
        {
            Report(ErrorCodes.Type0064, templateReceiver.Span);
        }
        var type = CheckSignatureCall(callSignature, bindings, bound, site.ClosingParenthesis, site.NameSpan, site.CallSpan);
        if (entry.Name == "group_by")
        {
            var keyType = bindings.Apply(new TypeVariable("K"));
            if (keyType is not TypeVariable && !TypeRelations.IsValidMapKey(keyType))
            {
                Report(ErrorCodes.Type0035, site.NameSpan, ("type", keyType.ToString()));
            }
        }

        // SCRIPTS-TYPE-0071 (L2): `ignore_error` must never drop a computed value along with the error.
        if (entry.QualifiedName == "result<T,E>.ignore_error"
            && bindings.Apply(new TypeVariable("T")) is var valueType
            && valueType is not (TypeVariable or ErrorType)
            && !TypeRelations.IsUnit(valueType))
        {
            Report(ErrorCodes.Type0071, site.NameSpan, ("type", valueType.ToString()));
        }

        if (site.Callee is not null)
        {
            site.Callee.ResolvedType = TypeRelations.ReplaceTypeVariablesWithError(bindings.Apply(signature.ToFunctionType()));
        }

        // SCRIPTS-LINT-0005 (reassigned when the six-argument constructor was removed): a literal
        // directory passed as the standard input is almost certainly a working directory.
        if (entry.QualifiedName == "run_request.with_standard_input"
            && bound.Count == 1
            && bound[0].Expression is StringLiteral text
            && LintRules.LooksLikeDirectory(text))
        {
            Report(ErrorCodes.Lint0005, text.Span, ("argument", TextOf(text.Span)));
        }

        return (type, new CallTarget(CallTargetKind.StdlibMethod, stdlib: entry));
    }

    /// <summary>
    /// SCRIPTS-TYPE-0065: an empty literal separator of <c>split</c>/<c>split_regex</c> can never succeed, so it is a static
    /// error (DESIGN 7.6). The separator is the argument after the receiver; <paramref name="arguments"/> holds it first
    /// for a pipeline stage, whose data operand is the stage's receiver (DESIGN 5.4).
    /// </summary>
    private void CheckEmptySeparator(StdlibEntry entry, IReadOnlyList<Expression> arguments)
    {
        if (LintRules.FindEmptySeparator(entry, arguments) is { } separator)
        {
            Report(ErrorCodes.Type0065, separator.Span, ("method", entry.Name));
        }
    }

    /// <summary>Binds the receiver's type variables and enforces the element constraints of join, sorted and contains (TYPE-0057).</summary>
    private bool MatchesMethodReceiver(StdlibEntry entry, ParsedSignature signature, MethodCallSite site, TypeBindings bindings)
    {
        var satisfied = bindings.TryUnify(signature.Receiver!, site.ReceiverType);
        if (satisfied && entry.ReceiverTypeText == "list<T>")
        {
            var elementType = bindings.Apply(new TypeVariable("T"));
            satisfied = entry.Name switch
            {
                "sorted" or "minimum" or "maximum" => TypeRelations.IsOrdered(elementType),
                "contains" or "distinct" => TypeRelations.IsEquatable(elementType),
                _ => true,
            };
        }

        if (!satisfied)
        {
            var constraint = entry.Name switch
            {
                "join" => "a `list<string>`",
                "sorted" or "minimum" or "maximum" => "elements of type `integer`, `float`, `string` or `path`",
                "sum" => "a `list<integer>`",
                "collect" => "a `list<result<T,E>>`",
                "flatten" => "a `list<list<T>>`",
                _ => "elements comparable with `==`",
            };
            Report(
                ErrorCodes.Type0057,
                site.NameSpan,
                ("method", site.MethodName),
                ("constraint", constraint),
                ("type", site.ReceiverType.ToString()));
        }

        return satisfied;
    }

    private void CheckMutatingReceiver(StdlibEntry entry, MethodCallSite site)
    {
        if (entry.Name is not ("append" or "remove_key" or "insert_at" or "remove_at"))
        {
            return;
        }

        if (site.Receiver is not IdentifierExpression receiverIdentifier)
        {
            Report(
                ErrorCodes.Type0042,
                site.Receiver.Span,
                ("method", site.MethodName),
                ("receiver", TextOf(site.Receiver.Span)));
        }
        else if (FindSymbol(receiverIdentifier.Name) is VariableSymbol { IsMutable: false })
        {
            Report(ErrorCodes.Type0043, receiverIdentifier.Span, ("method", site.MethodName), ("name", receiverIdentifier.Name));
        }
        else if (parallelLambdaDepth > 0
            && FindSymbol(receiverIdentifier.Name) is VariableSymbol { IsMutable: true, Kind: not VariableKind.LambdaParameter } captured)
        {
            Report(ErrorCodes.Type0063, receiverIdentifier.Span, ("name", captured.Name), ("method", site.MethodName));
        }
    }

    /// <summary>
    /// SCRIPTS-LINT-0014: two consecutive `path` parameters receive positional arguments, so a swap cannot be detected.
    /// Reported once per call, at the first such pair, unless both arguments already carry the parameter names. The
    /// fix names every argument (a call is either fully positional or fully named) and keeps the source order, hence
    /// the evaluation order.
    /// </summary>
    private void ReportPositionalPaths(IReadOnlyList<CoreCallArgument> arguments, CallSignature signature)
    {
        var names = signature.ParameterNames;
        if (names.Count != signature.Parameters.Count || arguments.Count != names.Count)
        {
            return;
        }

        for (var position = 0; position + 1 < names.Count; position++)
        {
            if (!ScriptTypes.Path.Equals(signature.Parameters[position])
                || !ScriptTypes.Path.Equals(signature.Parameters[position + 1]))
            {
                continue;
            }

            var first = arguments[position];
            var second = arguments[position + 1];
            if (first.Expression is IdentifierExpression { } firstIdentifier && firstIdentifier.Name == names[position]
                && second.Expression is IdentifierExpression { } secondIdentifier && secondIdentifier.Name == names[position + 1])
            {
                continue;
            }

            var start = arguments[0].Expression.Span.Start;
            var named = new System.Text.StringBuilder();
            for (var index = 0; index < arguments.Count; index++)
            {
                var expression = arguments[index].Expression.Span;
                var nextStart = index + 1 < arguments.Count ? arguments[index + 1].Expression.Span.Start : expression.End;
                named.Append(names[index]).Append(" = ").Append(source.Text, expression.Start, nextStart - expression.Start);
            }

            var fix = new Fix("name the arguments", source, new Span(start, arguments[^1].Expression.Span.End), named.ToString());
            Report(
                ErrorCodes.Lint0014,
                new Span(first.Expression.Span.Start, second.Expression.Span.End),
                fix,
                ("first", names[position]),
                ("second", names[position + 1]),
                ("function", signature.DisplayName));
            return;
        }
    }

    /// <summary>
    /// Binds a call's arguments to its signature. A positional call keeps the historical path unchanged. A named
    /// call (the parser guarantees it is fully named) is mapped to the parameter positions and validated: every
    /// parameter must be named exactly once, with no unknown or repeated name (SCRIPTS-TYPE-0067). The AST keeps the
    /// arguments in source order, and the interpreter evaluates them in that order, placing each value by name.
    /// </summary>
    private bool TryBindCallArguments(
        IReadOnlyList<CoreCallArgument> arguments,
        CallSignature signature,
        Span callSpan,
        out List<CallArgument> bound)
    {
        bound = Wrap(arguments);
        if (!arguments.Any(argument => argument.Name is not null))
        {
            ReportPositionalPaths(arguments, signature);
            return true;
        }

        var names = signature.ParameterNames;
        if (names.Count == 0 && signature.Parameters.Count == 0)
        {
            // A named function with no parameter: the real mistake is the extra argument (audit F9), not the
            // absence of published names.
            Report(
                ErrorCodes.Type0013,
                callSpan,
                ("function", signature.DisplayName),
                ("expected", "0"),
                ("found", arguments.Count.ToString(CultureInfo.InvariantCulture)),
                ("signature", signature.SignatureText));
            SilenceArguments(bound);
            bound = [];
            return false;
        }

        if (names.Count == 0 || names.Count != signature.Parameters.Count)
        {
            // A function value's type publishes no parameter names: one diagnostic at the call, with the cause
            // that actually applies (a per-argument "unknown argument" would suggest a catalogue lookup that
            // cannot exist — audit P8 #6).
            Report(ErrorCodes.Type0067, callSpan, ("reason", "a function value publishes no parameter names; call it positionally"));
            SilenceArguments(bound);
            bound = [];
            return false;
        }

        // Both arrays are indexed by parameter position, so they follow the signature, not the source argument count.
        var ordered = new CallArgument[names.Count];
        var seen = new bool[names.Count];
        var valid = true;
        foreach (var argument in arguments)
        {
            var position = IndexOfName(names, argument.Name!);
            if (position < 0)
            {
                if (RetiredNames.TryParameter(signature.DisplayName, argument.Name!, out var renamedParameter))
                {
                    Report(
                        ErrorCodes.Type0068,
                        argument.NameSpan ?? argument.Span,
                        new Fix($"rename to `{renamedParameter}`", source, argument.NameSpan ?? argument.Span, renamedParameter),
                        ("old", argument.Name!),
                        ("new", $"`{renamedParameter}`"));
                    valid = false;
                    continue;
                }

                Report(ErrorCodes.Type0067, argument.NameSpan ?? argument.Span, ("reason", $"unknown argument `{argument.Name}`"));
                valid = false;
                continue;
            }

            if (seen[position])
            {
                Report(ErrorCodes.Type0067, argument.NameSpan ?? argument.Span, ("reason", $"argument `{argument.Name}` given twice"));
                valid = false;
                continue;
            }

            seen[position] = true;
            ordered[position] = new CallArgument(argument.Expression, null);
        }

        // Report missing parameters only when every supplied name is valid: otherwise the unknown name already
        // explains the call and the missing parameters are a cascade of the same mistake.
        if (valid)
        {
            for (var position = 0; position < names.Count; position++)
            {
                if (!seen[position])
                {
                    Report(ErrorCodes.Type0067, callSpan, ("reason", $"missing argument `{names[position]}`"));
                    valid = false;
                }
            }
        }

        if (!valid)
        {
            SilenceArguments(bound);
            bound = [];
            return false;
        }

        bound = [.. ordered];
        return true;
    }

    private static int IndexOfName(IReadOnlyList<string> names, string name)
    {
        for (var position = 0; position < names.Count; position++)
        {
            if (string.Equals(names[position], name, StringComparison.Ordinal))
            {
                return position;
            }
        }

        return -1;
    }

    /// <summary>Checks arity then arguments: non-lambda arguments bind type variables first, lambdas afterwards (DESIGN 3.3 rule 4).</summary>
    private ScriptType CheckSignatureCall(
        CallSignature signature,
        TypeBindings bindings,
        IReadOnlyList<CallArgument> arguments,
        Span closingParenthesis,
        Span anchorSpan,
        Span callSpan,
        StdlibEntry? lintHost = null)
    {
        if (arguments.Count != signature.Parameters.Count)
        {
            ReportArity(signature, arguments, closingParenthesis, anchorSpan);
            SilenceArguments(arguments);
            return ErrorType.Instance;
        }

        for (var position = 0; position < arguments.Count; position++)
        {
            var argument = arguments[position];
            if (argument.Expression is LambdaExpression)
            {
                continue;
            }

            var expectedType = bindings.Apply(signature.Parameters[position]);
            var actualType = argument.PreTypedType ?? TypeOf(argument.Expression, HintFor(expectedType));
            if (!bindings.TryUnify(expectedType, actualType))
            {
                Report(
                    ErrorCodes.Type0012,
                    argument.Expression.Span,
                    ("expected", bindings.Apply(signature.Parameters[position]).ToString()),
                    ("found", actualType.ToString()));
            }
        }

        var lambdaNeverReturns = false;
        var isParallelHost = lintHost is not null && LintRules.IsParallelLambdaHost(lintHost);
        for (var position = 0; position < arguments.Count; position++)
        {
            // A worker passed by name must be a top-level function: a nested function can capture a mutable
            // of its enclosing function, and a function value can hold one (SCRIPTS-TYPE-0066).
            if (isParallelHost && arguments[position].Expression is IdentifierExpression worker)
            {
                switch (FindSymbol(worker.Name))
                {
                    case FunctionSymbol { IsNested: true } nestedWorker:
                        Report(ErrorCodes.Type0066, worker.Span, ("name", nestedWorker.Name));
                        break;
                    case VariableSymbol { Type: FunctionType } functionValue:
                        Report(ErrorCodes.Type0066, worker.Span, ("name", functionValue.Name));
                        break;
                }
            }

            if (arguments[position].Expression is LambdaExpression lambda)
            {
                var expectedType = bindings.Apply(signature.Parameters[position]);
                var isParallelLambda = isParallelHost;
                if (isParallelLambda)
                {
                    parallelLambdaDepth++;
                }

                try
                {
                    var errorsBefore = sink.Errors.Count;
                    var lambdaType = TypeOf(lambda, HintFor(expectedType));
                    if (bindings.TryUnify(expectedType, lambdaType))
                    {
                        lambdaNeverReturns |= lambdaType is FunctionType { Return: var lambdaReturn } && TypeRelations.IsNever(lambdaReturn);
                    }
                    else if (expectedType is not TypeVariable and not ErrorType && sink.Errors.Count == errorsBefore)
                    {
                        // A concrete parameter type (DESIGN 3.3 rule 4): the lambda's own check deferred its report here,
                        // so the incompatible function types are named once, at the lambda (like any function value).
                        Report(
                            ErrorCodes.Type0012,
                            lambda.Span,
                            ("expected", expectedType.ToString()),
                            ("found", lambdaType.ToString()));
                    }
                }
                finally
                {
                    if (isParallelLambda)
                    {
                        parallelLambdaDepth--;
                    }
                }
            }
        }

        var returnType = bindings.Apply(signature.Return);
        if (TypeRelations.HasForbiddenUnit(returnType))
        {
            Report(ErrorCodes.Type0034, callSpan, ("context", ""));
            return ErrorType.Instance;
        }

        if (!TypeRelations.ContainsTypeVariable(returnType))
        {
            return returnType;
        }

        // The remaining type variables come from a value that binds nothing: `never` is compatible with every type, so
        // `TryUnify` deliberately leaves the variable free (a lambda that never returns). Other undetermined returns
        // (`[]`, `{}`, `ok()`…) bind `error` and never reach this branch.
        Report(
            ErrorCodes.Type0029,
            callSpan,
            ("expression", TextOf(callSpan)),
            ("context", lambdaNeverReturns
                ? "undetermined return type of the call: a lambda that never returns"
                : "undetermined return type of the call"),
            ("suggested_type", SuggestedAnnotation(returnType)));
        return TypeRelations.ReplaceTypeVariablesWithError(returnType);
    }

    /// <summary>
    /// The annotation suggested by SCRIPTS-TYPE-0029, derived from the partially applied return type instead of a fixed
    /// `list<integer>`. A remaining signature variable cannot be written by the user: it is rendered with the same
    /// placeholders as the other undetermined literals (`integer`, `string`, `error`).
    /// </summary>
    private static string SuggestedAnnotation(ScriptType returnType) => WithPlaceholderTypes(returnType).ToString();

    private static ScriptType WithPlaceholderTypes(ScriptType type) => type switch
    {
        TypeVariable { Name: "K" } => ScriptTypes.String,
        TypeVariable { Name: "E" or "F" } => new NamedType("error"),
        TypeVariable => ScriptTypes.Integer,
        ListType list => new ListType(WithPlaceholderTypes(list.Element)),
        MapType map => new MapType(WithPlaceholderTypes(map.Key), WithPlaceholderTypes(map.Value)),
        ResultType result => new ResultType(WithPlaceholderTypes(result.Success), WithPlaceholderTypes(result.Failure)),
        FunctionType function => new FunctionType(
            [.. function.Parameters.Select(WithPlaceholderTypes)],
            WithPlaceholderTypes(function.Return)),
        _ => type,
    };

    private void ReportArity(CallSignature signature, IReadOnlyList<CallArgument> arguments, Span closingParenthesis, Span anchorSpan)
    {
        var parameterCount = signature.Parameters.Count;
        var span = closingParenthesis;
        if (arguments.Count > parameterCount)
        {
            var firstExtra = arguments[parameterCount];
            span = firstExtra.PreTypedType is null ? firstExtra.Expression.Span : anchorSpan;
        }

        Report(
            ErrorCodes.Type0013,
            span,
            ("function", signature.DisplayName),
            ("expected", parameterCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("found", arguments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("signature", signature.SignatureText));
    }

    private void ApplyLint(StdlibEntry entry, IReadOnlyList<Expression> arguments, int parameterCount, Span nameSpan)
    {
        if (LintRules.IsShellEntryPoint(entry))
        {
            Report(ErrorCodes.Lint0002, nameSpan);
        }

        if (arguments.Count == 0)
        {
            return;
        }

        var firstArgument = arguments[0];
        if (LintRules.LaunchesProgram(entry))
        {
            if (LintRules.FindLauncherProgram(firstArgument) is { } program)
            {
                Report(ErrorCodes.Lint0001, firstArgument.Span, ("program", program), ("target", LauncherTargetPlaceholder));
            }
            else if (LintRules.IsComputedProgram(firstArgument))
            {
                Report(ErrorCodes.Lint0004, firstArgument.Span, ("program", TextOf(firstArgument.Span)));
            }
        }

    }
}
