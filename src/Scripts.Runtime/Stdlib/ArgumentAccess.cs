namespace Scripts.Runtime;

/// <summary>Typed reads of stdlib arguments; the checker guarantees the types, so a mismatch is a programming error.</summary>
internal static class ArgumentAccess
{
    public static string Text(Value[] arguments, int position) => ((StringValue)arguments[position]).Text;

    public static string PathTextOf(Value[] arguments, int position) => ((PathValue)arguments[position]).Text;

    public static long Integer(Value[] arguments, int position) => ((IntegerValue)arguments[position]).Number;

    public static double Float(Value[] arguments, int position) => ((FloatValue)arguments[position]).Number;

    public static ListValue List(Value[] arguments, int position) => (ListValue)arguments[position];

    public static MapValue Map(Value[] arguments, int position) => (MapValue)arguments[position];

    public static ResultValue Result(Value[] arguments, int position) => (ResultValue)arguments[position];

    public static FunctionValue Function(Value[] arguments, int position) => (FunctionValue)arguments[position];
}
