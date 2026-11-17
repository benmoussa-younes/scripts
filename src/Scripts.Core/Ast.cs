namespace Scripts.Core;

public enum Visibility
{
    None,
    Public,
    Private,
}

public enum UnaryOperator
{
    Negate,
    Not,
}

public enum BinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    And,
    Or,
}

public enum AssignOperator
{
    Assign,
    AddAssign,
    SubtractAssign,
    MultiplyAssign,
    DivideAssign,
    ModuloAssign,
}

/// <summary>What a call or pipeline stage invokes (resolved by the checker, DESIGN 5.3, 5.4).</summary>
public enum CallTargetKind
{
    /// <summary>Function declared in the same file (top-level or nested); Declaration is set.</summary>
    UserFunction,

    /// <summary>Variable, parameter or call result of function type: evaluate the callee expression.</summary>
    FunctionValue,

    /// <summary>Global stdlib function or module stdlib function (environment.x, path.x); Stdlib is set.</summary>
    StdlibFunction,

    /// <summary>Stdlib method on a receiver; Stdlib is set.</summary>
    StdlibMethod,

    /// <summary>alias.function of an imported module; Declaration and ModuleAlias are set.</summary>
    ModuleFunction,
}

/// <summary>Reference type with identity equality: targets may point at functions that (recursively) contain the call itself.</summary>
public sealed class CallTarget(
    CallTargetKind kind,
    StdlibEntry? stdlib = null,
    FunctionDeclaration? declaration = null,
    string? moduleAlias = null)
{
    public CallTargetKind Kind { get; } = kind;

    public StdlibEntry? Stdlib { get; } = stdlib;

    public FunctionDeclaration? Declaration { get; } = declaration;

    public string? ModuleAlias { get; } = moduleAlias;

    public override string ToString() => $"{Kind}:{Stdlib?.QualifiedName ?? Declaration?.Name ?? string.Empty}";
}

public enum MemberAccessKind
{
    Unresolved,
    RecordField,
    ModuleConstant,
}

public abstract record Node(Span Span);

// ---------------------------------------------------------------- type syntax

public abstract record TypeSyntax(Span Span) : Node(Span);

/// <summary>Name with optional arguments: integer, list&lt;string&gt;, result&lt;unit, error&gt;.</summary>
public sealed record NamedTypeSyntax(Span Span, string Name, IReadOnlyList<TypeSyntax> TypeArguments) : TypeSyntax(Span);

public sealed record FunctionTypeSyntax(Span Span, IReadOnlyList<TypeSyntax> ParameterTypes, TypeSyntax ReturnType) : TypeSyntax(Span);

// ---------------------------------------------------------------- expressions

public abstract record Expression(Span Span) : Node(Span)
{
    /// <summary>Filled in by the type checker; null before checking. The runtime never reads it.</summary>
    public ScriptType? ResolvedType { get; set; }
}

public sealed record IntegerLiteral(Span Span, long Value) : Expression(Span);

public sealed record FloatLiteral(Span Span, double Value) : Expression(Span);

public sealed record BooleanLiteral(Span Span, bool Value) : Expression(Span);

/// <summary>Plain or raw string without interpolation (escapes already decoded).</summary>
public sealed record StringLiteral(Span Span, string Value) : Expression(Span);

public abstract record StringPart(Span Span) : Node(Span);

public sealed record TextPart(Span Span, string Text) : StringPart(Span);

public sealed record InterpolationPart(Span Span, Expression Expression) : StringPart(Span);

/// <summary>String with at least one interpolation.</summary>
public sealed record InterpolatedString(Span Span, IReadOnlyList<StringPart> Parts) : Expression(Span);

/// <summary>p"..." literal; Parts has one TextPart when there is no interpolation.</summary>
public sealed record PathLiteral(Span Span, IReadOnlyList<StringPart> Parts) : Expression(Span);

public sealed record IdentifierExpression(Span Span, string Name) : Expression(Span);

public sealed record ListLiteral(Span Span, IReadOnlyList<Expression> Elements) : Expression(Span);

