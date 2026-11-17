using Scripts.Core;
using Scripts.Runtime.Interpreter;
using Scripts.Syntax;

namespace Scripts.Runtime.Tests.InterpreterRuns;

/// <summary>Locates the repository (the directory holding Scripts.slnx above the test assembly) and runs script files from disk.</summary>
public static class InterpreterFiles
{
    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    public static string Root => RepositoryRoot.Value;

    public static string Combine(params string[] segments) => Path.Combine([Root, .. segments]);

    /// <summary>Loads the script from the file system and runs it on a large-stack thread, as the CLI does.</summary>
    public static ProgramRun RunFile(string scriptPath, params string[] scriptArguments) =>
        LargeStackThread.Run(() =>
        {
            var loaded = ProgramLoader.Load(scriptPath, new FileSystemSourceProvider());
            Assert.True(
                loaded.Error is null,
                loaded.Error is null ? string.Empty : $"{loaded.Error.Code} {loaded.Error.Message} at {loaded.Error.Start}");
            using var context = RuntimeContext.CreateForCapture(scriptArguments, out var captured, listenForSignals: false);
            var outcome = ProgramInterpreter.Run(loaded.Program!, context);
            context.Shutdown();
            return new ProgramRun(captured.ReadStandardOutput(), captured.ReadStandardError(), outcome);
        });

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Scripts.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Scripts.slnx not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>Runs a body with a fresh temporary working directory and restores the previous one (shared process state: use inside the system collection).</summary>
public sealed class TemporaryWorkingDirectory : IDisposable
{
    private readonly string previousDirectory = Directory.GetCurrentDirectory();

    public TemporaryWorkingDirectory()
    {
        Path = Directory.CreateTempSubdirectory("scripts-interpreter-test-").FullName;
        Directory.SetCurrentDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(previousDirectory);
        Directory.Delete(Path, recursive: true);
    }
}
