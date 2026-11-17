using Scripts.Core;

namespace Scripts.Runtime;

/// <summary>Fatal runtime failure (SCRIPTS-RUNTIME-NNNN, exit code 253). The interpreter attaches the position of the current statement.</summary>
public sealed class RuntimeFailure : Exception
{
    public RuntimeFailure(string code, params (string Name, string Value)[] arguments)
        : base(code)
    {
        Code = code;
        Arguments = arguments;
    }

    public string Code { get; }

    public (string Name, string Value)[] Arguments { get; }
}

/// <summary>process.exit_with_code(n): flush output and terminate with the given code (0..255, validated by the caller).</summary>
public sealed class ScriptExit(int exitCode) : Exception($"process.exit_with_code({exitCode})")
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>Write on a closed standard output (EPIPE): terminate silently with code 141.</summary>
public sealed class BrokenPipeTermination() : Exception("EPIPE");

/// <summary>SIGINT/SIGTERM/SIGHUP received: unwinds to the top level, which reports SCRIPTS-RUNTIME-0018 and exits with ExitCode.</summary>
public sealed class InterruptionTermination(string signalName, int exitCode) : Exception($"interrupted by {signalName}")
{
    public string SignalName { get; } = signalName;

    public int ExitCode { get; } = exitCode;
}
