using Scripts.Core;

namespace Scripts.Syntax;

/// <summary>Reads real files; <see cref="Canonicalize"/> resolves symbolic links so a module has one identity.</summary>
public sealed class FileSystemSourceProvider : ISourceProvider
{
    private const int MaximumSymbolicLinkExpansions = 40;

    public string Canonicalize(string path)
    {
        var absolute = path.StartsWith('/') ? path : SourcePaths.Combine(Directory.GetCurrentDirectory(), path);
        var pending = new Stack<string>();
        PushComponents(pending, absolute);
        var resolved = new List<string>();
        var expansions = 0;
        while (pending.Count > 0)
        {
            var component = pending.Pop();
            if (component.Length == 0 || component == ".")
            {
                continue;
            }

            if (component == "..")
            {
                if (resolved.Count > 0)
                {
                    resolved.RemoveAt(resolved.Count - 1);
                }

                continue;
            }

            var linkTarget = TryReadLinkTarget("/" + string.Join('/', resolved.Append(component)));
            if (linkTarget is null)
            {
                resolved.Add(component);
                continue;
            }

            expansions++;
            if (expansions > MaximumSymbolicLinkExpansions)
            {
                return SourcePaths.NormalizeLexically(absolute);
            }

            if (linkTarget.StartsWith('/'))
            {
                resolved.Clear();
            }

            PushComponents(pending, linkTarget);
        }

        return "/" + string.Join('/', resolved);
    }

    public bool TryReadBytes(string path, out byte[] bytes, out string error)
    {
        bytes = [];
        try
        {
            if (Directory.Exists(path))
            {
                error = "this is a directory, not a file";
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (!stream.CanSeek)
            {
                error = "this is not a regular file";
                return false;
            }

            if (stream.Length > Array.MaxLength)
            {
                error = "file too large";
                return false;
            }

            bytes = stream.Length > 0 ? new byte[stream.Length] : ReadAtMostOneByteBeyondTheSourceLimit(stream);
            if (stream.Length > 0)
            {
                stream.ReadExactly(bytes);
            }

            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            error = "file not found";
        }
        catch (UnauthorizedAccessException)
        {
            error = "permission denied";
        }
        catch (ArgumentException)
        {
            error = "invalid path";
        }
        catch (IOException exception)
        {
            error = "input/output error: " + exception.Message;
        }

        bytes = [];
        return false;
    }

    /// <summary>Devices such as /dev/zero report a zero length but never end; reading stops once the source limit is exceeded.</summary>
    private static byte[] ReadAtMostOneByteBeyondTheSourceLimit(FileStream stream)
    {
        var buffer = new byte[Limits.MaximumSourceBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return buffer.AsSpan(0, total).ToArray();
    }

    private static void PushComponents(Stack<string> pending, string path)
    {
        foreach (var component in path.Split('/').Reverse())
        {
            pending.Push(component);
        }
    }

    private static string? TryReadLinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
