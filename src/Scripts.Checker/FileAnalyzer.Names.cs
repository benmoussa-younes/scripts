using Scripts.Core;

namespace Scripts.Checker;

internal sealed partial class FileAnalyzer
{
    private Symbol? FindSymbol(string name)
    {
        for (var scope = currentScope; scope is not null; scope = scope.Parent)
        {
            if (scope.Symbols.TryGetValue(name, out var symbol))
            {
                return symbol;
            }
        }

        return null;
    }

    private bool TryFindPendingDeclaration(string name, out Span declarationSpan)
    {
        for (var scope = currentScope; scope is not null; scope = scope.Parent)
        {
            if (scope.PendingDeclarations.TryGetValue(name, out declarationSpan))
            {
                return true;
            }
        }

        declarationSpan = default;
        return false;
    }

    /// <summary>All names visible from the current scope, deduplicated and ordinal-sorted, cached (A4).</summary>
    private IReadOnlyList<string> SortedVisibleNames()
    {
        if (visibleNamesCache is not null
            && ReferenceEquals(visibleNamesCacheScope, currentScope)
            && visibleNamesCacheDeclarationCount == declarationCount)
        {
            return visibleNamesCache;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var scope = currentScope; scope is not null; scope = scope.Parent)
        {
            foreach (var name in scope.Symbols.Keys)
            {
                names.Add(name);
            }
        }

        foreach (var name in SignatureCatalog.GlobalFunctionNames())
        {
            names.Add(name);
        }

        // Every intrinsic namespace, `ok` and `error` belong to the name pool: suggesting a
        // variable when the user typed `string.split(…)` hides the real fix (SCRIPTS-TYPE-0001).
        foreach (var namespaceName in StdlibCatalog.IntrinsicModuleNames)
        {
            names.Add(namespaceName);
        }

        names.Add("ok");
        names.Add("error");

        var sorted = names.ToList();
        sorted.Sort(StringComparer.Ordinal);
        visibleNamesCache = sorted;
        visibleNamesCacheScope = currentScope;
        visibleNamesCacheDeclarationCount = declarationCount;
        return sorted;
    }

    /// <summary>
    /// The suggestion of SCRIPTS-TYPE-0001. A name that exists as a method is answered with the
    /// receiver form, because that is the fix a user of `string.split(s, ",")` needs; a name that
    /// looks like a namespace but is not one says so instead of offering a close variable.
    /// Without a close name the fragment is still actionable: declaring, importing or fixing the
    /// spelling are the three ways a name becomes visible.
    /// </summary>
    private string ExplainUnknownName(string name)
    {
        if (StdlibCatalog.IsMethodName(name))
        {
            return $"`{name}` is a method, not a function: write `receiver.{name}(…)`.";
        }

        // A type name used as if it were a namespace: `string.split(s, ",")` is the method
        // spelling of `s.split(",")`, and suggesting a close variable hides that.
        if (name is "string" or "integer" or "list" or "map" or "result")
        {
            var example = name == "string" ? "text.split(\",\")" : "items.length()";
            return $"`{name}` is a type, not a module: a method is called on its receiver, as `{example}`.";
        }

        var similar = SimilarNames.FindSimilar(name, SortedVisibleNames(), sorted: true);
        // The catalog already says "declare `x` before its use, or fix the typo."; the fallback adds the
        // only missing hint without repeating it.
        return similar == SimilarNames.NoSimilarText
            ? "It may come from a module: add the `import`."
            : similar;
    }

    /// <summary>TYPE-0002, TYPE-0007 or TYPE-0001 for a name that no visible scope declares.</summary>
    private void ReportUnknownName(string name, Span span)
    {
        if (TryFindPendingDeclaration(name, out var declarationSpan))
        {
            Report(ErrorCodes.Type0002, span, ("name", name), ("line", LineTextOf(declarationSpan)));
        }
        else if (enclosingTopLevelFunctionName is not null && scriptVariableDeclarations.TryGetValue(name, out var declaration))
        {
            var earlierConstants = EarlierConstantNames(declaration);
            Report(
                ErrorCodes.Type0007,
                span,
                ("name", name),
                ("function", enclosingTopLevelFunctionName),
                ("reason", ConstantReasons.Describe(declaration, file, earlierConstants)));
        }
        else if (RetiredNames.TryGlobal(name, out var replacement))
        {
            Report(ErrorCodes.Type0068, span, new Fix($"rename to `{replacement}`", source, span, replacement), ("old", name), ("new", $"`{replacement}`"));
        }
        else if (RetiredNames.TryConversion(name, out var rewrite))
        {
            Report(ErrorCodes.Type0068, span, ("old", name), ("new", rewrite));
        }
        else
        {
            var closestName = SimilarNames.FindFixCandidate(name, SortedVisibleNames(), sorted: true);
            // A module alias is a visible name but not a value: renaming to it would trade one static error
            // for another (audit F6), so no machine fix is offered for that candidate.
            var nameFix = closestName is not null && FindSymbol(closestName) is not AliasSymbol
                ? new Fix($"rename to `{closestName}`", source, span, closestName)
                : null;
            Report(ErrorCodes.Type0001, span, nameFix, ("name", name), ("similar", ExplainUnknownName(name)));
        }
    }

