using Scripts.Core;

namespace Scripts.Runtime;

/// <summary>Constructor of the single built-in error value (DESIGN 3.5) and its one-line description used by unwrap and unhandled errors.</summary>
public static class ErrorValues
{
    /// <summary>Predefined error value: error{kind, message, subject} (field order follows BuiltinTypes).</summary>
    public static RecordValue Error(string kind, string subject, string message) =>
        new("error", new StringValue(kind), new StringValue(message), new StringValue(subject));

    /// <summary>Environment failure. <paramref name="kind"/> is not_found (absent variable) or invalid_argument (invalid name/value).</summary>
    public static RecordValue EnvironmentFailure(string kind, string name, string message) =>
        new("error", new StringValue(kind), new StringValue(message), new StringValue(name));

    /// <summary>"{kind} : {subject} : {message}" when subject is present, "{kind} : {message}" otherwise (SCRIPTS-RUNTIME-0007 and -0017).</summary>
    public static string Describe(Value errorPayload) => errorPayload switch
    {
        RecordValue { TypeName: "error" } record => DescribeError(record),
        RecordValue record => record.TypeName,
        StringValue or PathValue or IntegerValue or FloatValue or BooleanValue => TextRenderer.Render(errorPayload),
        ListValue => "list",
        MapValue => "map",
        ResultValue => "result",
        FunctionValue => "function",
        _ => "unit",
    };

    private static string DescribeError(RecordValue record)
    {
        var kind = record.GetText("kind");
        var message = record.GetText("message");
        var subject = record.GetText("subject");
        return subject.Length == 0 ? $"{kind} : {message}" : $"{kind} : {subject} : {message}";
    }
}
