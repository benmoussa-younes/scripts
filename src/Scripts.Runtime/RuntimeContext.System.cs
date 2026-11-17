using Scripts.Runtime.Output;
using Scripts.Runtime.Platform;
using Scripts.Runtime.Processes;
using Scripts.Runtime.Signals;

namespace Scripts.Runtime;

/// <summary>
/// System services of a run: output channels, script arguments, signal interruption, child processes.
/// CLI sequence: create (CreateForConsole / CreateForCapture), run the script calling ThrowIfInterrupted between
/// statements, then on every end (normal, ScriptExit, failure, interruption) call Shutdown() and Dispose().
/// </summary>
public sealed partial class RuntimeContext : IDisposable
{
    private static readonly TimeSpan ForcedExitFlushTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>Context writing on the real stdout/stderr, without arguments and without signal handlers (library use, tests).</summary>
    public RuntimeContext()
        : this(FileDescriptorOutputChannel.ForStandardOutput(), FileDescriptorOutputChannel.ForStandardError(), [])
    {
    }

    /// <param name="capturesChildStreams">Capture mode: process.run_inherit pumps the child's stdout/stderr into the channels instead of inheriting them.</param>
    /// <param name="forceExit">Watchdog action after the interruption delay; defaults to a best-effort flush then Environment.Exit.</param>
    /// <param name="watchdogDelay">Defaults to 5 seconds (DESIGN §6).</param>
    public RuntimeContext(
        ITrackedOutputChannel standardOutput,
        ITrackedOutputChannel standardError,
        IReadOnlyList<string> scriptArguments,
        bool capturesChildStreams = false,
        Action<int>? forceExit = null,
        TimeSpan? watchdogDelay = null)
    {
        StandardOutput = standardOutput;
        StandardError = standardError;
        ScriptArguments = scriptArguments;
        CapturesChildStreams = capturesChildStreams;
        Interruption = new InterruptionMonitor(forceExit ?? ForceProcessExit, watchdogDelay ?? InterruptionMonitor.DefaultWatchdogDelay);
        Interruption.Interrupted += OnInterrupted;
        StandardInput = new StandardInputReader(Interruption);
    }

    /// <summary>Buffered stdout; flushed before any eprint, before process.run_inherit/process.run_shell, by Shutdown and on signal.</summary>
    public ITrackedOutputChannel StandardOutput { get; }

    /// <summary>stderr; its IsAtLineStart tells the `--errors-json` writer whether a newline must precede the JSON line.</summary>
    public ITrackedOutputChannel StandardError { get; }

    /// <summary>Arguments after the script path, in order (process.arguments()).</summary>
    public IReadOnlyList<string> ScriptArguments { get; }

    /// <summary>
    /// Absolute canonical path of the entry script (symlinks resolved by the loader), set once by the interpreter
    /// before execution and stable after <c>filesystem.set_current_directory</c> (SPEC-STDLIB §7.3 process.script_path).
    /// </summary>
    public string ScriptPath { get; internal set; } = string.Empty;

    /// <summary>True in capture mode (--json-out).</summary>
    public bool CapturesChildStreams { get; }

    public InterruptionMonitor Interruption { get; }

    /// <summary>The run's buffered standard-input state, shared by io.read_line and io.read_standard_input (SPEC-STDLIB §7.10).</summary>
    internal StandardInputReader StandardInput { get; set; } = new();

    internal ChildProcessTable ChildProcesses { get; } = new();

    internal SpawnedProcessTable SpawnedProcesses { get; } = new();

    /// <summary>Real stdout/stderr context with SIGINT/SIGTERM/SIGHUP handling (unless listenForSignals is false).</summary>
    public static RuntimeContext CreateForConsole(IReadOnlyList<string> scriptArguments, bool listenForSignals = true)
    {
        var context = new RuntimeContext(
            FileDescriptorOutputChannel.ForStandardOutput(),
            FileDescriptorOutputChannel.ForStandardError(),
            scriptArguments);
        return context.StartListening(listenForSignals);
    }

    /// <summary>Capture mode for `--json-out`: stdout/stderr (and process.run_inherit children) go to memory buffers read through capturedOutput.</summary>
    public static RuntimeContext CreateForCapture(IReadOnlyList<string> scriptArguments, out CapturedOutput capturedOutput, bool listenForSignals = true)
    {
        var standardOutput = new MemoryOutputChannel();
        var standardError = new MemoryOutputChannel();
        capturedOutput = new CapturedOutput(standardOutput, standardError);
        var context = new RuntimeContext(standardOutput, standardError, scriptArguments, capturesChildStreams: true);
        return context.StartListening(listenForSignals);
    }

    /// <summary>Throws InterruptionTermination when SIGINT/SIGTERM/SIGHUP was received; the interpreter calls it before each statement and loop turn.</summary>
    public void ThrowIfInterrupted() => Interruption.ThrowIfInterrupted();

    /// <summary>Flushes stdout then stderr. May throw BrokenPipeTermination or RuntimeFailure SCRIPTS-RUNTIME-0019.</summary>
    public void FlushAll()
    {
        try
        {
            StandardOutput.Flush();
        }
        finally
        {
            StandardError.Flush();
        }
    }

    /// <summary>End-of-run sequence: terminate unwaited children (SIGTERM, SIGKILL after 2 s) then FlushAll.</summary>
    public void Shutdown()
    {
        ChildProcesses.TerminateAllAndWait();
        FlushAll();
    }

    public void Dispose() => Interruption.Dispose();

    private RuntimeContext StartListening(bool listenForSignals)
    {
        if (listenForSignals)
        {
            Interruption.ListenForSignals();
        }

        return this;
    }

    /// <summary>Terminates the run's children when an interruption ends the run.</summary>
    private void OnInterrupted() => ChildProcesses.TerminateAllAfterInterruption();

    private void ForceProcessExit(int exitCode)
    {
        StandardOutput.TryFlush(ForcedExitFlushTimeout);
        StandardError.TryFlush(ForcedExitFlushTimeout);
        Environment.Exit(exitCode);
    }
}
