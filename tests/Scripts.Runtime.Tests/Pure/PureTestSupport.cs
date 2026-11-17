namespace Scripts.Runtime.Tests.Pure;

internal sealed class DelegateFunction(int arity, Func<Value[], Value> body) : FunctionValue
{
    public override int Arity { get; } = arity;

    public Value Invoke(IReadOnlyList<Value> arguments) => body([.. arguments]);
}

internal static class PureTestSupport
{
    public static RuntimeContext Context { get; } = new()
    {
        FunctionInvoker = (function, arguments) => ((DelegateFunction)function).Invoke(arguments),
    };

    public static Value Call(string qualifiedName, params Value[] arguments) =>
        StdlibRegistry.Get(qualifiedName)(Context, arguments);

    public static StringValue Text(string text) => new(text);

    public static PathValue Path(string text) => new(text);

    public static IntegerValue Integer(long number) => new(number);

    public static FloatValue Float(double number) => new(number);

    public static BooleanValue Boolean(bool flag) => BooleanValue.From(flag);

    public static ListValue List(params Value[] items) => new([.. items]);

    public static ListValue TextList(params string[] items) => new([.. items.Select(item => (Value)new StringValue(item))]);

    public static ListValue IntegerList(params long[] items) => new([.. items.Select(item => (Value)new IntegerValue(item))]);

    public static DelegateFunction Function(int arity, Func<Value[], Value> body) => new(arity, body);

    public static string[] ToTexts(Value value) => [.. ((ListValue)value).Items.Select(item => ((StringValue)item).Text)];

    public static Value Payload(Value result) => ((ResultValue)result).Payload;

    public static Value OkPayload(Value result)
    {
        var typed = (ResultValue)result;
        Assert.True(typed.IsOk, "ok(...) expected");
        return typed.Payload;
    }

    public static string OkText(Value result) => ((StringValue)OkPayload(result)).Text;

    public static long OkInteger(Value result) => ((IntegerValue)OkPayload(result)).Number;

    public static double OkFloat(Value result) => ((FloatValue)OkPayload(result)).Number;

    public static RecordValue ErrorRecord(Value result)
    {
        var typed = (ResultValue)result;
        Assert.False(typed.IsOk, "error(...) expected");
        return (RecordValue)typed.Payload;
    }

    public static void AssertValueError(Value result, string expectedKind)
    {
        var error = ErrorRecord(result);
        Assert.Equal("error", error.TypeName);
        Assert.Equal(expectedKind, error.GetText("kind"));
        Assert.False(string.IsNullOrEmpty(error.GetText("message")));
    }

    public static RuntimeFailure AssertFailure(string expectedCode, Action action)
    {
        var failure = Assert.Throws<RuntimeFailure>(action);
        Assert.Equal(expectedCode, failure.Code);
        return failure;
    }

    public static string ArgumentValue(RuntimeFailure failure, string name) =>
        failure.Arguments.Single(argument => argument.Name == name).Value;
}
