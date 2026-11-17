// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Serialization.Values;

/// <summary>
/// Orders two cells of the same column. A null sorts after every non-null value,
/// which keeps missing data at the bottom of an ascending and a descending sort.
/// </summary>
internal static class DataValueComparer
{
    internal static int Compare(DataValue left, DataValue right)
    {
        if (left.IsNull || right.IsNull)
        {
            return left.IsNull
                ? right.IsNull ? 0 : 1
                : -1;
        }

        // A DataColumn carries one kind, so a cross-kind pair is only possible in a
        // hand-built value; a zero keeps the method total.
        if (left.Kind != right.Kind)
        {
            return 0;
        }

        // A non-null DataValue cannot carry DataKind.Null, so that enum value has no
        // comparison arm and the compiler's exhaustiveness warning is expected.
#pragma warning disable CS8509
        return left.Kind switch
        {
            DataKind.Boolean => CompareBoolean(left, right),
            DataKind.Integer => CompareInteger(left, right),
            DataKind.Number => CompareNumber(left, right),
            DataKind.Decimal => CompareDecimal(left, right),
            DataKind.Date => CompareDate(left, right),
            DataKind.Time => CompareTime(left, right),
            DataKind.Timestamp => CompareTimestamp(left, right),
            DataKind.Text => CompareText(left, right),
            DataKind.Node => CompareNode(left, right),
        };
#pragma warning restore CS8509
    }

    private static int CompareBoolean(DataValue left, DataValue right)
    {
        _ = left.TryGetBoolean(out var leftValue);
        _ = right.TryGetBoolean(out var rightValue);
        return leftValue.CompareTo(rightValue);
    }

    private static int CompareInteger(DataValue left, DataValue right)
    {
        _ = left.TryGetInteger(out var leftValue);
        _ = right.TryGetInteger(out var rightValue);
        return leftValue.CompareTo(rightValue);
    }

    private static int CompareNumber(DataValue left, DataValue right)
    {
        _ = left.TryGetNumber(out var leftValue);
        _ = right.TryGetNumber(out var rightValue);
        return leftValue.CompareTo(rightValue);
    }

    private static int CompareDecimal(DataValue left, DataValue right)
    {
        _ = left.TryGetDecimal(out var leftValue);
        _ = right.TryGetDecimal(out var rightValue);
        return leftValue.CompareTo(rightValue);
    }

    private static int CompareDate(DataValue left, DataValue right)
    {
        _ = left.TryGetDate(out var leftValue);
        _ = right.TryGetDate(out var rightValue);
        return leftValue.CompareTo(rightValue);
    }

    private static int CompareTime(DataValue left, DataValue right)
    {
        _ = left.TryGetTime(out var leftValue);
        _ = right.TryGetTime(out var rightValue);
        return leftValue.CompareTo(rightValue);
    }

    private static int CompareTimestamp(DataValue left, DataValue right)
    {
        _ = left.TryGetTimestamp(out var leftValue);
        _ = right.TryGetTimestamp(out var rightValue);
        return leftValue.CompareTo(rightValue);
    }

    private static int CompareText(DataValue left, DataValue right)
    {
        _ = left.TryGetText(out var leftValue);
        _ = right.TryGetText(out var rightValue);
        return string.CompareOrdinal(leftValue, rightValue);
    }

    private static int CompareNode(DataValue left, DataValue right)
    {
        // A node has no single invariant text form, but its summary is
        // deterministic: a scalar's value, or "List(n)"/"Map(n)". Equal summaries
        // keep the original row order through the sort's index tie-break.
        _ = left.TryGetNode(out var leftValue);
        _ = right.TryGetNode(out var rightValue);
        return string.CompareOrdinal(leftValue?.ToString(), rightValue?.ToString());
    }
}
