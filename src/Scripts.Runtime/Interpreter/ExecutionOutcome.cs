using Scripts.Core;
using Scripts.Core.Reporting;

namespace Scripts.Runtime.Interpreter;

/// <summary>How a run ended; <paramref name="Result"/> is the value of a top-level <c>return</c> (null when none was executed).</summary>
public sealed record ExecutionOutcome(int ExitCode, RunTermination Termination, Diagnostic? Failure, Value? Result);
