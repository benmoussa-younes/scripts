using Scripts.Core;

namespace Scripts.Checker;

/// <summary>Tracks which values the arms of a match have already covered (DESIGN 5.2).</summary>
internal sealed class PatternCoverage(ScriptType scrutineeType, IReadOnlyList<string>? closedValues = null)
{
    private readonly HashSet<long> integers = [];
    private readonly HashSet<string> strings = new(StringComparer.Ordinal);
    private bool catchAll;
    private bool trueCovered;
    private bool falseCovered;
    private bool okCovered;
    private bool errorCovered;

    public bool HasCatchAll => catchAll;

    public bool IsFullyCovered =>
        catchAll
        // A closed set of strings (the `kind` fields of path_status, directory_entry, walk_skip and error):
        // the values are already published by describe.types[].kinds, so the coverage check uses them instead
        // of forcing a wildcard (P4).
        || (closedValues is not null && closedValues.All(strings.Contains))
        || (scrutineeType.Equals(ScriptTypes.Boolean) && trueCovered && falseCovered)
        || (scrutineeType is ResultType && okCovered && errorCovered);

    public void CoverEverything() => catchAll = true;

    /// <summary>False when the literal was already covered by an earlier pattern.</summary>
    public bool TryCoverLiteral(Expression literal) =>
        literal switch
        {
            BooleanLiteral { Value: true } => !Exchange(ref trueCovered),
            BooleanLiteral => !Exchange(ref falseCovered),
            IntegerLiteral integer => integers.Add(integer.Value),
            StringLiteral text => strings.Add(text.Value),
            _ => true,
        };

    public bool TryCoverResult(bool isOk) => !Exchange(ref isOk ? ref okCovered : ref errorCovered);

    public string DescribeMissing()
    {
        var missing = new List<string>();
        if (closedValues is not null)
        {
            var uncovered = closedValues.Where(value => !strings.Contains(value)).Select(value => $"`{value}`").ToList();
            return uncovered.Count == 0
                ? "none"
                : string.Join(", ", uncovered) + (uncovered.Count > 1 ? " are not covered" : " is not covered");
        }

        if (scrutineeType.Equals(ScriptTypes.Boolean))
        {
            AddIf(missing, !trueCovered, "`true`");
            AddIf(missing, !falseCovered, "`false`");
            return missing.Count == 0 ? "none" : string.Join(" and ", missing) + (missing.Count > 1 ? " are not covered" : " is not covered");
        }

        if (scrutineeType is ResultType)
        {
            AddIf(missing, !okCovered, "`ok(_)`");
            AddIf(missing, !errorCovered, "`error(_)`");
            return missing.Count == 0 ? "none" : string.Join(" and ", missing) + (missing.Count > 1 ? " are not covered" : " is not covered");
        }

        return "the other values are not covered (add `_`)";
    }

    private static void AddIf(List<string> missing, bool condition, string text)
    {
        if (condition)
        {
            missing.Add(text);
        }
    }

    /// <summary>Returns the previous value and sets the flag.</summary>
    private static bool Exchange(ref bool flag)
    {
        var previous = flag;
        flag = true;
        return previous;
    }
}
