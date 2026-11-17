using Scripts.Core;

namespace Scripts.Syntax;

/// <summary>Lexical POSIX path helpers shared by the loader and the in-memory provider.</summary>
internal static class SourcePaths
{
    /// <summary>Directory part of a path: "" for a bare file name, "/" for a root-level file.</summary>
    public static string GetDirectory(string path)
    {
        var lastSeparator = path.LastIndexOf('/');
        return lastSeparator < 0 ? string.Empty : lastSeparator == 0 ? "/" : path.Substring(0, lastSeparator);
    }

    public static string Combine(string directory, string relativePath) =>
        directory.Length == 0 ? relativePath : directory.EndsWith('/') ? directory + relativePath : directory + "/" + relativePath;

    /// <summary>The import path with `.scs` appended when it does not already end with that extension (DESIGN §4.2).</summary>
    public static string AppendBcsExtension(string path) =>
        path.EndsWith(".scs", StringComparison.Ordinal) ? path : path + ".scs";

    private static bool IsIdentifierStart(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    /// <summary>Absolute path with `.`, `..` and repeated separators collapsed (no file system access).</summary>
    public static string NormalizeLexically(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return "/" + string.Join('/', segments);
    }
}