public sealed record MapEntry(Span Span, Expression Key, Expression Value) : Node(Span);

public sealed record MapLiteral(Span Span, IReadOnlyList<MapEntry> Entries) : Expression(Span);

public sealed record UnaryExpression(Span Span, UnaryOperator Operator, Expression Operand) : Expression(Span);

public sealed record BinaryExpression(Span Span, BinaryOperator Operator, Expression Left, Expression Right, Span OperatorSpan) : Expression(Span);

/// <summary>
/// One argument of a call. Name and NameSpan are set only by the fully named form (`f(name = value)`, DESIGN 5.3);
/// a positional argument has neither. It is a wrapper, not a level of the AST: the tree depth is the depth of
/// <see cref="Expression"/>. Arguments stay in source order: the checker maps them to parameter positions, and the
/// interpreter evaluates them in source order.
/// </summary>
public sealed record CallArgument(Span Span, Expression Expression, string? Name, Span? NameSpan);

/// <summary>f(a), alias.f(a), environment.get_variable(a), value.method(a): Callee is an IdentifierExpression or MemberAccessExpression.</summary>
public sealed record CallExpression(Span Span, Expression Callee, IReadOnlyList<CallArgument> Arguments) : Expression(Span)
{
    /// <summary>Filled in by the checker; the interpreter relies on it and never re-resolves callees.</summary>
    public CallTarget? Target { get; set; }
}

public sealed record MemberAccessExpression(Span Span, Expression Target, string MemberName, Span MemberSpan) : Expression(Span)
{
    /// <summary>Filled in by the checker: record field read or module constant read (alias.CONSTANT).</summary>
    public MemberAccessKind AccessKind { get; set; }

    /// <summary>The imported module's constant declaration when AccessKind is ModuleConstant.</summary>
    public LetStatement? ConstantDeclaration { get; set; }
}

public sealed record IndexExpression(Span Span, Expression Target, Expression Index) : Expression(Span);

/// <summary>Postfix ? operator.</summary>
public sealed record PropagateExpression(Span Span, Expression Operand) : Expression(Span);

public sealed record LambdaParameter(Span Span, string Name, TypeSyntax? Annotation) : Node(Span);

public sealed record LambdaExpression(Span Span, IReadOnlyList<LambdaParameter> Parameters, Expression Body) : Expression(Span);

/// <summary>ok(value) or ok() (Value null, only valid for result&lt;unit, E&gt;).</summary>
public sealed record OkExpression(Span Span, Expression? Value) : Expression(Span);

public sealed record ErrorExpression(Span Span, Expression Value) : Expression(Span);

/// <summary>error(kind, message, subject): builds an `error` value directly (DESIGN 3.5.1).</summary>
public sealed record ErrorConstructorExpression(Span Span, Expression Kind, Expression Subject, Expression Message) : Expression(Span);

public sealed record MatchExpression(Span Span, Expression Scrutinee, IReadOnlyList<MatchArm> Arms, bool IsStatement) : Expression(Span);

/// <summary>Exactly one of BodyExpression / BodyBlock is non-null.</summary>
public sealed record MatchArm(Span Span, Pattern Pattern, Expression? BodyExpression, BlockStatement? BodyBlock) : Node(Span);

// ---------------------------------------------------------------- patterns

public abstract record Pattern(Span Span) : Node(Span);

public sealed record WildcardPattern(Span Span) : Pattern(Span);

public sealed record BindingPattern(Span Span, string Name) : Pattern(Span);

/// <summary>Literal is an IntegerLiteral (possibly negated: Value already negative), StringLiteral or BooleanLiteral.</summary>
public sealed record LiteralPattern(Span Span, Expression Literal) : Pattern(Span);

/// <summary>1 | 2 | 3 (literals and _ only).</summary>
public sealed record AlternativePattern(Span Span, IReadOnlyList<Pattern> Alternatives) : Pattern(Span);

/// <summary>ok(x) or error(x); Inner is WildcardPattern or BindingPattern.</summary>
public sealed record ResultPattern(Span Span, bool IsOk, Pattern Inner) : Pattern(Span);

