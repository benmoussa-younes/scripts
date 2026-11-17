namespace Scripts.EndToEnd.Tests;

/// <summary>
/// Regression for a low descriptor chosen for the standard-input file: with scripts's own fd 0 closed, the opened file can
/// receive fd 0 (or fd 1 under run_inherit). The launch must move it above the standard descriptors before
/// <c>adddup2</c>, or the child's input is a no-op/O_CLOEXEC-closed (fd 0) or its stdout (fd 1).
/// </summary>
public sealed class ClosedStandardInputEndToEndTests
{
    [Fact]
    public void A_file_standard_input_is_fed_when_bcs_standard_input_is_closed()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "input.txt"), "contenu\n");
            var script = ScriptsProcess.WriteScript(
                directory,
                "closed-stdin.scs",
                """
                let outcome = process.run(process.request("cat", []).with_standard_input_from_file(p"input.txt"))?
                io.write_standard_output(outcome.standard_output)
                """);

            var result = ScriptsProcess.RunWithClosedStandardInput(directory, script);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("contenu\n", result.StandardOutput);
            Assert.Equal(string.Empty, result.StandardError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
