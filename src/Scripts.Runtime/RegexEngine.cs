using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Scripts.Runtime;

/// <summary>`string.matches` engine (DESIGN 7.6): .NET NonBacktracking over UTF-16 units, POSIX classes rejected before compilation.</summary>
public static class RegexEngine
{
    private const RegexOptions EngineOptions = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
    private const int MaximumCachedPatterns = 128;

    private static readonly Dictionary<string, Regex> CompiledPatterns = new(StringComparer.Ordinal);
    private static readonly Lock CacheLock = new();

    /// <summary>True when the pattern is valid; then `found` tells whether it matches anywhere in the text. Otherwise `failureMessage` is a reason for invalid_regex.</summary>
    public static bool TryFind(string text, string pattern, out bool found, [NotNullWhen(false)] out string? failureMessage)
    {
        found = false;
        if (!TryCompile(pattern, out var regex, out failureMessage))
        {
            return false;
        }

        found = regex!.IsMatch(text);
        failureMessage = null;
        return true;
    }

    /// <summary>
    /// The single compile path shared by <c>matches</c>, <c>capture_groups</c>, <c>replace_regex</c> and
    /// <c>split_regex</c>: POSIX classes are rejected before compilation, then the cached
    /// <see cref="RegexOptions.NonBacktracking"/> engine is used. On failure <paramref name="failureMessage"/>
    /// explains why the caller must return <c>invalid_regex</c>.
    /// </summary>
    internal static bool TryCompile(string pattern, [NotNullWhen(true)] out Regex? regex, [NotNullWhen(false)] out string? failureMessage)
    {
        regex = null;
        if (ContainsPosixClass(pattern))
        {
            failureMessage = "POSIX classes [:…:] are not supported";
            return false;
        }

        try
        {
            regex = Compile(pattern);
        }
        catch (NotSupportedException)
        {
            failureMessage = "unsupported construct (backreferences, lookaround, automaton too large…)";
            return false;
        }
        catch (ArgumentException)
        {
            failureMessage = "invalid syntax";
            return false;
        }

        failureMessage = null;
        return true;
    }

    private static Regex Compile(string pattern)
    {
        lock (CacheLock)
        {
            if (CompiledPatterns.TryGetValue(pattern, out var cached))
            {
                return cached;
            }
        }

        var regex = new Regex(pattern, EngineOptions);
        lock (CacheLock)
        {
            if (CompiledPatterns.Count >= MaximumCachedPatterns)
            {
                CompiledPatterns.Clear();
            }

            CompiledPatterns[pattern] = regex;
        }

        return regex;
    }

    /// <summary>True when an unescaped character class contains `[:` followed, in the same class, by `:]`.</summary>
    private static bool ContainsPosixClass(string pattern)
    {
        var classDepth = 0;
        for (var position = 0; position < pattern.Length; position++)
        {
            var character = pattern[position];
            if (character == '\\')
            {
                position++;
            }
            else if (classDepth == 0)
            {
                if (character == '[')
                {
                    classDepth = 1;
                    position = SkipLeadingClassCharacters(pattern, position);
                }
            }
            else if (character == '[')
            {
                if (position + 1 < pattern.Length && pattern[position + 1] == ':' && ClosesWithColon(pattern, position))
                {
                    return true;
                }

                if (pattern[position - 1] == '-')
                {
                    classDepth++;
                }
            }
            else if (character == ']')
            {
                classDepth--;
            }
        }

        return false;
    }

    // `^` negation and a `]` right after the opening bracket are part of the class content.
    private static int SkipLeadingClassCharacters(string pattern, int openingPosition)
    {
        var position = openingPosition;
        if (position + 1 < pattern.Length && pattern[position + 1] == '^')
        {
            position++;
        }

        if (position + 1 < pattern.Length && pattern[position + 1] == ']')
        {
            position++;
        }

        return position;
    }

    private static bool ClosesWithColon(string pattern, int openingPosition)
    {
        for (var position = openingPosition + 2; position < pattern.Length; position++)
        {
            if (pattern[position] == '\\')
            {
                position++;
            }
            else if (pattern[position] == ']')
            {
                return position - 1 >= openingPosition + 2 && pattern[position - 1] == ':';
            }
        }

        return false;
    }
}
