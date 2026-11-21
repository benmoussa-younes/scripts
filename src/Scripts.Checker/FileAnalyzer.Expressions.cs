using Scripts.Core;

namespace Scripts.Checker;

internal sealed partial class FileAnalyzer
{
    /// <summary>Types an expression (DESIGN 3.3); the expected type only guides literals, constructors and lambdas, and an error type silences it.</summary>
    private ScriptType TypeOf(Expression expression, ScriptType? expected)
    {
        var type = TypeOfCore(expression, expected);
        expression.ResolvedType = type;
        return type;
    }

    private ScriptType CheckAgainst(Expression expression, ScriptType expected)
    {
        var actual = TypeOf(expression, expected);
        if (!TypeRelations.IsCompatible(expected, actual))
        {
            Report(ErrorCodes.Type0012, expression.Span, ("expected", expected.ToString()), ("found", actual.ToString()));
        }

        return actual;
    }

    private ScriptType TypeOfCore(Expression expression, ScriptType? expected)
    {
        switch (expression)
        {
            case IntegerLiteral:
                return ScriptTypes.Integer;
            case FloatLiteral:
                return ScriptTypes.Float;
            case BooleanLiteral:
                return ScriptTypes.Boolean;
            case StringLiteral literal:
                LintDollarName(literal.Span, isRaw: source.Text[literal.Span.Start] == 'r');
                return ScriptTypes.String;
            case InterpolatedString interpolated:
                CheckInterpolatedParts(interpolated.Parts);
                LintDollarName(interpolated.Parts);
                return ScriptTypes.String;
            case PathLiteral path:
                CheckInterpolatedParts(path.Parts);
                return ScriptTypes.Path;
            case IdentifierExpression identifier:
                return TypeOfIdentifier(identifier);
            case ListLiteral list:
                return TypeOfListLiteral(list, expected);
            case MapLiteral map:
                return TypeOfMapLiteral(map, expected);
            case UnaryExpression unary:
                return TypeOfUnary(unary);
            case BinaryExpression binary:
                return TypeOfBinary(binary);
            case CallExpression call:
                return TypeOfCall(call);
            case MemberAccessExpression member:
                return TypeOfMemberAccess(member);
            case IndexExpression index:
                return TypeOfIndex(index);
            case PropagateExpression propagate:
                return TypeOfPropagate(propagate);
            case LambdaExpression lambda:
                return TypeOfLambda(lambda, expected);
            case OkExpression ok:
                return TypeOfOk(ok, expected);
            case ErrorExpression error:
                return TypeOfErrorConstructor(error, expected);
            case ErrorConstructorExpression errorConstructor:
                return TypeOfErrorValueConstructor(errorConstructor, expected);
            case MatchExpression match:
                return TypeOfMatch(match, expected);
            default:
                return ErrorType.Instance;
        }
    }

    private void CheckInterpolatedParts(IReadOnlyList<StringPart> parts)
    {
        foreach (var part in parts.OfType<InterpolationPart>())
        {
            var type = TypeOf(part.Expression, null);
            if (!TypeRelations.IsInterpolable(type) && !TypeRelations.IsNever(type))
            {
                Report(ErrorCodes.Type0023, part.Expression.Span, ("type", type.ToString()));
            }
        }
    }

    /// <summary>SCRIPTS-LINT-0003: `$name` in a string literal (the bash habit); raw strings are exempt (they are the fix).</summary>
    private void LintDollarName(Span span, bool isRaw = false)
    {
        if (!isRaw)
        {
            LintDollarNameIn(span.Start, Math.Min(span.End, source.Text.Length));
        }
    }

    private void LintDollarName(IReadOnlyList<StringPart> parts)
    {
        foreach (var part in parts.OfType<TextPart>())
        {
            LintDollarName(part.Span);
        }
    }

    private void LintDollarNameIn(int start, int end)
    {
        var text = source.Text;
        for (var index = start; index + 1 < end; index++)
        {
            if (text[index] == '$' && IsIdentifierStart(text[index + 1]))
            {
                Report(ErrorCodes.Lint0003, new Span(index, index + 1));
            }
        }
    }

