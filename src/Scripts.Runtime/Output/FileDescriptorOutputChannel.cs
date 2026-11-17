using System.Runtime.InteropServices;
using System.Text;
using Scripts.Runtime.Platform;

namespace Scripts.Runtime.Output;

/// <summary>
/// Buffered UTF-8 (no BOM) writer on a file descriptor. EPIPE is a silent termination for stdout
/// (BrokenPipeTermination) and any other write error is SCRIPTS-RUNTIME-0019. After a failure further output is discarded.
/// </summary>
public sealed class FileDescriptorOutputChannel : ITrackedOutputChannel
{
    private const int BufferCapacity = 64 * 1024;
    private const int EncodedChunkCharacters = 8 * 1024;
    private const int EncodedChunkMaximumBytes = (EncodedChunkCharacters * 3) + 8;

    private readonly object gate = new();
    private readonly int fileDescriptor;
    private readonly string streamName;
    private readonly bool brokenPipeEndsSilently;
    private readonly byte[] buffer = new byte[BufferCapacity];
    private readonly Encoder encoder = new UTF8Encoding(false).GetEncoder();
    private int bufferedByteCount;
    private volatile bool atLineStart = true;
    private bool failed;

    public FileDescriptorOutputChannel(int fileDescriptor, string streamName, bool brokenPipeEndsSilently)
    {
        this.fileDescriptor = fileDescriptor;
        this.streamName = streamName;
        this.brokenPipeEndsSilently = brokenPipeEndsSilently;
    }

    public bool IsAtLineStart => atLineStart;

    public static FileDescriptorOutputChannel ForStandardOutput() => new(1, "stdout", brokenPipeEndsSilently: true);

    public static FileDescriptorOutputChannel ForStandardError() => new(2, "stderr", brokenPipeEndsSilently: false);

    public void Write(string text)
    {
        lock (gate)
        {
            if (failed || text.Length == 0)
            {
                return;
            }

            atLineStart = text[^1] == '\n';
            for (var offset = 0; offset < text.Length; offset += EncodedChunkCharacters)
            {
                var chunkLength = Math.Min(EncodedChunkCharacters, text.Length - offset);
                if (BufferCapacity - bufferedByteCount < EncodedChunkMaximumBytes)
                {
                    FlushBuffered();
                }

                var isLastChunk = offset + chunkLength == text.Length;
                bufferedByteCount += encoder.GetBytes(text.AsSpan(offset, chunkLength), buffer.AsSpan(bufferedByteCount), isLastChunk);
            }
        }
    }

    public void Flush()
    {
        lock (gate)
        {
            FlushBuffered();
        }
    }

    public bool TryFlush(TimeSpan timeout)
    {
        if (!Monitor.TryEnter(gate, timeout))
        {
            return false;
        }

        try
        {
            FlushBuffered();
            return true;
        }
        catch (Exception exception) when (exception is RuntimeFailure or BrokenPipeTermination)
        {
            return false;
        }
        finally
        {
            Monitor.Exit(gate);
        }
    }

    private void FlushBuffered()
    {
        if (bufferedByteCount == 0)
        {
            return;
        }

        var pendingByteCount = bufferedByteCount;
        bufferedByteCount = 0;
        if (failed)
        {
            return;
        }

        var offset = 0;
        while (offset < pendingByteCount)
        {
            var bytesWritten = Libc.Write(fileDescriptor, buffer.AsSpan(offset, pendingByteCount - offset), (nuint)(pendingByteCount - offset));
            if (bytesWritten >= 0)
            {
                offset += (int)bytesWritten;
                continue;
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno == Errno.Interrupted)
            {
                continue;
            }

            if (errno == Errno.TryAgain)
            {
                Thread.Sleep(1);
                continue;
            }

            failed = true;
            if (errno == Errno.BrokenPipe && brokenPipeEndsSilently)
            {
                throw new BrokenPipeTermination();
            }

            throw new RuntimeFailure(
                "SCRIPTS-RUNTIME-0019",
                ("stream", streamName),
                ("reason", Marshal.GetPInvokeErrorMessage(errno)));
        }
    }
}
