namespace Scripts.Runtime;

/// <summary>Ordering (ordinal UTF-16) and structural equality of values, shared by the operators, sorted and contains.</summary>
public static class ValueComparison
{
    public static IComparer<Value> Ordering { get; } = Comparer<Value>.Create(Compare);

    /// <summary>Negative, zero or positive for integer, float, string or path operands of the same type; -0.0 equals 0.0.</summary>
    public static int Compare(Value left, Value right) => (left, right) switch
    {
        (IntegerValue leftInteger, IntegerValue rightInteger) => leftInteger.Number.CompareTo(rightInteger.Number),
        (FloatValue leftFloat, FloatValue rightFloat) => leftFloat.Number.CompareTo(rightFloat.Number),
        (StringValue leftString, StringValue rightString) => string.CompareOrdinal(leftString.Text, rightString.Text),
        (PathValue leftPath, PathValue rightPath) => string.CompareOrdinal(leftPath.Text, rightPath.Text),
        _ => throw new ArgumentException($"values not orderable: {left.GetType().Name} and {right.GetType().Name}"),
    };

    /// <summary>Structural equality (`==`): containers compare element-wise, maps ignore insertion order.</summary>
    public static bool AreEqual(Value left, Value right) => left.Equals(right);
}
