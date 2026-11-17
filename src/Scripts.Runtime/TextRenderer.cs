using System.Globalization;

namespace Scripts.Runtime;

/// <summary>Interpolation text of the interpolable types (DESIGN 5.5): string, integer, float, boolean, path.</summary>
public static class TextRenderer
{
    public static string Render(Value value) => value switch
    {
        StringValue text => text.Text,
        PathValue path => path.Text,
        IntegerValue integer => integer.Number.ToString(CultureInfo.InvariantCulture),
        FloatValue number => FloatFormatter.Render(number.Number),
        BooleanValue boolean => boolean.Flag ? "true" : "false",
        _ => throw new ArgumentException($"type not interpolable: {value.GetType().Name}", nameof(value)),
    };
}
