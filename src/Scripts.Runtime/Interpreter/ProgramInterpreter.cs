using Scripts.Core;
using Scripts.Runtime.Parallel;

namespace Scripts.Runtime.Interpreter;

/// <summary>Tree-walking evaluator (DESIGN 3 to 6). Every failure is mapped to an outcome; only internal errors escape, as InvalidOperationException.</summary>
public static class ProgramInterpreter
{
    /// <summary>The caller owns the context: it must call Shutdown and Dispose afterwards.</summary>
    public static ExecutionOutcome Run(LoadedProgram program, RuntimeContext context)
    {
        // Refcounted: several runs can be concurrent in the same process (long-lived host);
        // the shared Core.Concurrency context is destroyed only at the end of the LAST one (see ParallelPool.Quiesce).
        ParallelPool.BeginRun();
        try
        {
            return new ProgramExecution(program, context).Execute();
        }
        finally
        {
            // The parallel worker threads are pooled across the calls of one run and reused; at the end of
            // the run they are joined, so no worker of a finished run is still parked when the caller returns
            // (a long-lived host running several scripts gets a clean context per run, created on first use).
            ParallelPool.Quiesce();
        }
    }
}
