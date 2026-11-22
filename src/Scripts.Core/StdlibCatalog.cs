namespace Scripts.Core;

public enum StdlibKind
{
    Function,
    Method,
    ModuleFunction,
}

public sealed record StdlibParameter(string Name, string TypeText);

/// <summary>One standard-library entry: the signature text is canonical, and every layer reads this record.</summary>
public sealed record StdlibEntry(
    string QualifiedName,
    StdlibKind Kind,
    string? ReceiverTypeText,
    string? ModuleName,
    string Name,
    IReadOnlyList<StdlibParameter> Parameters,
    string ReturnTypeText,
    string Signature,
    string ExampleCode,
    string ExampleOutput,
    string Summary,
    IReadOnlyList<string> ErrorKinds,
    IReadOnlyList<string> Effects,
    bool Destructive,
    bool MutatesReceiver,
    bool Dangerous,
    bool Deterministic,
    string ExplainTemplate,
    string Semantics);

/// <summary>Lookup over the standard-library catalogue (Catalogue/StdlibCatalogData.cs). Single source of truth for checker, runtime, describe.</summary>
public static class StdlibCatalog
{
    private static readonly Lazy<Index> Data = new(() => new Index(StdlibCatalogData.All));

    public static IReadOnlyList<StdlibEntry> All => StdlibCatalogData.All;

    /// <summary>Global function such as filesystem.read_file.</summary>
    public static StdlibEntry? FindGlobalFunction(string name) =>
        Data.Value.Globals.TryGetValue(name, out var entry) ? entry : null;

    /// <summary>Module function such as environment.get_variable (moduleName = "environment", "process", …).</summary>
    public static StdlibEntry? FindModuleFunction(string moduleName, string name) =>
        Data.Value.ModuleFunctions.TryGetValue(moduleName + "." + name, out var entry) ? entry : null;

    /// <summary>Methods with this name, any receiver (receiverKind is one of string, list, map, result).</summary>
    public static StdlibEntry? FindMethod(string receiverKind, string name) =>
        Data.Value.Methods.TryGetValue(receiverKind + "." + name, out var entry) ? entry : null;

    /// <summary>True for an intrinsic namespace, always available and reserved (DESIGN §1.5).</summary>
    public static bool IsModuleName(string name) =>
        IntrinsicModuleNames.Contains(name, StringComparer.Ordinal);

    /// <summary>Intrinsic namespaces, in a stable order (used by error messages and describe).</summary>
    public static IReadOnlyList<string> IntrinsicModuleNames =>
        ["environment", "io", "process", "json", "filesystem", "parallel", "time", "system", "encoding", "random", "csv"];

    public static bool IsGlobalFunctionName(string name) => Data.Value.Globals.ContainsKey(name);

    /// <summary>True when the name is a method of some receiver kind (SCRIPTS-TYPE-0001 suggestion).</summary>
    public static bool IsMethodName(string name) =>
        Data.Value.Methods.Keys.Any(key => key.EndsWith("." + name, StringComparison.Ordinal));

    /// <summary>"list&lt;string&gt;" and "list&lt;T&gt;" both map to "list".</summary>
    public static string ReceiverKind(string receiverTypeText)
    {
        var angle = receiverTypeText.IndexOf('<', StringComparison.Ordinal);
        return angle < 0 ? receiverTypeText : receiverTypeText[..angle];
    }

    private sealed class Index
    {
        public Index(IReadOnlyList<StdlibEntry> entries)
        {
            foreach (var entry in entries)
            {
                switch (entry.Kind)
                {
                    case StdlibKind.Function:
                        Globals[entry.Name] = entry;
                        break;
                    case StdlibKind.ModuleFunction:
                        ModuleFunctions[entry.ModuleName + "." + entry.Name] = entry;
                        break;
                    default:
                        Methods[ReceiverKind(entry.ReceiverTypeText!) + "." + entry.Name] = entry;
                        break;
                }
            }
        }

        public Dictionary<string, StdlibEntry> Globals { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, StdlibEntry> ModuleFunctions { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, StdlibEntry> Methods { get; } = new(StringComparer.Ordinal);
    }
}
