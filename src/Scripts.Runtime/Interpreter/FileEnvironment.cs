using Scripts.Core;

namespace Scripts.Runtime.Interpreter;

/// <summary>Per-file state: constants and hoisted functions are visible to every function of the file, imports only to this file.</summary>
internal sealed class FileEnvironment
{
    public FileEnvironment(LoadedFile loadedFile)
    {
        LoadedFile = loadedFile;
        ConstantScope = new Scope(null, this);
    }

    /// <summary>The file of this environment: its source positions feed every runtime diagnostic raised here.</summary>
    public LoadedFile LoadedFile { get; private set; }

    public SourceFile Source => LoadedFile.Source;

    /// <summary>Root of every function scope of the file; holds the constants.</summary>
    public Scope ConstantScope { get; }

    public Dictionary<string, UserFunctionValue> Functions { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, FileEnvironment> Imports { get; } = new(StringComparer.Ordinal);
}
