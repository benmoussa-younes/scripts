using Scripts.Core;

namespace Scripts.Checker;

/// <summary>Collects diagnostics of every file; the same code at the same span is reported once.</summary>
internal sealed class DiagnosticSink
{
    private readonly List<Diagnostic> errors = [];
    private readonly List<Diagnostic> warnings = [];
    private readonly HashSet<(SourceFile File, int Start, int End, string Code)> reported = [];

    public IReadOnlyList<Diagnostic> Errors => errors;

    public IReadOnlyList<Diagnostic> Warnings => warnings;

    public void Report(string code, SourceFile file, Span span, params (string Name, string Value)[] arguments) =>
        Report(code, file, span, null, arguments);

    public void Report(string code, SourceFile file, Span span, Fix? fix, params (string Name, string Value)[] arguments)
    {
        if (!reported.Add((file, span.Start, span.End, code)))
        {
            return;
        }

        var diagnostic = Diagnostics.Create(code, file, span, fix, arguments);
        if (diagnostic.Severity == Severity.Warning)
        {
            warnings.Add(diagnostic);
        }
        else
        {
            errors.Add(diagnostic);
        }
    }
}
