using System.Text;

namespace Scripts.Runtime.Platform;

/// <summary>
/// Explicit glob grammar (SPEC-STDLIB §7.2/§7.5.1): `*`, `?`, `[...]` inside one component and a
/// `**` component that crosses `/`. No implicit expansion, no brace expansion, no escape.
/// </summary>
internal sealed class GlobPattern
{
    private GlobPattern(string[] components) => Components = components;

    public IReadOnlyList<string> Components { get; }

    public static bool TryCompile(string pattern, out GlobPattern? compiled, out string reason)
    {
        compiled = null;
        reason = string.Empty;
        if (pattern.Length == 0)
        {
            reason = "the pattern is empty";
            return false;
        }

        if (pattern[0] == '/')
        {
            reason = "the pattern must be relative";
            return false;
        }

        var components = pattern.Split('/');
        foreach (var component in components)
        {
            if (component.Length == 0)
            {
                reason = "the pattern contains an empty component";
                return false;
            }

            if (component is "." or "..")
            {
                reason = "the pattern must not contain `.` or `..` components";
                return false;
            }

            if (component.Contains("**", StringComparison.Ordinal) && component != "**")
            {
                reason = "`**` must be a whole component";
                return false;
            }

            if (!ClassBracketsAreBalanced(component))
            {
                reason = "the pattern contains an unterminated or empty `[...]` class";
                return false;
            }
        }

        compiled = new GlobPattern(components);
        return true;
    }

    private static bool ClassBracketsAreBalanced(string component)
    {
        for (var index = 0; index < component.Length; index++)
        {
            if (component[index] != '[')
            {
                continue;
            }

            var closing = component.IndexOf(']', index + 1);
            if (closing < 0)
            {
                return false;
            }

            var start = index + 1;
            if (start < closing && component[start] is '!' or '^')
            {
                start++;
            }

            if (start >= closing)
            {
                return false;
            }

            index = closing;
        }

        return true;
    }

    public bool MatchesPath(string path)
    {
        // An absolute receiver is matched on its text below the root; the pattern itself stays relative,
        // so a path returned by filesystem.glob under an absolute root can still be tested.
        var relative = path.StartsWith('/') ? path[1..] : path;
        relative = relative.TrimEnd('/');
        var pathComponents = relative.Length == 0 ? [] : relative.Split('/');
        return MatchesPath(pathComponents, 0, 0);
    }

    private bool MatchesPath(string[] pathComponents, int pathIndex, int patternIndex)
    {
        if (patternIndex == Components.Count)
        {
            return pathIndex == pathComponents.Length;
        }

        var component = Components[patternIndex];
        if (component == "**")
        {
            if (MatchesPath(pathComponents, pathIndex, patternIndex + 1))
            {
                return true;
            }

            for (var next = pathIndex; next < pathComponents.Length; next++)
            {
                if (MatchesPath(pathComponents, next + 1, patternIndex + 1))
                {
                    return true;
                }
            }

            return false;
        }

        return pathIndex < pathComponents.Length
            && MatchesName(pathComponents[pathIndex], component)
            && MatchesPath(pathComponents, pathIndex + 1, patternIndex + 1);
    }

    /// <summary>Matches one path component; `?` and `[...]` consume one Unicode scalar, not one UTF-16 unit.</summary>
    public static bool MatchesName(string name, string pattern)
    {
        var nameRunes = name.EnumerateRunes().ToArray();
        var patternRunes = pattern.EnumerateRunes().ToArray();
        var nameIndex = 0;
        var patternIndex = 0;
        var starIndex = -1;
        var starNameIndex = 0;
        while (nameIndex < nameRunes.Length)
        {
            if (patternIndex < patternRunes.Length && patternRunes[patternIndex].Value == '*')
            {
                starIndex = patternIndex++;
                starNameIndex = nameIndex;
                continue;
            }

            if (patternIndex < patternRunes.Length && patternRunes[patternIndex].Value == '?')
            {
                nameIndex++;
                patternIndex++;
                continue;
            }

            if (patternIndex < patternRunes.Length && patternRunes[patternIndex].Value == '[')
            {
                if (!MatchesClass(nameRunes[nameIndex], patternRunes, ref patternIndex))
                {
                    if (starIndex >= 0)
                    {
                        patternIndex = starIndex + 1;
                        nameIndex = ++starNameIndex;
                        continue;
                    }

                    return false;
                }

                nameIndex++;
                continue;
            }

            if (patternIndex < patternRunes.Length && patternRunes[patternIndex] == nameRunes[nameIndex])
            {
                nameIndex++;
                patternIndex++;
                continue;
            }

            if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                nameIndex = ++starNameIndex;
                continue;
            }

            return false;
        }

        while (patternIndex < patternRunes.Length && patternRunes[patternIndex].Value == '*')
        {
            patternIndex++;
        }

        return patternIndex == patternRunes.Length;
    }

    private static bool MatchesClass(Rune character, Rune[] pattern, ref int patternIndex)
    {
        var closing = -1;
        for (var index = patternIndex + 1; index < pattern.Length; index++)
        {
            if (pattern[index].Value == ']')
            {
                closing = index;
                break;
            }
        }

        var start = patternIndex + 1;
        var negated = false;
        if (start < closing && pattern[start].Value is '!' or '^')
        {
            negated = true;
            start++;
        }

        var matched = false;
        for (var index = start; index < closing; index++)
        {
            if (index + 2 < closing && pattern[index + 1].Value == '-')
            {
                if (pattern[index].Value <= character.Value && character.Value <= pattern[index + 2].Value)
                {
                    matched = true;
                }

                index += 2;
                continue;
            }

            if (pattern[index] == character)
            {
                matched = true;
            }
        }

        patternIndex = closing + 1;
        return negated ? !matched : matched;
    }
}
