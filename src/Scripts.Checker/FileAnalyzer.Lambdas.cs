using Scripts.Core;

namespace Scripts.Checker;

internal sealed partial class FileAnalyzer
{
    private ScriptType TypeOfLambda(LambdaExpression lambda, ScriptType? expected)
    {
        var expectedFunction = expected as FunctionType;
        var isSilent = expected is ErrorType;
        if (expectedFunction is not null && expectedFunction.Parameters.Count != lambda.Parameters.Count)
        {
            Report(
                ErrorCodes.Type0032,
                lambda.Span,
                ("found", lambda.Parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("expected", expectedFunction.Parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            expectedFunction = null;
            isSilent = true;
        }
        else if (expected is not null and not FunctionType and not ErrorType and not TypeVariable)
        {
            Report(ErrorCodes.Type0031, lambda.Span, ("expected", expected.ToString()));
            isSilent = true;
        }

        var parameterTypes = ResolveLambdaParameterTypes(lambda, expectedFunction, isSilent, out var isMissingAnnotation);
        if (isMissingAnnotation)
        {
            Report(ErrorCodes.Type0030, lambda.Span);
        }

        var enclosingScope = currentScope;
        currentScope = new Scope(enclosingScope);
        // A lambda body never leaves through an enclosing loop: `break`/`continue` in one of its block arms are TYPE-0073.
        var enclosingLoops = loopStack;
        loopStack = [];
        lambdaDepth++;
        DeclareLambdaParameters(lambda, parameterTypes);
        var lambdaType = TypeOfLambdaBody(lambda, parameterTypes, expectedFunction, isSilent || isMissingAnnotation);
        lambdaDepth--;
        loopStack = enclosingLoops;
        currentScope = enclosingScope;
        return lambdaType;
    }

    private List<ScriptType> ResolveLambdaParameterTypes(LambdaExpression lambda, FunctionType? expectedFunction, bool isSilent, out bool isMissingAnnotation)
    {
        isMissingAnnotation = false;
        // A concrete expected function type is compared as a whole by the caller: a call names the two function types
        // at the lambda (SCRIPTS-TYPE-0012), and CheckAgainst does the same. Reporting the parameter here would duplicate it.
        // A type-variable context keeps the precise parameter report, because the whole signature would leak a variable
        // (`function(integer) -> U`) that no user can write.
        var reportParameterMismatch = expectedFunction is null || TypeRelations.ContainsTypeVariable(expectedFunction);
        var parameterTypes = new List<ScriptType>();
        for (var position = 0; position < lambda.Parameters.Count; position++)
        {
            var parameter = lambda.Parameters[position];
            var annotated = parameter.Annotation is null ? null : ResolveType(parameter.Annotation, unitAllowed: false);
            var fromContext = expectedFunction is null ? null : TypeRelations.ReplaceTypeVariablesWithError(expectedFunction.Parameters[position]);
            if (annotated is not null)
            {
                if (fromContext is not null && reportParameterMismatch && !TypeRelations.IsCompatible(fromContext, annotated))
                {
                    Report(ErrorCodes.Type0012, parameter.Annotation!.Span, ("expected", fromContext.ToString()), ("found", annotated.ToString()));
                }

                parameterTypes.Add(annotated);
            }
            else if (fromContext is not null)
            {
                parameterTypes.Add(fromContext);
            }
            else
            {
                isMissingAnnotation |= !isSilent;
                parameterTypes.Add(ErrorType.Instance);
            }
        }

        return parameterTypes;
    }

    private void DeclareLambdaParameters(LambdaExpression lambda, List<ScriptType> parameterTypes)
    {
        var firstSpans = new Dictionary<string, Span>(StringComparer.Ordinal);
        for (var position = 0; position < lambda.Parameters.Count; position++)
        {
            var parameter = lambda.Parameters[position];
            var nameSpan = new Span(parameter.Span.Start, parameter.Span.Start + parameter.Name.Length);
            if (parameter.Name != "_" && firstSpans.TryGetValue(parameter.Name, out var firstSpan))
            {
                Report(ErrorCodes.Type0006, nameSpan, ("name", parameter.Name), ("line", LineTextOf(firstSpan)));
                continue;
            }

            firstSpans[parameter.Name] = nameSpan;
            DeclareVariable(parameter.Name, nameSpan, VariableKind.LambdaParameter, false, parameterTypes[position], null);
        }
    }

    private ScriptType TypeOfLambdaBody(LambdaExpression lambda, List<ScriptType> parameterTypes, FunctionType? expectedFunction, bool isSilent)
    {
        if (isSilent)
        {
            TypeOf(lambda.Body, ErrorType.Instance);
            return ErrorType.Instance;
        }

        if (expectedFunction is not null && !TypeRelations.ContainsTypeVariable(expectedFunction.Return))
        {
            CheckAgainst(lambda.Body, expectedFunction.Return);
            return new FunctionType(parameterTypes, expectedFunction.Return);
        }

        var bodyType = TypeOf(lambda.Body, null);
        if (expectedFunction is not null && TypeRelations.IsUnit(bodyType))
        {
            Report(ErrorCodes.Type0034, lambda.Body.Span, ("context", ""));
            bodyType = ErrorType.Instance;
        }

        return new FunctionType(parameterTypes, bodyType);
    }
}
