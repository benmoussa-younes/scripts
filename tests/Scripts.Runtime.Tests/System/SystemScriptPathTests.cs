using Scripts.Runtime.Interpreter;
using Scripts.Runtime.Tests.InterpreterRuns;
using Scripts.Syntax;

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>
/// <c>process.script_path()</c> (SPEC-STDLIB §7.3): the entry script's absolute canonical path, fixed by the loader
/// before execution and stable after <c>filesystem.set_current_directory</c>. The real-filesystem aspect is covered in EndToEnd.
/// </summary>
[Collection(SystemTestCollection.Name)]
public sealed class SystemScriptPathTests
{
    [Fact]
    public void Script_path_is_the_canonical_entry_path()
    {
        var run = RunAt("/work/main.scs", "io.write_standard_output_line(process.script_path().to_string())\n");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("/work/main.scs\n", run.StandardOutput);
    }

    [Fact]
    public void Script_path_is_stable_after_change_directory()
    {
        using var workingDirectory = new TemporaryWorkingDirectory();
        var run = RunAt(
            "/work/main.scs",
            """
            let before = process.script_path().to_string()
            filesystem.set_current_directory(p"/")?
            let after = process.script_path().to_string()
            io.write_standard_output_line("{before}|{after}")
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("/work/main.scs|/work/main.scs\n", run.StandardOutput);
    }

    private static ProgramRun RunAt(string entryPath, string source)
    {
        var provider = new InMemorySourceProvider().AddText(entryPath, source);
        var loaded = ProgramLoader.Load(entryPath, provider);
        Assert.True(loaded.Error is null, loaded.Error?.Message);

        using var context = RuntimeContext.CreateForCapture(Array.Empty<string>(), out var captured, listenForSignals: false);
        var outcome = ProgramInterpreter.Run(loaded.Program!, context);
        context.Shutdown();
        return new ProgramRun(captured.ReadStandardOutput(), captured.ReadStandardError(), outcome);
    }
}
