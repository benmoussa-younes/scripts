using System.Diagnostics;
using Scripts.Runtime.Interpreter;

namespace Scripts.Runtime.Tests.InterpreterRuns;

/// <summary>Non-parallel collection: this test checks the interaction of the shared pool between concurrent runs.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InterpreterConcurrentRunCollection
{
    public const string Name = "Interpreter concurrent runs (alone)";
}

/// <summary>
/// Regression: two scripts runs in the SAME process, one of them with a <c>parallel</c> job, must
/// never deadlock. The worker pool is a process singleton and
/// <c>ProgramInterpreter.Run</c> calls <c>Quiesce</c> on exit: a run that finishes
/// while another has a job in flight must not leave that job blocked on its gate.
/// </summary>
[Collection(InterpreterConcurrentRunCollection.Name)]
public sealed class InterpreterConcurrentRunTests(ITestOutputHelper output)
{
    private const string ParallelProgram = """
        let squared = parallel.transform(range(0, 4000), 8, (n: integer) => n * n)?
        io.write_standard_output_line("{squared.length()}")
        """;

    [Fact(Timeout = 30000)]
    public async Task TwoConcurrentRunsWithAParallelJobDoNotDeadlock()
    {
        var stopwatch = Stopwatch.StartNew();
        var slow = Task.Run(() => InterpreterHarness.Output(ParallelProgram));
        var quick = Task.Run(() => InterpreterHarness.Output("io.write_standard_output_line(\"ok\")\n"));

        var completed = await Task.WhenAll(slow, quick).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.Equal("4000\n", completed[0]);
        Assert.Equal("ok\n", completed[1]);
        output.WriteLine($"termine en {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact(Timeout = 30000)]
    public async Task ManyConcurrentRunsStayCorrect()
    {
        var runs = Enumerable.Range(0, 6).Select(index => Task.Run(() => index % 2 == 0
            ? InterpreterHarness.Output(ParallelProgram)
            : InterpreterHarness.Output("io.write_standard_output_line(\"ok\")\n"))).ToArray();

        var results = await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        for (var index = 0; index < results.Length; index++)
        {
            Assert.Equal(index % 2 == 0 ? "4000\n" : "ok\n", results[index]);
        }
    }
}
