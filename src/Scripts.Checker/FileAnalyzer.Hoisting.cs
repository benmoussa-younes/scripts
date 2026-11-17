using Scripts.Core;

namespace Scripts.Checker;

internal sealed partial class FileAnalyzer
{
    /// <summary>Phase A: aliases, top-level functions and let names are declared before any body is checked (functions are hoisted).</summary>
    private void HoistImportsAndDeclarations()
    {
        var declaredNames = new Dictionary<string, (Span Span, string Kind)>(StringComparer.Ordinal);
        foreach (var import in file.Program.Imports)
        {
            HoistAlias(import, declaredNames);
        }

        foreach (var statement in file.Program.Statements)
        {
            switch (statement)
            {
                case FunctionDeclaration function:
                    HoistFunction(function, declaredNames);
                    break;
                case LetStatement { Name: not "_" } letStatement:
                    HoistLetName(letStatement, declaredNames);
                    break;
            }
        }
    }

    private void HoistAlias(ImportDeclaration import, Dictionary<string, (Span Span, string Kind)> declaredNames)
    {
        CheckNameIsDeclarable(import.Alias, import.AliasSpan);
        if (declaredNames.TryGetValue(import.Alias, out var first))
        {
            ReportShadowing(import.Alias, first.Span, import.AliasSpan, first.Kind);
            return;
        }

        declaredNames[import.Alias] = (import.AliasSpan, "import alias");
        if (file.Imports.TryGetValue(import.Alias, out var module))
        {
            var info = moduleInfos.TryGetValue(module, out var knownInfo) ? knownInfo : new ModuleInfo();
            hoistedScope.Symbols[import.Alias] = new AliasSymbol(import.Alias, import.AliasSpan, module, info);
        }
    }

    private void HoistFunction(FunctionDeclaration function, Dictionary<string, (Span Span, string Kind)> declaredNames)
    {
        CheckNameIsDeclarable(function.Name, function.NameSpan);
        var symbol = CreateFunctionSymbol(function);
        hoistedFunctions[function] = symbol;
        if (declaredNames.TryGetValue(function.Name, out var first))
        {
            ReportShadowing(function.Name, first.Span, function.NameSpan, first.Kind);
            return;
        }

        declaredNames[function.Name] = (function.NameSpan, "function");
        hoistedScope.Symbols[function.Name] = symbol;
        exports.Functions[function.Name] = symbol;
    }

    private void HoistLetName(LetStatement letStatement, Dictionary<string, (Span Span, string Kind)> declaredNames)
    {
        CheckNameIsDeclarable(letStatement.Name, letStatement.NameSpan);
        if (declaredNames.TryGetValue(letStatement.Name, out var first))
        {
            ReportShadowing(letStatement.Name, first.Span, letStatement.NameSpan, first.Kind);
            return;
        }

        declaredNames[letStatement.Name] = (letStatement.NameSpan, letStatement.IsConstant ? "constant" : "script variable");
        if (!file.IsModule && !letStatement.IsConstant)
        {
            scriptVariableDeclarations[letStatement.Name] = letStatement;
        }
    }

    /// <summary>Phase B: constants (every let of a module) are typed in order, before function bodies, so functions see them regardless of order.</summary>
    private void DeclareConstants()
    {
        currentScope = constantsScope;
        var constantLets = file.Program.Statements
            .OfType<LetStatement>()
            .Where(letStatement => file.IsModule || letStatement.IsConstant)
            .ToList();
        foreach (var letStatement in constantLets.Where(letStatement => letStatement.Name != "_"))
        {
            constantsScope.PendingDeclarations.TryAdd(letStatement.Name, letStatement.NameSpan);
        }

        foreach (var letStatement in constantLets)
        {
            if (file.IsModule)
            {
                ReportNonConstantModuleLet(letStatement);
            }

            var type = CheckLetInitializer(letStatement);
            VariableSymbol? symbol = null;
            if (letStatement.Name != "_")
            {
                symbol = new VariableSymbol(letStatement.Name, letStatement.NameSpan, VariableKind.Constant, letStatement.IsMutable, type)
                {
                    Declaration = letStatement,
                };
                if (letStatement.IsConstant)
                {
                    symbol.Value = ConstantFolding.TryFold(letStatement.Initializer, LookupConstantValue);
                }

                constantsScope.Symbols.TryAdd(letStatement.Name, symbol);
                constantsScope.PendingDeclarations.Remove(letStatement.Name);
                if (file.IsModule)
                {
                    exports.Constants.TryAdd(letStatement.Name, symbol);
                }
            }

            hoistedLets[letStatement] = symbol;
        }
    }

    private void ReportNonConstantModuleLet(LetStatement letStatement)
    {
        if (!letStatement.IsMutable && letStatement.IsConstant)
        {
            return;
        }

        var span = letStatement.IsMutable ? letStatement.MutableSpan ?? letStatement.NameSpan : letStatement.Initializer.Span;
        Report(
            ErrorCodes.Module0009,
            span,
            ("name", letStatement.Name),
            ("reason", ConstantReasons.Describe(letStatement, file, EarlierConstantNames(letStatement))));
    }

    private ConstantValue? LookupConstantValue(string name) =>
        FindSymbol(name) is VariableSymbol { Kind: VariableKind.Constant } constant ? constant.Value : null;

    /// <summary>Phase C: statements in order; a module only allows declarations (MODULE-0008).</summary>
    private void CheckTopLevelStatements()
    {
        currentScope = new Scope(hoistedScope);
        MarkPendingDeclarations(file.Program.Statements, includeFunctions: false);
        if (!file.IsModule)
        {
            CheckStatementList(file.Program.Statements, isFileLevel: true);
            return;
        }

        foreach (var statement in file.Program.Statements)
        {
            switch (statement)
            {
                case FunctionDeclaration function:
                    CheckHoistedFunction(function);
                    break;
                case LetStatement:
                    break;
                default:
                    Report(ErrorCodes.Module0008, statement.Span);
                    break;
            }
        }
    }

    private void CheckHoistedFunction(FunctionDeclaration function) =>
        CheckFunctionBody(hoistedFunctions[function], constantsScope, isTopLevel: true);
}
