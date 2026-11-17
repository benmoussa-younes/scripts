using Scripts.Core;

namespace Scripts.Checker;

internal sealed partial class FileAnalyzer
{
    /// <summary>Resolves written types; <paramref name="unitAllowed"/> is true only where `unit` may appear (DESIGN 3.1).</summary>
    private ScriptType ResolveType(TypeSyntax syntax, bool unitAllowed)
    {
        switch (syntax)
        {
            case FunctionTypeSyntax function:
                var parameters = function.ParameterTypes.Select(parameter => ResolveType(parameter, unitAllowed: false)).ToList();
                return new FunctionType(parameters, ResolveType(function.ReturnType, unitAllowed: true));
            case NamedTypeSyntax named:
                return ResolveNamedType(named, unitAllowed);
            default:
                return ErrorType.Instance;
        }
    }

    private ScriptType ResolveNamedType(NamedTypeSyntax named, bool unitAllowed)
    {
        var arguments = named.TypeArguments;
        switch (named.Name)
        {
            case "list":
                return HasTypeArgumentCount(named, 1, "list<T>")
                    ? new ListType(ResolveType(arguments[0], unitAllowed: false))
                    : ResolveArgumentsForErrors(named);
            case "map":
                return HasTypeArgumentCount(named, 2, "map<K, V>") ? ResolveMapType(arguments) : ResolveArgumentsForErrors(named);
            case "result":
                return HasTypeArgumentCount(named, 2, "result<T, E>")
                    ? new ResultType(ResolveType(arguments[0], unitAllowed: true), ResolveType(arguments[1], unitAllowed: false))
                    : ResolveArgumentsForErrors(named);
            case "never":
                Report(ErrorCodes.Type0038, named.Span);
                return ErrorType.Instance;
            case "unit":
                if (!HasTypeArgumentCount(named, 0, "unit"))
                {
                    return ResolveArgumentsForErrors(named);
                }

                if (unitAllowed)
                {
                    return ScriptTypes.Unit;
                }

                Report(ErrorCodes.Type0034, named.Span, ("context", ""));
                return ErrorType.Instance;
            default:
                if (ScriptTypes.IsPrimitiveName(named.Name) || ScriptTypes.IsNominalTypeName(named.Name))
                {
                    return HasTypeArgumentCount(named, 0, named.Name)
                        ? ScriptTypes.IsPrimitiveName(named.Name) ? new PrimitiveType(named.Name) : new NamedType(named.Name)
                        : ResolveArgumentsForErrors(named);
                }

                if (RetiredNames.TryType(named.Name, out var replacement))
                {
                    Report(
                        ErrorCodes.Type0068,
                        named.Span,
                        new Fix($"rename to `{replacement}`", source, named.Span, replacement),
                        ("old", named.Name),
                        ("new", $"`{replacement}`"));
                }
                else
                {
                    Report(ErrorCodes.Type0036, named.Span, ("name", named.Name));
                }

                return ResolveArgumentsForErrors(named);
        }
    }

    private ScriptType ResolveMapType(IReadOnlyList<TypeSyntax> arguments)
    {
        var key = ResolveType(arguments[0], unitAllowed: false);
        var value = ResolveType(arguments[1], unitAllowed: false);
        if (TypeRelations.IsValidMapKey(key))
        {
            return new MapType(key, value);
        }

        Report(ErrorCodes.Type0035, arguments[0].Span, ("type", key.ToString()));
        return ErrorType.Instance;
    }

    private bool HasTypeArgumentCount(NamedTypeSyntax named, int expectedCount, string usage)
    {
        if (named.TypeArguments.Count == expectedCount)
        {
            return true;
        }

        Report(
            ErrorCodes.Type0037,
            named.Span,
            ("type", named.Name),
            ("expected", expectedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("found", named.TypeArguments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("usage", usage));
        return false;
    }

    private ErrorType ResolveArgumentsForErrors(NamedTypeSyntax named)
    {
        foreach (var argument in named.TypeArguments)
        {
            ResolveType(argument, unitAllowed: false);
        }

        return ErrorType.Instance;
    }

    private FunctionSymbol CreateFunctionSymbol(FunctionDeclaration declaration)
    {
        var parameterTypes = declaration.Parameters.Select(parameter => ResolveType(parameter.Type, unitAllowed: false)).ToList();
        var returnType = declaration.ReturnType is null ? ScriptTypes.Unit : ResolveType(declaration.ReturnType, unitAllowed: true);
        return new FunctionSymbol(declaration, parameterTypes, returnType);
    }
}
