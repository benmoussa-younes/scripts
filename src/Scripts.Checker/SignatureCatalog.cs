using System.Collections.Concurrent;
using Scripts.Core;

namespace Scripts.Checker;

/// <summary>Stdlib signature with its type variables, parsed once from the catalog text.</summary>
internal sealed record ParsedSignature(ScriptType? Receiver, IReadOnlyList<ScriptType> Parameters, ScriptType Return)
{
    public FunctionType ToFunctionType() => new(Parameters, Return);
}

internal static class SignatureCatalog
{
    private static readonly ConcurrentDictionary<StdlibEntry, ParsedSignature> Cache = new(ReferenceEqualityComparer.Instance);

    public static ParsedSignature Get(StdlibEntry entry) => Cache.GetOrAdd(entry, Parse);

    /// <summary>Placeholder value for SCRIPTS-TYPE-0010 when the name is not a method of the type.</summary>
    public const string NoneMethodText = "";

    public static IReadOnlyList<string> MethodNames(string receiverKind) =>
        [.. StdlibCatalog.All
            .Where(entry => entry.Kind == StdlibKind.Method && StdlibCatalog.ReceiverKind(entry.ReceiverTypeText!) == receiverKind)
            .Select(entry => entry.Name)
            .Order(StringComparer.Ordinal)];

    public static IReadOnlyList<string> ModuleFunctionNames(string moduleName) =>
        [.. StdlibCatalog.All
            .Where(entry => entry.Kind == StdlibKind.ModuleFunction && entry.ModuleName == moduleName)
            .Select(entry => entry.Name)
            .Order(StringComparer.Ordinal)];

    public static IEnumerable<string> GlobalFunctionNames() =>
        StdlibCatalog.All.Where(entry => entry.Kind == StdlibKind.Function).Select(entry => entry.Name);

    private static ParsedSignature Parse(StdlibEntry entry) =>
        new(
            entry.ReceiverTypeText is null ? null : TypeText.Parse(entry.ReceiverTypeText),
            [.. entry.Parameters.Select(parameter => TypeText.Parse(parameter.TypeText))],
            TypeText.Parse(entry.ReturnTypeText));
}
