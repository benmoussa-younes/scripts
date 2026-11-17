using Scripts.Runtime;

namespace Scripts.Runtime.Platform;

internal static class SystemLimits
{
    /// <summary>DESIGN §5.8: maximum string length (UTF-16 units) and list length.</summary>
    public const int MaximumUnits = 1 << 28;

    public static RuntimeFailure ListTooLong(long length) =>
        new("SCRIPTS-RUNTIME-0014", ("length", length.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public static RuntimeFailure TextTooLong(long length) =>
        new("SCRIPTS-RUNTIME-0013", ("length", length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}