    private static bool IsIdentifierStart(char character) =>
        character == '_' || (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z');

    private ScriptType TypeOfIdentifier(IdentifierExpression identifier)
    {
        switch (FindSymbol(identifier.Name))
        {
            case VariableSymbol variable:
                variable.IsRead = true;
                RejectCapturedReferenceInParallelWorker(variable.Type is FunctionType, identifier.Name, identifier.Span);
                return variable.Type;
            case FunctionSymbol function:
                RejectCapturedReferenceInParallelWorker(function.IsNested, function.Name, identifier.Span);
                return function.Type;
            case AliasSymbol:
                Report(ErrorCodes.Type0059, identifier.Span, ("name", identifier.Name));
                return ErrorType.Instance;
        }

        if (StdlibCatalog.IsGlobalFunctionName(identifier.Name))
        {
            Report(ErrorCodes.Type0056, identifier.Span, ("name", identifier.Name));
        }
        else if (StdlibCatalog.IsModuleName(identifier.Name))
        {
            Report(ErrorCodes.Type0059, identifier.Span, ("name", identifier.Name));
        }
        else
        {
            ReportUnknownName(identifier.Name, identifier.Span);
        }

        return ErrorType.Instance;
    }

    /// <summary>
    /// A parallel worker runs concurrently and must not reach state that can be mutated under it. A nested
    /// function can capture a `mutable` of its enclosing function, and a function value can hold one; neither
    /// can be proven side-effect free, so no reference to either is allowed inside a worker lambda
    /// (SCRIPTS-TYPE-0066). A top-level function cannot see a script `mutable` (SCRIPTS-TYPE-0007) and stays allowed.
    /// </summary>
    private void RejectCapturedReferenceInParallelWorker(bool mayCaptureState, string name, Span span)
    {
        if (parallelLambdaDepth > 0 && mayCaptureState)
        {
            Report(ErrorCodes.Type0066, span, ("name", name));
        }
    }

    private static bool NeedsExpectedType(Expression expression) =>
        expression is ListLiteral { Elements.Count: 0 } or MapLiteral { Entries.Count: 0 } or OkExpression or ErrorExpression or LambdaExpression;

    private static ScriptType? KnownOrNull(ScriptType? type) => type is null || TypeRelations.ContainsTypeVariable(type) ? null : type;

    private ScriptType TypeOfListLiteral(ListLiteral list, ScriptType? expected)
    {
        var expectedElement = KnownOrNull(expected switch
        {
            ListType expectedList => expectedList.Element,
            ErrorType => ErrorType.Instance,
            _ => null,
        });
        if (list.Elements.Count == 0)
        {
            return expectedElement is not null ? new ListType(expectedElement) : ReportUndeterminedLiteral(list.Span, expected, "list<_>", "undetermined element type of the list", "list<integer>");
        }

        ScriptType elementType;
        if (expectedElement is not null)
        {
            foreach (var element in list.Elements)
            {
                CheckAgainst(element, expectedElement);
            }

            elementType = expectedElement;
        }
        else
        {
            elementType = TypeElementsFromAnchor(list.Elements);
        }

        return new ListType(elementType);
    }

    /// <summary>Types the elements without guidance: the first self-typed element fixes the type, the others must match it.</summary>
    private ScriptType TypeElementsFromAnchor(IReadOnlyList<Expression> elements)
    {
        var anchorPosition = 0;
        for (var position = 0; position < elements.Count; position++)
        {
            if (!NeedsExpectedType(elements[position]))
            {
                anchorPosition = position;
                break;
            }
        }

        var anchorType = TypeOf(elements[anchorPosition], null);
        if (TypeRelations.IsUnit(anchorType) || TypeRelations.IsNever(anchorType))
        {
            Report(ErrorCodes.Type0034, elements[anchorPosition].Span, ("context", ""));
            anchorType = ErrorType.Instance;
        }

        for (var position = 0; position < elements.Count; position++)
        {
            if (position != anchorPosition)
            {
                CheckAgainst(elements[position], anchorType);
            }
        }

        return anchorType;
    }

    private ScriptType ReportUndeterminedLiteral(Span span, ScriptType? expected, string foundText, string context, string suggestedType)
    {
        if (expected is null or TypeVariable || TypeRelations.ContainsTypeVariable(expected))
        {
            Report(
                ErrorCodes.Type0029,
                span,
                ("expression", TextOf(span)),
                ("context", context),
                ("suggested_type", suggestedType));
        }
        else
        {
            Report(ErrorCodes.Type0012, span, ("expected", expected.ToString()), ("found", foundText));
        }

        return ErrorType.Instance;
    }

    private ScriptType TypeOfMapLiteral(MapLiteral map, ScriptType? expected)
    {
        var expectedKey = KnownOrNull(expected switch
        {
            MapType expectedMap => expectedMap.Key,
            ErrorType => ErrorType.Instance,
            _ => null,
        });
        var expectedValue = KnownOrNull(expected switch
        {
            MapType expectedMap => expectedMap.Value,
            ErrorType => ErrorType.Instance,
            _ => null,
        });
        if (map.Entries.Count == 0)
        {
            return expectedKey is not null && expectedValue is not null
                ? new MapType(expectedKey, expectedValue)
                : ReportUndeterminedLiteral(map.Span, expected, "map<_, _>", "undetermined key and value types of the map", "map<string, integer>");
        }

        var keys = map.Entries.Select(entry => entry.Key).ToList();
        var values = map.Entries.Select(entry => entry.Value).ToList();
        var keyType = TypeEntries(keys, expectedKey);
        var valueType = TypeEntries(values, expectedValue);
        if (expectedKey is null && !TypeRelations.IsValidMapKey(keyType))
        {
            Report(ErrorCodes.Type0035, keys[0].Span, ("type", keyType.ToString()));
            keyType = ErrorType.Instance;
        }

        ReportDuplicateConstantKeys(map);
        return new MapType(keyType, valueType);
    }

    private ScriptType TypeEntries(IReadOnlyList<Expression> expressions, ScriptType? expectedType)
    {
        if (expectedType is null)
        {
            return TypeElementsFromAnchor(expressions);
        }

        foreach (var expression in expressions)
        {
            CheckAgainst(expression, expectedType);
        }

        return expectedType;
    }

    private void ReportDuplicateConstantKeys(MapLiteral map)
    {
        var firstKeySpans = new Dictionary<ConstantValue, Span>();
        foreach (var entry in map.Entries)
        {
            if (ConstantFolding.TryFold(entry.Key, LookupConstantValue) is not { } value)
            {
                continue;
            }

            if (firstKeySpans.TryGetValue(value, out var firstSpan))
            {
                Report(ErrorCodes.Type0055, entry.Key.Span, ("key", TextOf(entry.Key.Span)), ("line", LineTextOf(firstSpan)));
            }
            else
            {
                firstKeySpans[value] = entry.Key.Span;
            }
        }
    }

    private ScriptType TypeOfIndex(IndexExpression index)
    {
        var targetType = TypeOf(index.Target, null);
        switch (targetType)
        {
            case ListType list:
                CheckAgainst(index.Index, ScriptTypes.Integer);
                return list.Element;
            case MapType map:
                CheckAgainst(index.Index, map.Key);
                return map.Value;
            default:
                if (targetType is not ErrorType)
                {
                    Report(ErrorCodes.Type0041, index.Target.Span, ("type", targetType.ToString()));
                }

                TypeOf(index.Index, ErrorType.Instance);
                return ErrorType.Instance;
        }
    }

    private ScriptType TypeOfPropagate(PropagateExpression propagate)
    {
        var operandType = TypeOf(propagate.Operand, null);
        var questionSpan = new Span(propagate.Span.End - 1, propagate.Span.End);
        if (lambdaDepth > 0)
        {
            Report(ErrorCodes.Type0044, questionSpan);
            return operandType is ResultType lambdaResult ? lambdaResult.Success : ErrorType.Instance;
        }

        if (operandType is ErrorType)
        {
            return operandType;
        }

        if (operandType is not ResultType result)
        {
            Report(ErrorCodes.Type0045, questionSpan, ("found", operandType.ToString()));
            return ErrorType.Instance;
        }

        CheckPropagatedErrorType(result.Failure, questionSpan);
        return result.Success;
    }

    private void CheckPropagatedErrorType(ScriptType failure, Span questionSpan)
    {
        if (failure is ErrorType)
        {
            return;
        }

        if (currentFunction is null)
        {
            if (failure is not PrimitiveType { Name: "string" } && !(failure is NamedType named && ScriptTypes.IsErrorTypeName(named.Name)))
            {
                Report(ErrorCodes.Type0047, questionSpan, ("found", failure.ToString()));
            }

            return;
        }

        if (currentFunction.ReturnType is ErrorType
            || (currentFunction.ReturnType is ResultType returned && TypeRelations.IsSame(returned.Failure, failure)))
        {
            return;
        }

        Report(
            ErrorCodes.Type0046,
            questionSpan,
            ("found", failure.ToString()),
            ("function", currentFunction.Name),
            ("expected", currentFunction.ReturnType.ToString()));
    }

    private ScriptType TypeOfOk(OkExpression ok, ScriptType? expected)
    {
        if (expected is ErrorType)
        {
            SilenceValue(ok.Value);
            return ErrorType.Instance;
        }

        if (expected is not ResultType resultExpected)
        {
            return ReportConstructorWithoutResult(ok.Span, ok.Value, expected, isOk: true);
        }

        if (TypeRelations.ContainsTypeVariable(resultExpected.Failure))
        {
            SilenceValue(ok.Value);
            Report(
                ErrorCodes.Type0029,
                ok.Span,
                ("expression", TextOf(ok.Span)),
                ("context", "undetermined error type of the `result`"),
                ("suggested_type", $"result<{resultExpected.Success}, error>"));
            return ErrorType.Instance;
        }

        if (ok.Value is null)
        {
            if (TypeRelations.IsUnit(resultExpected.Success) || resultExpected.Success is ErrorType)
            {
                return new ResultType(ScriptTypes.Unit, resultExpected.Failure);
            }

            Report(ErrorCodes.Type0033, ok.Span, ("expected", resultExpected.ToString()));
            return ErrorType.Instance;
        }

        return new ResultType(TypeConstructorValue(ok.Value, resultExpected.Success), resultExpected.Failure);
    }

    private ScriptType TypeOfErrorConstructor(ErrorExpression error, ScriptType? expected)
    {
        if (expected is ErrorType)
        {
            SilenceValue(error.Value);
            return ErrorType.Instance;
        }

        if (expected is not ResultType resultExpected)
        {
            return ReportConstructorWithoutResult(error.Span, error.Value, expected, isOk: false);
        }

        if (TypeRelations.ContainsTypeVariable(resultExpected.Success))
        {
            SilenceValue(error.Value);
            Report(
                ErrorCodes.Type0029,
                error.Span,
                ("expression", TextOf(error.Span)),
                ("context", "undetermined success type of the `result`"),
                ("suggested_type", $"result<integer, {resultExpected.Failure}>"));
            return ErrorType.Instance;
        }

        return new ResultType(resultExpected.Success, TypeConstructorValue(error.Value, resultExpected.Failure));
    }

    /// <summary>error(kind, subject, message): a value of type `error`, or the failure branch of an expected result (DESIGN 3.5.1, TYPE-0062).</summary>
    private ScriptType TypeOfErrorValueConstructor(ErrorConstructorExpression constructor, ScriptType? expected)
    {
        CheckErrorConstructorKind(constructor.Kind);
        CheckAgainst(constructor.Subject, ScriptTypes.String);
        CheckAgainst(constructor.Message, ScriptTypes.String);
        if (expected is ResultType resultExpected)
        {
            if (!TypeRelations.IsCompatible(resultExpected.Failure, new NamedType("error")))
            {
                Report(
                    ErrorCodes.Type0012,
                    constructor.Span,
                    ("expected", resultExpected.ToString()),
                    ("found", $"result<{resultExpected.Success}, error>"));
            }

            return new ResultType(resultExpected.Success, resultExpected.Failure);
        }

        return new NamedType("error");
    }

    /// <summary>TYPE-0062: the first argument of error(kind, subject, message) must be a literal from the closed set.</summary>
    private void CheckErrorConstructorKind(Expression kind)
    {
        var kinds = BuiltinTypes.KindsOf("error") ?? [];
        if (kind is StringLiteral literal && kinds.Contains(literal.Value))
        {
            return;
        }

        var available = string.Join(", ", kinds);
        if (kind is StringLiteral wrong)
        {
            var closest = SimilarNames.FindClosest(wrong.Value, kinds);
            // The suggestion promises "fix: …"; carry the edit so an agent can apply it (audit F11).
            var fix = closest is not null
                ? new Fix($"replace with `{closest}`", source, wrong.Span, "\"" + closest + "\"")
                : null;
            Report(
                ErrorCodes.Type0062,
                wrong.Span,
                fix,
                ("literal", "\"" + wrong.Value + "\""),
                ("detail", "literal outside the closed set"),
                ("available", available),
                ("closest", closest ?? SimilarNames.NoneText));
            return;
        }

        TypeOf(kind, ScriptTypes.String);
        Report(
            ErrorCodes.Type0062,
            kind.Span,
            ("literal", TextOf(kind.Span)),
            ("detail", "a literal `kind` is required"),
            ("available", available),
            ("closest", SimilarNames.NoneText));
    }

    /// <summary>Checks the payload of ok(...)/error(...) against the expected part, or infers that part when it is still a variable.</summary>
    private ScriptType TypeConstructorValue(Expression value, ScriptType expectedPart)
    {
        if (!TypeRelations.ContainsTypeVariable(expectedPart))
        {
            var checkedType = CheckAgainst(value, expectedPart);
            if (TypeRelations.IsUnit(checkedType))
            {
                Report(ErrorCodes.Type0034, value.Span, ("context", ""));
            }

            return expectedPart;
        }

        var valueType = TypeOf(value, null);
        if (TypeRelations.IsUnit(valueType) || TypeRelations.IsNever(valueType))
        {
            Report(ErrorCodes.Type0034, value.Span, ("context", ""));
            return ErrorType.Instance;
        }

        return valueType;
    }

    private void SilenceValue(Expression? value)
    {
        if (value is not null)
        {
            TypeOf(value, ErrorType.Instance);
        }
    }

    private ScriptType ReportConstructorWithoutResult(Span span, Expression? value, ScriptType? expected, bool isOk)
    {
        var valueType = value is null ? ScriptTypes.Unit : TypeOf(value, ErrorType.Instance);
        if (expected is null or TypeVariable || TypeRelations.ContainsTypeVariable(expected))
        {
            var suggested = isOk
                ? $"result<{valueType}, error>"
                : $"result<integer, {valueType}>";
            Report(
                ErrorCodes.Type0029,
                span,
                ("expression", TextOf(span)),
                ("context", isOk ? "undetermined error type of the `result`" : "undetermined success type of the `result`"),
                ("suggested_type", suggested));
        }
        else if (isOk && value is null)
        {
            Report(ErrorCodes.Type0033, span, ("expected", expected.ToString()));
        }
        else
        {
            var found = isOk ? $"result<{valueType}, _>" : $"result<_, {valueType}>";
            Report(ErrorCodes.Type0012, span, ("expected", expected.ToString()), ("found", found));
        }

        return ErrorType.Instance;
    }

    private ScriptType TypeOfMemberAccess(MemberAccessExpression member)
    {
        if (member.Target is IdentifierExpression targetIdentifier)
        {
            var symbol = FindSymbol(targetIdentifier.Name);
            if (symbol is AliasSymbol alias)
            {
                return TypeOfModuleMember(member, alias);
            }

            if (symbol is null && StdlibCatalog.IsModuleName(targetIdentifier.Name))
            {
                return TypeOfStandardModuleMember(member, targetIdentifier.Name);
            }
        }

        var targetType = TypeOf(member.Target, null);
        if (targetType is ErrorType)
        {
            return targetType;
        }

        // Reading a field of a record is using the value (SCRIPTS-LINT-0006); only an
        // identifier alone is not a read, because it produces no observable effect.
        if (member.Target is IdentifierExpression fieldTarget
            && FindSymbol(fieldTarget.Name) is VariableSymbol fieldVariable)
        {
            fieldVariable.IsRead = true;
            fieldVariable.ReadMembers.Add(member.MemberName);
        }

        if (targetType is not NamedType named || !ScriptTypes.IsRecordOrErrorName(named.Name))
        {
            // When the name exists as a method of this type, name it: `.length` failing on a
            // list says nothing a user can act on, `.length()` is the fix (SCRIPTS-TYPE-0010).
            var receiverKind = TypeRelations.ReceiverKindOf(targetType);
            var sameNameMethod = receiverKind is not null
                && SignatureCatalog.MethodNames(receiverKind).Contains(member.MemberName, StringComparer.Ordinal);
            Report(
                ErrorCodes.Type0010,
                member.MemberSpan,
                ("type", targetType.ToString()),
                ("field", member.MemberName),
                ("hint", sameNameMethod
                    ? $"`{member.MemberName}` is a method of this type: call it with parentheses, `.{member.MemberName}()`"
                    : "check the type of the value: `.name` is a field access, a method needs `.name(…)`"));
            return ErrorType.Instance;
        }

        var fieldType = BuiltinTypes.FieldType(named.Name, member.MemberName);
        if (fieldType is null)
        {
            if (RetiredNames.TryField(named.Name, member.MemberName, out var replacement))
            {
                Report(
                    ErrorCodes.Type0068,
                    member.MemberSpan,
                    new Fix($"rename to `{replacement}`", source, member.MemberSpan, replacement),
                    ("old", member.MemberName),
                    ("new", $"`{replacement}`"));
                return ErrorType.Instance;
            }

            var available = string.Join(", ", BuiltinTypes.FieldsOf(named.Name).Select(field => field.Name));
            Report(ErrorCodes.Type0009, member.MemberSpan, ("field", member.MemberName), ("type", named.Name), ("available", available));
            return ErrorType.Instance;
        }

        member.AccessKind = MemberAccessKind.RecordField;
        return fieldType;
    }

    private ScriptType TypeOfModuleMember(MemberAccessExpression member, AliasSymbol alias)
    {
        if (alias.Info.Constants.TryGetValue(member.MemberName, out var constant))
        {
            if (constant.Declaration?.Visibility == Visibility.Private)
            {
                ReportPrivateMember(member.MemberName, member.MemberSpan, alias);
                return ErrorType.Instance;
            }

            member.AccessKind = MemberAccessKind.ModuleConstant;
            member.ConstantDeclaration = constant.Declaration;
            return constant.Type;
        }

        if (alias.Info.Functions.TryGetValue(member.MemberName, out var function))
        {
            if (function.Declaration.Visibility == Visibility.Private)
            {
                ReportPrivateMember(member.MemberName, member.MemberSpan, alias);
            }
            else
            {
                Report(ErrorCodes.Module0010, member.MemberSpan, ("alias", alias.Name), ("member", member.MemberName));
            }

            return ErrorType.Instance;
        }

        ReportUnknownModuleMember(member.MemberName, member.MemberSpan, alias);
        return ErrorType.Instance;
    }

    private ScriptType TypeOfStandardModuleMember(MemberAccessExpression member, string moduleName)
    {
        if (StdlibCatalog.FindModuleFunction(moduleName, member.MemberName) is not null)
        {
            Report(ErrorCodes.Type0056, member.Span, ("name", moduleName + "." + member.MemberName));
        }
        else
        {
            ReportUnknownStandardMember(moduleName, member.MemberName, member.MemberSpan);
        }

        return ErrorType.Instance;
    }

    private void ReportPrivateMember(string memberName, Span span, AliasSymbol alias) =>
        Report(ErrorCodes.Module0004, span, ("member", memberName), ("alias", alias.Name), ("module_file", alias.Module.Source.Path));

    private void ReportUnknownModuleMember(string memberName, Span span, AliasSymbol alias) =>
        Report(
            ErrorCodes.Module0006,
            span,
            ("alias", alias.Name),
            ("member", memberName),
            ("available", SimilarNames.JoinAvailable(alias.Info.PublicMemberNames)));

    private void ReportUnknownStandardMember(string moduleName, string memberName, Span span)
    {
        if (RetiredNames.TryModuleMember(moduleName, memberName, out var replacement))
        {
            Report(
                ErrorCodes.Type0068,
                span,
                new Fix($"rename to `{replacement}`", source, span, replacement),
                ("old", memberName),
                ("new", $"`{replacement}`"));
            return;
        }

        if (RetiredNames.TryModuleMemberConversion(moduleName, memberName, out var rewrite))
        {
            Report(ErrorCodes.Type0068, span, ("old", $"{moduleName}.{memberName}"), ("new", rewrite));
            return;
        }

        Report(
            ErrorCodes.Type0011,
            span,
            ("member", memberName),
            ("module", moduleName),
            ("available", SimilarNames.JoinAvailableWithClosest(SignatureCatalog.ModuleFunctionNames(moduleName), memberName)));
    }
}
