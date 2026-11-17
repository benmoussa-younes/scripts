namespace Scripts.Checker;

/// <summary>Deterministic suggestions: smallest edit distance first, then ordinal order.</summary>
internal static class SimilarNames
{
    public const string NoneText = "none";

    /// <summary>Rendered when no candidate is close enough; SCRIPTS-TYPE-0001 substitutes an actionable fallback.</summary>
    public const string NoSimilarText = "no suggestion available.";

    /// <summary>Names compared for suggestions are bounded; a 32000-character identifier cannot have a "close" name (A4).</summary>
    private const int MaximumSuggestedNameLength = 64;

    /// <summary>Closest candidate within a distance proportional to the name length, as a ready-to-use sentence fragment.</summary>
    public static string FindSimilar(string name, IEnumerable<string> candidates, bool sorted = false)
    {
        if (name.Length > MaximumSuggestedNameLength)
        {
            return NoSimilarText;
        }

        var best = FindClosest(name, candidates, MaximumDistance(name), sorted);
        return best is not null ? $"close name: `{best}`." : NoSimilarText;
    }

    /// <summary>
    /// The closest candidate within the suggestion bound, or null; used for a machine-applicable fix
    /// (<c>--errors-json</c> <c>fix</c> and <c>scripts check --fix</c>). A far candidate is not a safe rename.
    /// </summary>
    public static string? FindFixCandidate(string name, IEnumerable<string> candidates, bool sorted = false) =>
        name.Length > MaximumSuggestedNameLength ? null : FindClosest(name, candidates, MaximumDistance(name), sorted);

    /// <summary>
    /// The suggestion of an unknown method (SCRIPTS-TYPE-0008): the closest name first when the typo is close enough,
    /// then the full sorted list so the contract of the type stays visible.
    /// </summary>
    public static string JoinAvailableWithClosest(IEnumerable<string> names, string requested)
    {
        var sorted = names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (sorted.Count == 0)
        {
            return NoneText;
        }

        var available = string.Join(", ", sorted);
        if (requested.Length > MaximumSuggestedNameLength)
        {
            return available;
        }

        var closest = FindClosest(requested, sorted, MaximumDistance(requested), sorted: true);
        return closest is null ? available : $"close name: `{closest}`. Full list: {available}";
    }

    /// <summary>A one-character typo must not suggest any two-character name (`x` → `io`): small names get one edit.</summary>
    private static int MaximumDistance(string name) => name.Length < 4 ? 1 : Math.Max(2, name.Length / 3);

    /// <summary>
    /// Closest candidate whatever its distance; null when there is no candidate. When <paramref name="maximumDistance"/>
    /// is positive, candidates whose length differs by more than that bound are skipped: the edit distance cannot be
    /// smaller, so the result is unchanged while the O(L^2) comparisons are avoided (A4). When <paramref name="sorted"/>
    /// is true, <paramref name="candidates"/> is already deduplicated and ordinal-sorted, so it is used as is.
    /// </summary>
    public static string? FindClosest(string name, IEnumerable<string> candidates, int maximumDistance = int.MaxValue, bool sorted = false)
    {
        var pool = sorted ? candidates : candidates.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in pool)
        {
            var lengthGap = Math.Abs(candidate.Length - name.Length);
            if (lengthGap > maximumDistance || (bestDistance != int.MaxValue && lengthGap >= bestDistance))
            {
                continue;
            }

            var distance = Distance(name, candidate);
            if (distance > maximumDistance)
            {
                continue;
            }

            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    public static string JoinAvailable(IEnumerable<string> names)
    {
        var sorted = names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return sorted.Count == 0 ? NoneText : string.Join(", ", sorted);
    }

    public static int Distance(string left, string right)
    {
        var previousRow = new int[right.Length + 1];
        var currentRow = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
        {
            previousRow[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            currentRow[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitutionCost = left[row - 1] == right[column - 1] ? 0 : 1;
                currentRow[column] = Math.Min(
                    Math.Min(currentRow[column - 1] + 1, previousRow[column] + 1),
                    previousRow[column - 1] + substitutionCost);
            }

            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return previousRow[right.Length];
    }
}
