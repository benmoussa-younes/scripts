using System.Globalization;
using Scripts.Core;

namespace Scripts.Runtime;

/// <summary>Checked operators of DESIGN 3.2 and 5.6 on runtime values; the interpreter calls these verbatim. Operand types are already validated by the checker.</summary>
public static class ArithmeticOperations
{
    public static Value Add(Value left, Value right) => (left, right) switch
    {
        (IntegerValue leftInteger, IntegerValue rightInteger) => IntegerValue.From(AddIntegers(leftInteger.Number, rightInteger.Number)),
        (FloatValue leftFloat, FloatValue rightFloat) => new FloatValue(FiniteFloat(leftFloat.Number + rightFloat.Number, leftFloat.Number, "+", rightFloat.Number)),
        (StringValue leftString, StringValue rightString) => StringValue.Concatenate(leftString, rightString),
        (ListValue leftList, ListValue rightList) => Concatenate(leftList, rightList),
        _ => throw UnsupportedOperands("+", left, right),
    };

    public static Value Subtract(Value left, Value right) => (left, right) switch
    {
        (IntegerValue leftInteger, IntegerValue rightInteger) => IntegerValue.From(SubtractIntegers(leftInteger.Number, rightInteger.Number)),
        (FloatValue leftFloat, FloatValue rightFloat) => new FloatValue(FiniteFloat(leftFloat.Number - rightFloat.Number, leftFloat.Number, "-", rightFloat.Number)),
        _ => throw UnsupportedOperands("-", left, right),
    };

    public static Value Multiply(Value left, Value right) => (left, right) switch
    {
        (IntegerValue leftInteger, IntegerValue rightInteger) => IntegerValue.From(MultiplyIntegers(leftInteger.Number, rightInteger.Number)),
        (FloatValue leftFloat, FloatValue rightFloat) => new FloatValue(FiniteFloat(leftFloat.Number * rightFloat.Number, leftFloat.Number, "*", rightFloat.Number)),
        _ => throw UnsupportedOperands("*", left, right),
    };

    /// <summary>integer/integer truncates toward zero; float/float; path/string and path/path join paths (DESIGN 3.2.1).</summary>
    public static Value Divide(Value left, Value right) => (left, right) switch
    {
        (IntegerValue leftInteger, IntegerValue rightInteger) => IntegerValue.From(DivideIntegers(leftInteger.Number, rightInteger.Number)),
        (FloatValue leftFloat, FloatValue rightFloat) => new FloatValue(DivideFloats(leftFloat.Number, rightFloat.Number)),
        (PathValue leftPath, StringValue rightString) => new PathValue(JoinPath(leftPath.Text, rightString.Text)),
        (PathValue leftPath, PathValue rightPath) => new PathValue(JoinPath(leftPath.Text, rightPath.Text)),
        _ => throw UnsupportedOperands("/", left, right),
    };

    /// <summary>SCRIPTS-RUNTIME-0022: the `/` operator rejects a computed absolute or empty segment before joining (DESIGN 3.2.1).</summary>
    private static string JoinPath(string basePath, string childSegment)
    {
        if (childSegment.Length == 0 || childSegment[0] == '/')
        {
            throw new RuntimeFailure(ErrorCodes.Runtime0022, ("value", Diagnostics.PrintableValue(childSegment)));
        }

        return LexicalPath.Join(basePath, childSegment);
    }

    /// <summary>integer % integer with the sign of the dividend.</summary>
    public static Value Remainder(Value left, Value right)
    {
        if (left is not IntegerValue dividend || right is not IntegerValue divisor)
        {
            throw UnsupportedOperands("%", left, right);
        }

        if (divisor.Number == 0)
        {
            throw new RuntimeFailure(ErrorCodes.Runtime0002, ("operation", Describe(dividend.Number, "%", divisor.Number)));
        }

        if (dividend.Number == long.MinValue && divisor.Number == -1)
        {
            throw OverflowFailure(Describe(dividend.Number, "%", divisor.Number));
        }

        return IntegerValue.From(dividend.Number % divisor.Number);
    }

    public static Value Negate(Value operand) => operand switch
    {
        IntegerValue integer when integer.Number == long.MinValue =>
            throw OverflowFailure(string.Create(CultureInfo.InvariantCulture, $"-({integer.Number})")),
        IntegerValue integer => IntegerValue.From(-integer.Number),
        FloatValue number => new FloatValue(-number.Number),
        _ => throw new ArgumentException($"non-numeric operand: {operand.GetType().Name}", nameof(operand)),
    };

    private static long AddIntegers(long left, long right)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException)
        {
            throw OverflowFailure(Describe(left, "+", right));
        }
    }

    private static long SubtractIntegers(long left, long right)
    {
        try
        {
            return checked(left - right);
        }
        catch (OverflowException)
        {
            throw OverflowFailure(Describe(left, "-", right));
        }
    }

    private static long MultiplyIntegers(long left, long right)
    {
        try
        {
            return checked(left * right);
        }
        catch (OverflowException)
        {
            throw OverflowFailure(Describe(left, "*", right));
        }
    }

    private static long DivideIntegers(long dividend, long divisor)
    {
        if (divisor == 0)
        {
            throw new RuntimeFailure(ErrorCodes.Runtime0002, ("operation", Describe(dividend, "/", divisor)));
        }

        if (dividend == long.MinValue && divisor == -1)
        {
            throw OverflowFailure(Describe(dividend, "/", divisor));
        }

        return dividend / divisor;
    }

    private static double DivideFloats(double dividend, double divisor)
    {
        if (divisor == 0.0)
        {
            throw new RuntimeFailure(
                ErrorCodes.Runtime0003,
                ("reason", "division by zero"),
                ("operation", $"{FloatFormatter.Render(dividend)} / {FloatFormatter.Render(divisor)}"));
        }

        return FiniteFloat(dividend / divisor, dividend, "/", divisor);
    }

    private static double FiniteFloat(double result, double left, string operatorText, double right)
    {
        if (!double.IsFinite(result))
        {
            throw new RuntimeFailure(
                ErrorCodes.Runtime0003,
                ("reason", "overflow"),
                ("operation", $"{FloatFormatter.Render(left)} {operatorText} {FloatFormatter.Render(right)}"));
        }

        return result;
    }

    private static ListValue Concatenate(ListValue left, ListValue right)
    {
        SizeLimits.EnsureListLength((long)left.Items.Count + right.Items.Count);
        var items = new List<Value>(left.Items.Count + right.Items.Count);
        foreach (var item in left.Items)
        {
            items.Add(item.DeepCopy());
        }

        foreach (var item in right.Items)
        {
            items.Add(item.DeepCopy());
        }

        return new ListValue(items);
    }

    private static RuntimeFailure OverflowFailure(string operation) =>
        new(ErrorCodes.Runtime0001, ("operation", operation));

    private static string Describe(long left, string operatorText, long right) =>
        string.Create(CultureInfo.InvariantCulture, $"{left} {operatorText} {right}");

    private static ArgumentException UnsupportedOperands(string operatorText, Value left, Value right) =>
        new($"unsupported operands for `{operatorText}`: {left.GetType().Name} and {right.GetType().Name}");
}
