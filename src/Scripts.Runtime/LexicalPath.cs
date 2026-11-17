namespace Scripts.Runtime;

/// <summary>Lexical path operations on UTF-16 text (DESIGN 3.2.1 and 7.5): no normalization, no disk access.</summary>
public static class LexicalPath
{
    /// <summary>Raw lexical join for internal callers: no segment validation (empty child → base, rooted child → child). The `/` operator's fail-closed guard is in ArithmeticOperations.JoinPath.</summary>
    public static string Join(string basePath, string childSegment)
    {
        if (childSegment.Length == 0)
        {
            return basePath;
        }

        if (childSegment[0] == '/' || basePath.Length == 0)
        {
            return childSegment;
        }

        if (basePath[^1] == '/')
        {
            SizeLimits.EnsureStringLength((long)basePath.Length + childSegment.Length);
            return basePath + childSegment;
        }

        SizeLimits.EnsureStringLength((long)basePath.Length + childSegment.Length + 1);
        return string.Concat(basePath, "/", childSegment);
    }

    public static string BaseName(string path)
    {
        var withoutTrailingSeparators = path.TrimEnd('/');
        if (withoutTrailingSeparators.Length == 0)
        {
            return path.Length == 0 ? string.Empty : "/";
        }

        return withoutTrailingSeparators[(withoutTrailingSeparators.LastIndexOf('/') + 1)..];
    }

    public static string Extension(string path)
    {
        var name = BaseName(path);
        if (name is "/" or "." or "..")
        {
            return string.Empty;
        }

        var lastDot = name.LastIndexOf('.');
        return lastDot <= 0 || lastDot == name.Length - 1 ? string.Empty : name[(lastDot + 1)..];
    }

    /// <summary>Base name without its final extension; a leading dot alone is not an extension.</summary>
    public static string FileStem(string path)
    {
        var name = BaseName(path);
        if (name is "/" or "." or ".." or "")
        {
            return name;
        }

        var lastDot = name.LastIndexOf('.');
        return lastDot <= 0 ? name : name[..lastDot];
    }

    /// <summary>Replaces the final extension of the last component; an empty extension removes it.</summary>
    public static string WithExtension(string path, string extension)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return path;
        }

        var name = BaseName(trimmed);
        if (name is "/" or "." or "..")
        {
            return path;
        }

        var prefix = trimmed[..^name.Length];
        var replaced = prefix + FileStem(name) + extension;
        SizeLimits.EnsureStringLength(replaced.Length);
        return replaced;
    }

    /// <summary>Lexical normal form: removes `.` and duplicate separators, resolves `..` lexically, keeps a leading `..`.</summary>
    public static string Normalize(string path)
    {
        if (path.Length == 0)
        {
            return string.Empty;
        }

        var absolute = path[0] == '/';
        var components = new List<string>();
        foreach (var component in path.Split('/'))
        {
            if (component.Length == 0 || component == ".")
            {
                continue;
            }

            if (component == "..")
            {
                if (components.Count > 0 && components[^1] != "..")
                {
                    components.RemoveAt(components.Count - 1);
                }
                else if (!absolute)
                {
                    components.Add(component);
                }

                continue;
            }

            components.Add(component);
        }

        var normalized = (absolute ? "/" : string.Empty) + string.Join('/', components);
        if (normalized.Length == 0)
        {
            return absolute ? "/" : ".";
        }

        return normalized;
    }

    public static string Parent(string path)
    {
        if (path.Length == 0)
        {
            return ".";
        }

        var withoutTrailingSeparators = path.TrimEnd('/');
        if (withoutTrailingSeparators.Length == 0)
        {
            return "/";
        }

        var lastSeparator = withoutTrailingSeparators.LastIndexOf('/');
        if (lastSeparator < 0)
        {
            return ".";
        }

        var directory = withoutTrailingSeparators[..lastSeparator].TrimEnd('/');
        return directory.Length == 0 ? "/" : directory;
    }
}
