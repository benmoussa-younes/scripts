namespace Scripts.Cli.Tests.Reporting;

/// <summary>
/// A non-parallel collection disables parallelization only within this test assembly; the other test
/// assemblies still run concurrently and compete for the CPU. See
/// <see cref="DiagnosticReporterTests.Fifteen_thousand_diagnostics_on_one_long_line_render_quickly"/> for the
/// measured numbers and the decision behind the bound.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DiagnosticReporterCollection
{
    public const string Name = "Diagnostic rendering (serial)";
}