// ---------------------------------------------------------------- statements

public abstract record Statement(Span Span) : Node(Span);

public sealed record BlockStatement(Span Span, IReadOnlyList<Statement> Statements) : Statement(Span);

/// <summary>Name is "_" for a discard binding; MutableSpan is the `mutable` keyword (null when IsMutable is false).</summary>
public sealed record LetStatement(
    Span Span,
    Visibility Visibility,
    bool IsMutable,
    string Name,
    Span NameSpan,
    TypeSyntax? Annotation,
    Expression Initializer,
    Span? MutableSpan) : Statement(Span)
{
    /// <summary>Filled in by the checker: immutable top-level let with a constant initializer (DESIGN 4.4).</summary>
    public bool IsConstant { get; set; }
}

/// <summary>target[index]...[index] op= value; Indexes is empty for a plain variable target.</summary>
public sealed record AssignmentStatement(
    Span Span,
    string TargetName,
    Span TargetSpan,
    IReadOnlyList<Expression> Indexes,
    AssignOperator Operator,
    Expression Value,
    Span OperatorSpan) : Statement(Span);

public sealed record IfBranch(Span Span, Expression Condition, BlockStatement Body) : Node(Span);

public sealed record IfStatement(Span Span, IReadOnlyList<IfBranch> Branches, BlockStatement? ElseBlock) : Statement(Span);

public sealed record WhileStatement(Span Span, Expression Condition, BlockStatement Body) : Statement(Span);

public sealed record ForStatement(Span Span, string VariableName, Span VariableSpan, Expression Iterable, BlockStatement Body) : Statement(Span);

public sealed record ReturnStatement(Span Span, Expression? Value) : Statement(Span);

public sealed record BreakStatement(Span Span) : Statement(Span);

public sealed record ContinueStatement(Span Span) : Statement(Span);

public sealed record Parameter(Span Span, string Name, TypeSyntax Type) : Node(Span);

public sealed record FunctionDeclaration(
    Span Span,
    Visibility Visibility,
    string Name,
    Span NameSpan,
    IReadOnlyList<Parameter> Parameters,
    TypeSyntax? ReturnType,
    BlockStatement Body) : Statement(Span);

public sealed record ExpressionStatement(Span Span, Expression Expression) : Statement(Span);

// ---------------------------------------------------------------- program

public sealed record ImportDeclaration(Span Span, string RelativePath, Span PathSpan, string Alias, Span AliasSpan) : Node(Span);

public sealed record ProgramNode(Span Span, IReadOnlyList<ImportDeclaration> Imports, IReadOnlyList<Statement> Statements);

/// <summary>One parsed source file with its resolved imports (alias to file).</summary>
public sealed class LoadedFile
{
    public LoadedFile(SourceFile source, ProgramNode program, bool isModule, string canonicalPath)
    {
        Source = source;
        Program = program;
        IsModule = isModule;
        CanonicalPath = canonicalPath;
    }

    public SourceFile Source { get; }

    public ProgramNode Program { get; }

    public bool IsModule { get; }

    public string CanonicalPath { get; }

    /// <summary>Alias declared in this file's imports to the module it designates.</summary>
    public Dictionary<string, LoadedFile> Imports { get; } = new(StringComparer.Ordinal);
}

/// <summary>Entry script plus every module it (transitively) imports, dependencies first (initialization order).</summary>
public sealed record LoadedProgram(LoadedFile Entry, IReadOnlyList<LoadedFile> Modules)
{
    public IEnumerable<LoadedFile> AllFilesInLoadOrder()
    {
        foreach (var module in Modules)
        {
            yield return module;
        }

        yield return Entry;
    }
}

/// <summary>File access abstraction so loaders and tests can run on in-memory sources.</summary>
public interface ISourceProvider
{
    /// <summary>Canonical absolute path (symlinks resolved when the file exists), used as module identity.</summary>
    string Canonicalize(string path);

    /// <summary>Raw bytes of the file or false with a reason (not found, unreadable...).</summary>
    bool TryReadBytes(string path, out byte[] bytes, out string error);
}
