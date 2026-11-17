using System.Diagnostics;
using System.Text;
using Scripts.Syntax;

namespace Scripts.Checker.Tests;

[Collection(PerformanceAndDepthCollection.Name)]
public sealed class PerformanceAndDepthTests
{
    [Fact]
    public void The_largest_corpus_script_checks_quickly()
    {
        var largest = Directory.GetFiles(Repository.MigrationDirectory, "*.scs")
            .Concat(Directory.GetFiles(Repository.ConformanceErrorsDirectory, "*.scs"))
            .OrderByDescending(path => new FileInfo(path).Length)
            .First();
        var loaded = ProgramLoader.Load(largest, new FileSystemSourceProvider());
        Assert.NotNull(loaded.Program);
        ProgramChecker.Check(loaded.Program);
        var stopwatch = Stopwatch.StartNew();
        ProgramChecker.Check(loaded.Program);
        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"{Path.GetFileName(largest)} took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Thousands_of_statements_check_in_reasonable_time()
    {
        var builder = new StringBuilder();
        for (var statement = 0; statement < 3000; statement++)
        {
            builder.Append("let value_").Append(statement).Append(" = ").Append(statement).Append(" + 1\n");
        }

        builder.Append("io.write_standard_output_line(\"{value_2999}\")\n");
        var loaded = Checking.Load(builder.ToString());
        var stopwatch = Stopwatch.StartNew();
        var result = ProgramChecker.Check(loaded);
        stopwatch.Stop();
        Assert.Empty(result.Errors);
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Deeply_nested_expressions_and_blocks_do_not_overflow_the_stack()
    {
        var lists = new string('[', 90) + "1" + new string(']', 90);
        Assert.Equal(
            string.Concat(Enumerable.Repeat("list<", 90)) + "integer" + new string('>', 90),
            Checking.TypeOfLastLet("let _v = " + lists).ToString());

        var sum = "1" + string.Concat(Enumerable.Repeat(" + 1", 150));
        Assert.Equal("integer", Checking.TypeOfLastLet("let _v = " + sum).ToString());

        var blocks = string.Concat(Enumerable.Repeat("if true {\n", 40)) + "io.write_standard_output_line(\"x\")\n" + string.Concat(Enumerable.Repeat("}\n", 40));
        Checking.AssertClean(blocks);

        var calls = "1";
        for (var level = 0; level < 60; level++)
        {
            calls = "identity(" + calls + ")";
        }

        Checking.AssertClean("function identity(n: integer) -> integer {\n    return n\n}\nlet _v = " + calls);
    }

    [Fact]
    public void Checking_from_a_small_stack_thread_still_works()
    {
        var program = Checking.Load("let _v = " + new string('[', 90) + "1" + new string(']', 90));
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    Assert.Empty(ProgramChecker.Check(program).Errors);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            },
            256 * 1024);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void Many_unknown_names_are_checked_in_reasonable_time()
    {
        // A4: the suggestion search used to re-sort every visible name for each unknown name (quadratic). The guard
        // fails a return to superlinear behaviour relative to the observed ~1.5 s alone; 30 s is the load-tolerant
        // ceiling. The pre-collection flake on 2026-11-08 was ~11.7 s. `DisableParallelization` only serializes
        // this assembly; the 30 s bound stays for shared-host load (BUG-010 precedent).
        var builder = new StringBuilder();
        for (var index = 0; index < 2000; index++)
        {
            builder.Append("let name_").Append(index).Append(" = ").Append(index).Append('\n');
        }

        for (var index = 0; index < 2000; index++)
        {
            builder.Append("io.write_standard_output_line(unknown_").Append(index).Append(")\n");
        }

        var program = Checking.Load(builder.ToString());
        var stopwatch = Stopwatch.StartNew();
        var result = ProgramChecker.Check(program);
        stopwatch.Stop();
        Assert.Equal(2000, result.Errors.Count);
        Assert.True(stopwatch.ElapsedMilliseconds < 30000, $"took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_very_long_unknown_identifier_is_checked_quickly()
    {
        // A4: a 32000-character identifier used to take seconds; the suggestion search is now bounded by length.
        var longName = new string('a', 32000);
        var program = Checking.Load("let x = 1\nio.write_standard_output_line(" + longName + ")\n");
        var stopwatch = Stopwatch.StartNew();
        var result = ProgramChecker.Check(program);
        stopwatch.Stop();
        Assert.Single(result.Errors);
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"took {stopwatch.ElapsedMilliseconds} ms");
    }
}