    private HashSet<string> EarlierConstantNames(LetStatement declaration)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in file.Program.Statements)
        {
            if (statement is LetStatement { IsConstant: true } constant && constant.Span.Start < declaration.Span.Start)
            {
                names.Add(constant.Name);
            }
        }

        return names;
    }

    /// <summary>Reserved and standard names cannot be declared (TYPE-0004, TYPE-0005); the declaration is still recorded so later uses do not cascade.</summary>
    private bool CheckNameIsDeclarable(string name, Span nameSpan)
    {
        if (LanguageTables.IsReservedIdentifier(name))
        {
            Report(ErrorCodes.Type0004, nameSpan, ("name", name));
            return false;
        }

        if (StdlibCatalog.IsGlobalFunctionName(name))
        {
            Report(ErrorCodes.Type0005, nameSpan, ("name", name));
            return false;
        }

        return true;
    }

    /// <summary>TYPE-0003 on the textually later of the two declarations; the message cites the earlier one and its nature.</summary>
    private void ReportShadowing(string name, Span first, Span second, string earlierKind)
    {
        var (earlier, later) = first.Start <= second.Start ? (first, second) : (second, first);
        Report(ErrorCodes.Type0003, later, ("name", name), ("line", LineTextOf(earlier)), ("kind", earlierKind));
    }

    /// <summary>The nature of a symbol, for the TYPE-0003 message ({kind}).</summary>
    private static string DescribeSymbolKind(Symbol symbol) => symbol switch
    {
        FunctionSymbol => "function",
        AliasSymbol => "import alias",
        VariableSymbol variable => variable.Kind switch
        {
            VariableKind.Constant => "constant",
            VariableKind.ScriptVariable => "script variable",
            VariableKind.LocalVariable => "local variable",
            VariableKind.Parameter => "parameter",
            VariableKind.ForVariable => "`for` variable",
            VariableKind.PatternBinding => "pattern binding",
            _ => "lambda parameter",
        },
        _ => "declaration",
    };

    /// <summary>Reserved/standard/shadowing checks for a declaration inside a function or block.</summary>
    private void CheckLocalDeclaration(string name, Span nameSpan)
    {
        if (CheckNameIsDeclarable(name, nameSpan) && FindSymbol(name) is { } existing)
        {
            ReportShadowing(name, existing.NameSpan, nameSpan, DescribeSymbolKind(existing));
        }
    }

    private VariableSymbol? DeclareVariable(
        string name,
        Span nameSpan,
        VariableKind kind,
        bool isMutable,
        ScriptType type,
        LetStatement? declaration,
        bool checkConflicts = true)
    {
        if (name == "_")
        {
            // SCRIPTS-TYPE-0070: `_` alone is unreadable, so a bound type containing `result` is always a discard (§3.8).
            if (IsDiscardedFallibleBinding(name, type, isRead: false))
            {
                Report(ErrorCodes.Type0070, nameSpan, ("name", name));
            }

            return null;
        }

        if (checkConflicts)
        {
            CheckLocalDeclaration(name, nameSpan);
        }

        var symbol = new VariableSymbol(name, nameSpan, kind, isMutable, type) { Declaration = declaration };
        currentScope.Symbols[name] = symbol;
        currentScope.PendingDeclarations.Remove(name);
        declarationCount++;
        if (kind != VariableKind.Constant)
        {
            trackedBindings.Add(symbol);
        }

        return symbol;
    }

    /// <summary>Registers the lets (and nested functions) of a block as declared later, so that early uses report TYPE-0002.</summary>
    private void MarkPendingDeclarations(IReadOnlyList<Statement> statements, bool includeFunctions)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case LetStatement { Name: not "_" } letStatement:
                    currentScope.PendingDeclarations.TryAdd(letStatement.Name, letStatement.NameSpan);
                    break;
                case FunctionDeclaration function when includeFunctions:
                    currentScope.PendingDeclarations.TryAdd(function.Name, function.NameSpan);
                    break;
            }
        }
    }
}
