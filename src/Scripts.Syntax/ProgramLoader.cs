using Scripts.Core;

namespace Scripts.Syntax;

public sealed record LoadResult(LoadedProgram? Program, Diagnostic? Error);

/// <summary>Loading phase of DESIGN §4.2: reads and parses the entry script and, depth first, every imported module.</summary>
public static class ProgramLoader
{
    /// <summary>Name of the entry file read from standard input: the `file` of its diagnostics (DESIGN §9).</summary>
    public const string StandardInputSourceName = "<stdin>";

    public static LoadResult Load(string entryPath, ISourceProvider provider, bool isModule = false) =>
        new ModuleLoader(provider).Load(entryPath, isModule);

    /// <summary>
    /// Loading phase of DESIGN §9 for `scripts run -` / `scripts check -`: the bytes of standard input are the entry file.
    /// The entry is named <see cref="StandardInputSourceName"/> and has no canonical path (the empty string); its
    /// imports resolve against the current working directory at load time.
    /// </summary>
    public static LoadResult LoadStandardInput(byte[] bytes, ISourceProvider provider, bool isModule = false) =>
        new ModuleLoader(provider).LoadEntryBytes(StandardInputSourceName, bytes, canonicalPath: string.Empty, isModule);
}

internal sealed class ModuleLoader
{
    private readonly ISourceProvider provider;
    private readonly Dictionary<string, LoadedFile> loadedModules = new(StringComparer.Ordinal);
    private readonly List<LoadedFile> orderedModules = [];
    private readonly List<ActiveFile> activeFiles = [];
    /// <summary>Name table shared by all files of the program: canonical identifiers end to end.</summary>
    private readonly NameTable names = new();

    public ModuleLoader(ISourceProvider provider)
    {
        this.provider = provider;
    }

    private sealed record ActiveFile(string CanonicalPath, string Path);

    public LoadResult Load(string entryPath, bool isModule = false)
    {
        if (!provider.TryReadBytes(entryPath, out var bytes, out var reason))
        {
            var unreadable = Diagnostics.Create(ErrorCodes.Cli0009, null, default, ("path", entryPath), ("reason", reason));
            return new LoadResult(null, unreadable);
        }

        var error = TryLoadFile(entryPath, bytes, isModule, provider.Canonicalize(entryPath), out var entry, isEntry: true);
        return error is null ? new LoadResult(new LoadedProgram(entry!, orderedModules), null) : new LoadResult(null, error);
    }

    /// <summary>Loads the entry file from bytes already read (standard input): its canonical path is given, not derived.</summary>
    public LoadResult LoadEntryBytes(string entryPath, byte[] bytes, string canonicalPath, bool isModule = false)
    {
        var error = TryLoadFile(entryPath, bytes, isModule, canonicalPath, out var entry, isEntry: true);
        return error is null ? new LoadResult(new LoadedProgram(entry!, orderedModules), null) : new LoadResult(null, error);
    }

    /// <summary>Order per: the import header is parsed, the modules are loaded, then the rest of the file is parsed.</summary>
    private Diagnostic? TryLoadFile(string path, byte[] bytes, bool isModule, string canonicalPath, out LoadedFile? file, bool isEntry = false)
    {
        file = null;
        var reading = SourceReading.Read(path, bytes);
        if (reading.Error is not null)
        {
            return reading.Error;
        }

        var source = reading.Source!;
        var parser = new SyntaxParser(source, isModule, names);
        if (!parser.TryParseImports(out var imports, out var error))
        {
            return error;
        }

        activeFiles.Add(new ActiveFile(canonicalPath, path));
        var importedModules = new List<(ImportDeclaration Import, LoadedFile Module)>();
        foreach (var import in imports)
        {
            var failure = TryLoadImport(source, path, import, out var module);
            if (failure is not null)
            {
                return failure;
            }

            importedModules.Add((import, module!));
        }

        activeFiles.RemoveAt(activeFiles.Count - 1);
        if (!parser.TryParseBody(imports, out var program, out error))
        {
            return error;
        }

        file = new LoadedFile(source, program!, isModule, canonicalPath);
        foreach (var (import, module) in importedModules)
        {
            file.Imports.TryAdd(import.Alias, module);
        }

        if (isModule)
        {
            loadedModules[canonicalPath] = file;
            if (!isEntry)
            {
                orderedModules.Add(file);
            }
        }

        return null;
    }

    private Diagnostic? TryLoadImport(SourceFile importer, string importerPath, ImportDeclaration import, out LoadedFile? module)
    {
        module = null;
        var relativePath = import.RelativePath;
        if (relativePath.StartsWith("std:", StringComparison.Ordinal))
        {
            // DESIGN §1.5: the standard modules are intrinsic namespaces and are never imported.
            return ImportError(ErrorCodes.Module0005, importer, import, ("name", relativePath));
        }

        var invalidReason = DescribeInvalidImportPath(relativePath);
        if (invalidReason is not null)
        {
            return ImportError(ErrorCodes.Module0007, importer, import, ("path", relativePath), ("reason", invalidReason));
        }

        // DESIGN §4.2: the `.scs` extension is optional; every import carries an explicit `as` alias (checked by the parser).
        var resolvedRelativePath = SourcePaths.AppendBcsExtension(relativePath);
        var importerDirectory = SourcePaths.GetDirectory(importerPath);
        var resolvedPath = SourcePaths.Combine(importerDirectory, resolvedRelativePath);
        var canonicalPath = provider.Canonicalize(resolvedPath);
        var cycleStart = activeFiles.FindIndex(active => active.CanonicalPath == canonicalPath);
        if (cycleStart >= 0)
        {
            var cycle = string.Join(" → ", activeFiles.Skip(cycleStart).Select(active => active.Path).Append(activeFiles[cycleStart].Path));
            return ImportError(ErrorCodes.Module0002, importer, import, ("cycle", cycle));
        }

        if (activeFiles.Count > Limits.MaximumImportDepth)
        {
            return ImportError(ErrorCodes.Module0003, importer, import, ("path", relativePath));
        }

        if (loadedModules.TryGetValue(canonicalPath, out var alreadyLoaded))
        {
            module = alreadyLoaded;
            return null;
        }

        if (!provider.TryReadBytes(resolvedPath, out var bytes, out var reason))
        {
            return ImportError(
                ErrorCodes.Module0001,
                importer,
                import,
                ("path", resolvedRelativePath),
                ("reason", reason),
                ("importer_directory", importerDirectory.Length == 0 ? "." : importerDirectory));
        }

        return TryLoadFile(resolvedPath, bytes, isModule: true, canonicalPath, out module);
    }

    private static string? DescribeInvalidImportPath(string relativePath)
    {
        if (relativePath.Length == 0)
        {
            return "empty path";
        }

        if (relativePath.Contains('\0', StringComparison.Ordinal))
        {
            return "the path contains a NUL character";
        }

        if (relativePath.StartsWith('/'))
        {
            return "absolute path";
        }

        return null;
    }

    private static Diagnostic ImportError(string code, SourceFile importer, ImportDeclaration import, params (string Name, string Value)[] arguments) =>
        Diagnostics.Create(code, importer, import.PathSpan, arguments);
}
