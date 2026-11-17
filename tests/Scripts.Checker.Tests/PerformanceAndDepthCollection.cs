namespace Scripts.Checker.Tests;

/// <summary>
/// Runs the checker performance guards apart from the other collections of this assembly; the measured
/// numbers and the bound decision live in
/// <see cref="PerformanceAndDepthTests.Many_unknown_names_are_checked_in_reasonable_time"/>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceAndDepthCollection
{
    public const string Name = "Checker performance (alone)";
}
