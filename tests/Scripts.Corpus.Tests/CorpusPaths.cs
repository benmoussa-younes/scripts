namespace Scripts.Corpus.Tests;

internal static class CorpusPaths
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string BinaryPath()
    {
        var configured = Environment.GetEnvironmentVariable("SCRIPTS_CORPUS_BINARY");
        var binary = string.IsNullOrEmpty(configured)
            ? Path.Combine(RepositoryRoot, "src", "Scripts.App", "bin", "Release", "net10.0", "linux-x64", "publish", "scripts")
            : configured;
        if (!File.Exists(binary))
        {
            throw new InvalidOperationException(
                $"scripts AOT binary not found: {binary} (publish it first: dotnet publish src/Scripts.App/Scripts.App.csproj -c Release -r linux-x64)");
        }

        return binary;
    }

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
