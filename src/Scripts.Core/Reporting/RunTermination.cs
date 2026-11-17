namespace Scripts.Core.Reporting;

/// <summary>How a <c>run --json-out</c> execution ended (DESIGN 8.4).</summary>
public enum RunTermination
{
    /// <summary>The script ran to its end or through a top-level <c>return</c>.</summary>
    Normal,

    /// <summary><c>process.exit_with_code(n)</c> ended the script (any <c>n</c>, including 0).</summary>
    ExitScript,

    /// <summary>Parse, type, module or command-line error: nothing was executed (exit code 250, 251 or 252).</summary>
    StaticError,

    /// <summary>A fatal <c>SCRIPTS-RUNTIME</c> error, including an unhandled top-level <c>error(…)</c> (exit code 253).</summary>
    RuntimeError,

    /// <summary>Interrupted by SIGHUP, SIGINT or SIGTERM (exit code 129, 130 or 143).</summary>
    Signal,
}

public static class RunTerminationNames
{
    public static string ToJsonName(this RunTermination termination) => termination switch
    {
        RunTermination.Normal => "normal",
        RunTermination.ExitScript => "exit_script",
        RunTermination.StaticError => "static_error",
        RunTermination.RuntimeError => "runtime_error",
        RunTermination.Signal => "signal",
        _ => throw new ArgumentOutOfRangeException(nameof(termination)),
    };
}
