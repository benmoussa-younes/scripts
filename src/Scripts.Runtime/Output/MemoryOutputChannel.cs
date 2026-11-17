using System.Text;
using Scripts.Runtime.Platform;

namespace Scripts.Runtime.Output;

/// <summary>In-memory UTF-8 sink used by capture mode (--json-out); exceeding 2^28 UTF-16 units is SCRIPTS-RUNTIME-0013.</summary>
public sealed class MemoryOutputChannel : ITrackedOutputChannel
{
    private const long MaximumStoredBytes = 3L * SystemLimits.MaximumUnits;

    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly object gate = new();
    private readonly MemoryStream storage = new();
    private volatile bool atLineStart = true;

    public bool IsAtLineStart => atLineStart;

    public void Write(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        lock (gate)
        {
            atLineStart = text[^1] == '\n';
            storage.Write(Utf8.GetBytes(text));
            if (storage.Length > MaximumStoredBytes)
            {
                throw SystemLimits.TextTooLong(storage.Length);
            }
        }
    }

    public void Flush()
    {
    }

    public bool TryFlush(TimeSpan timeout) => true;

    /// <summary>Decoded text; throws RuntimeFailure SCRIPTS-RUNTIME-0013 when it exceeds 2^28 UTF-16 units.</summary>
    public string ReadText()
    {
        lock (gate)
        {
            var text = NativeText.DecodeLenient(storage.GetBuffer().AsSpan(0, (int)storage.Length));
            return text.Length > SystemLimits.MaximumUnits ? throw SystemLimits.TextTooLong(text.Length) : text;
        }
    }
}

/// <summary>Text captured by a context created with CreateForCapture (what --json-out reports as stdout/stderr).</summary>
public sealed class CapturedOutput
{
    private readonly MemoryOutputChannel standardOutput;
    private readonly MemoryOutputChannel standardError;

    internal CapturedOutput(MemoryOutputChannel standardOutput, MemoryOutputChannel standardError)
    {
        this.standardOutput = standardOutput;
        this.standardError = standardError;
    }

    public string ReadStandardOutput() => standardOutput.ReadText();

    public string ReadStandardError() => standardError.ReadText();
}
