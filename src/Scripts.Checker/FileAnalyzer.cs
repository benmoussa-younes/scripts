using System.Globalization;
using Scripts.Core;

namespace Scripts.Checker;

/// <summary>Semantic analysis of one file: hoisting, names, inference, statements, patterns (split over the partial files).</summary>
internal sealed partial class FileAnalyzer
{
    private readonly LoadedFile file;
    private readonly SourceFile source;
    private readonly DiagnosticSink sink;
    private readonly IReadOnlyDictionary<LoadedFile, ModuleInfo> moduleInfos;
    private readonly ModuleInfo exports = new();
    private readonly Scope hoistedScope = new(null);
    private readonly Scope constantsScope;
    private readonly Dictionary<FunctionDeclaration, FunctionSymbol> hoistedFunctions = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LetStatement, VariableSymbol?> hoistedLets = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, LetStatement> scriptVariableDeclarations = new(StringComparer.Ordinal);
    private readonly List<VariableSymbol> trackedBindings = [];
    private Scope currentScope;
    private FunctionContext? currentFunction;
    private string? enclosingTopLevelFunctionName;
    private List<LoopContext> loopStack = [];
    private int lambdaDepth;
    private int parallelLambdaDepth;

    // A4: the sorted list of visible names is needed for every unknown name (TYPE-0001). Recomputing it (with its
    // Order/Distinct) for each one is quadratic; it is cached and invalidated whenever a scope or a declaration changes.
    private IReadOnlyList<string>? visibleNamesCache;
    private Scope? visibleNamesCacheScope;
    private int visibleNamesCacheDeclarationCount;
    private int declarationCount;

    public FileAnalyzer(LoadedFile file, DiagnosticSink sink, IReadOnlyDictionary<LoadedFile, ModuleInfo> moduleInfos)
    {
        this.file = file;
        this.sink = sink;
        this.moduleInfos = moduleInfos;
        source = file.Source;
        constantsScope = new Scope(hoistedScope);
        currentScope = constantsScope;
    }

    public ModuleInfo Analyze()
    {
        HoistImportsAndDeclarations();
        DeclareConstants();
        CheckTopLevelStatements();
        ReportUnreadResultBindings();
        ReportUncalledMain();
        return exports;
    }

    /// <summary>
    /// SCRIPTS-LINT-0007 (DESIGN 8.5): a main script that declares `main` and never calls it
    /// prints nothing and exits 0 — the classic "why does my program do nothing?". The rule
    /// names the convention instead of silently calling `main` (a script is a sequence of
    /// statements, DESIGN 4.1).
    /// </summary>
    private void ReportUncalledMain()
    {
        if (file.IsModule)
        {
            return;
        }

        foreach (var (declaration, symbol) in hoistedFunctions)
        {
            if (symbol.Name == "main" && !symbol.IsCalled)
            {
                Report(ErrorCodes.Lint0007, declaration.NameSpan, ("name", "main"));
            }
        }
    }

    private static bool IsRunInheritBinding(VariableSymbol binding)
    {
        var initializer = binding.Declaration?.Initializer;
        while (initializer is PropagateExpression propagate)
        {
            initializer = propagate.Operand;
        }

        return initializer is CallExpression
        {
            Target.Kind: CallTargetKind.StdlibFunction,
            Target.Stdlib.QualifiedName: "process.run_inherit",
        };
    }

    /// <summary>
    /// SCRIPTS-TYPE-0070 (DESIGN 3.8): a `_`/`_name` binding whose bound type contains `result` must be read; `?`,
    /// `match`, `require_success` and `value_or` are the accepted ways to consume it. `_` and `_`-prefixed names
    /// share this predicate so the two declaration sites cannot drift.
    /// </summary>
    private static bool IsDiscardedFallibleBinding(string name, ScriptType type, bool isRead) =>
        name.StartsWith('_') && TypeRelations.ContainsResult(type) && !isRead;


    private void Report(string code, Span span, params (string Name, string Value)[] arguments) =>
        sink.Report(code, source, span, arguments);

    private void Report(string code, Span span, Fix? fix, params (string Name, string Value)[] arguments) =>
        sink.Report(code, source, span, fix, arguments);

    private int LineOf(Span span) => source.GetPosition(span.Start).Line;

    private string LineTextOf(Span span) => LineOf(span).ToString(CultureInfo.InvariantCulture);

    private string TextOf(Span span)
    {
        const int maximumLength = 60;
        var text = source.Text.Substring(span.Start, Math.Min(span.Length, source.Text.Length - span.Start)).Trim();
        var firstLineEnd = text.IndexOf('\n', StringComparison.Ordinal);
        if (firstLineEnd >= 0)
        {
            text = text[..firstLineEnd].TrimEnd() + " …";
        }

        return text.Length > maximumLength ? text[..maximumLength] + "…" : text;
    }

    private void ReportUnreadResultBindings()
    {
        foreach (var binding in trackedBindings)
        {
            if (!binding.IsRead && !binding.Name.StartsWith('_') && TypeRelations.ContainsResult(binding.Type))
            {
                Report(ErrorCodes.Type0049, binding.NameSpan, ("name", binding.Name));
            }

            // A bound `process_result` that is never read: the child may have failed and
            // nothing looks at it. Narrow on purpose (no data-flow analysis): a value that
            // is passed on, stored or returned is out of scope, as DESIGN 8.5 records.
            if (!binding.IsRead && !binding.Name.StartsWith('_') && binding.Type is NamedType { Name: "process_result" })
            {
                Report(ErrorCodes.Lint0006, binding.NameSpan, ("name", binding.Name));
            }

            // `run_inherit` returns a plain integer, so the type alone cannot identify it: look at the initializer.
            if (!binding.IsRead && !binding.Name.StartsWith('_') && IsRunInheritBinding(binding))
            {
                Report(ErrorCodes.Lint0006, binding.NameSpan, ("name", binding.Name));
            }

            // SCRIPTS-TYPE-0070: a `_`/`_name` binding whose bound type contains `result` must be read (DESIGN 3.8).
            if (IsDiscardedFallibleBinding(binding.Name, binding.Type, binding.IsRead))
            {
                Report(ErrorCodes.Type0070, binding.NameSpan, ("name", binding.Name));
            }

            // SCRIPTS-LINT-0010: a spawned child that is never read is never waited for or killed.
            if (!binding.IsRead && binding.Type is NamedType { Name: "process_handle" })
            {
                Report(ErrorCodes.Lint0010, binding.NameSpan, ("name", binding.Name));
            }

            // SCRIPTS-LINT-0011: a walk report whose reads never touch `.skipped_paths` (or that is never read) loses a subtree silently.
            if (binding.Type is NamedType { Name: "walk_report" }
                && !binding.ReadMembers.Contains("skipped_paths")
                && (!binding.IsRead || binding.ReadMembers.Count > 0))
            {
                Report(ErrorCodes.Lint0011, binding.NameSpan, ("name", binding.Name));
            }
        }
    }
}
