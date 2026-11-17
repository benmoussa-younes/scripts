using Scripts.Core;

namespace Scripts.Checker;

/// <summary>Static semantic diagnostics of a loaded program: errors block execution, warnings never do.</summary>
public sealed record CheckResult(IReadOnlyList<Diagnostic> Errors, IReadOnlyList<Diagnostic> Warnings);
