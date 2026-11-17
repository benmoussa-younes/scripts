using System.Text;
using Scripts.Core;

namespace Scripts.Syntax;

/// <summary>Sources held in memory; paths are POSIX-like and compared after lexical normalization (relative paths are rooted at `/`).</summary>
public sealed class InMemorySourceProvider : ISourceProvider
{
    private static readonly UTF8Encoding Utf8WithoutByteOrderMark = new(false);

    private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);

    public InMemorySourceProvider AddText(string path, string text) => AddBytes(path, Utf8WithoutByteOrderMark.GetBytes(text));

    public InMemorySourceProvider AddBytes(string path, byte[] bytes)
    {
        files[Canonicalize(path)] = bytes;
        return this;
    }

    public string Canonicalize(string path) => SourcePaths.NormalizeLexically(path);

    public bool TryReadBytes(string path, out byte[] bytes, out string error)
    {
        if (files.TryGetValue(Canonicalize(path), out var found))
        {
            bytes = found;
            error = string.Empty;
            return true;
        }

        bytes = [];
        error = "file not found";
        return false;
    }
}
