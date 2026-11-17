using System.Globalization;
using Scripts.Core;

namespace Scripts.Runtime;

/// <summary>Length limits of strings, paths and lists (DESIGN 5.8): more than 2^28 units or elements is a fatal runtime failure.</summary>
public static class SizeLimits
{
    public static void EnsureStringLength(long length)
    {
        if (length > Limits.MaximumCollectionLength)
        {
            throw new RuntimeFailure(ErrorCodes.Runtime0013, ("length", length.ToString(CultureInfo.InvariantCulture)));
        }
    }

    public static void EnsureListLength(long length)
    {
        if (length > Limits.MaximumCollectionLength)
        {
            throw new RuntimeFailure(ErrorCodes.Runtime0014, ("length", length.ToString(CultureInfo.InvariantCulture)));
        }
    }
}
