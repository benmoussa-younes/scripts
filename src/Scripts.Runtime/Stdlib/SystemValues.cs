using System.Globalization;
using Scripts.Runtime.Platform;
using Scripts.Runtime.Processes;

namespace Scripts.Runtime.Stdlib;

/// <summary>Argument extraction and result construction shared by the system stdlib functions.</summary>
internal static class SystemValues
{
    public static string TextAt(Value[] arguments, int index) => ((StringValue)arguments[index]).Text;

    public static string PathAt(Value[] arguments, int index) => ((PathValue)arguments[index]).Text;

    public static long IntegerAt(Value[] arguments, int index) => ((IntegerValue)arguments[index]).Number;

    public static List<string> TextListAt(Value[] arguments, int index)
    {
        var items = ((ListValue)arguments[index]).Items;
        var texts = new List<string>(items.Count);
        foreach (var item in items)
        {
            texts.Add(((StringValue)item).Text);
        }

        return texts;
    }

    public static ResultValue Success() => ResultValue.Ok(UnitValue.Instance);

    public static ResultValue Success(Value payload) => ResultValue.Ok(payload);

    public static ResultValue IoFailure(IoFailureException failure) =>
        ResultValue.Error(ErrorValues.Error(failure.Kind, failure.Subject, failure.Reason));

    public static ResultValue ProcessFailure(ProcessFailureException failure) =>
        ResultValue.Error(ErrorValues.Error(failure.Kind, failure.Program, failure.Reason));

    public static string Invariant(long number) => number.ToString(CultureInfo.InvariantCulture);

    /// <summary>Runs a filesystem operation, turning IoFailureException into error.</summary>
    public static Value GuardIo(Func<Value> operation)
    {
        try
        {
            return operation();
        }
        catch (IoFailureException failure)
        {
            return IoFailure(failure);
        }
    }

    /// <summary>Runs a process operation, turning ProcessFailureException into error.</summary>
    public static Value GuardProcess(Func<Value> operation)
    {
        try
        {
            return operation();
        }
        catch (ProcessFailureException failure)
        {
            return ProcessFailure(failure);
        }
    }
}
