using Scripts.Core;

namespace Scripts.Checker;

internal enum VariableKind
{
    Constant,
    ScriptVariable,
    LocalVariable,
    Parameter,
    ForVariable,
    PatternBinding,
    LambdaParameter,
}

internal abstract class Symbol(string name, Span nameSpan)
{
    public string Name { get; } = name;

    public Span NameSpan { get; } = nameSpan;
}

internal sealed class VariableSymbol(string name, Span nameSpan, VariableKind kind, bool isMutable, ScriptType type) : Symbol(name, nameSpan)
{
    public VariableKind Kind { get; } = kind;

    public bool IsMutable { get; } = isMutable;

    public ScriptType Type { get; } = type;

    public bool IsRead { get; set; }

    /// <summary>Field names read through this binding (SCRIPTS-LINT-0011: a walk report that never reads `.skipped_paths`).</summary>
    public HashSet<string> ReadMembers { get; } = new(StringComparer.Ordinal);

    public LetStatement? Declaration { get; init; }

    public ConstantValue? Value { get; set; }
}

internal sealed class FunctionSymbol(FunctionDeclaration declaration, IReadOnlyList<ScriptType> parameterTypes, ScriptType returnType)
    : Symbol(declaration.Name, declaration.NameSpan)
{
    public FunctionDeclaration Declaration { get; } = declaration;

    public IReadOnlyList<ScriptType> ParameterTypes { get; } = parameterTypes;

    public ScriptType ReturnType { get; } = returnType;

    public FunctionType Type { get; } = new(parameterTypes, returnType);

    /// <summary>Set when a call resolves to this function (SCRIPTS-LINT-0007).</summary>
    public bool IsCalled { get; set; }

    /// <summary>
    /// True when the function is declared inside another function body: only a nested function can capture a
    /// `mutable` of its enclosing function, so only it can mutate captured state (SCRIPTS-TYPE-0066).
    /// </summary>
    public bool IsNested { get; set; }

    public string SignatureText
    {
        get
        {
            var parameters = string.Join(", ", Declaration.Parameters.Select((parameter, position) => $"{parameter.Name}: {ParameterTypes[position]}"));
            return $"{Name}({parameters}) -> {ReturnType}";
        }
    }
}

internal sealed class AliasSymbol(string name, Span nameSpan, LoadedFile module, ModuleInfo info) : Symbol(name, nameSpan)
{
    public LoadedFile Module { get; } = module;

    public ModuleInfo Info { get; } = info;
}

/// <summary>Lexical scope: declared symbols plus names whose declaration appears later in the same block.</summary>
internal sealed class Scope(Scope? parent)
{
    public Scope? Parent { get; } = parent;

    public Dictionary<string, Symbol> Symbols { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Span> PendingDeclarations { get; } = new(StringComparer.Ordinal);
}

/// <summary>Public face of a checked module as seen by its importers.</summary>
internal sealed class ModuleInfo
{
    public Dictionary<string, FunctionSymbol> Functions { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, VariableSymbol> Constants { get; } = new(StringComparer.Ordinal);

    public IEnumerable<string> PublicMemberNames =>
        Functions.Values.Where(function => function.Declaration.Visibility == Visibility.Public).Select(function => function.Name)
            .Concat(Constants.Values.Where(constant => constant.Declaration?.Visibility == Visibility.Public).Select(constant => constant.Name));
}
