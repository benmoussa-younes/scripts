namespace Scripts.Runtime.Output;

/// <summary>Output channel that also reports whether the next character would start a new line.</summary>
public interface ITrackedOutputChannel : IOutputChannel
{
    /// <summary>True when nothing was written yet or the last character written is '\n' (column 0).</summary>
    bool IsAtLineStart { get; }

    /// <summary>Best-effort flush that gives up (returns false) if the channel stays busy or fails; used by the signal watchdog.</summary>
    bool TryFlush(TimeSpan timeout);
}
