using System.Runtime.InteropServices;
using System.Text;
using Scripts.Runtime.Output;
using Scripts.Runtime.Platform;

namespace Scripts.Runtime.Processes;

internal interface IByteSink
{
    /// <summary>Consumes one chunk; false tells the pump to stop reading and close its end of the stream.</summary>
    bool Append(ReadOnlySpan<byte> bytes);

    /// <summary>Called once after the last Append.</summary>
    void Complete();
}

/// <summary>Accumulates raw bytes; more than 3 * 2^28 bytes necessarily decode to more than 2^28 UTF-16 units.</summary>
internal sealed class BufferingSink(Action onOverflow) : IByteSink
{
    private const long MaximumBytes = 3L * SystemLimits.MaximumUnits;

    private readonly MemoryStream storage = new();
    private bool overflowed;

    public bool Append(ReadOnlySpan<byte> bytes)
    {
        if (overflowed)
        {
            return false;
        }

        if (storage.Length + bytes.Length > MaximumBytes)
        {
            overflowed = true;
            onOverflow();
            return false;
        }

        storage.Write(bytes);
        return true;
    }

    public void Complete()
    {
    }

    /// <summary>Strict UTF-8 without BOM detection; invalid bytes are `invalid_encoding` (DESIGN §6). Fatal SCRIPTS-RUNTIME-0013 beyond 2^28 units.</summary>
    public string ReadText(string program)
    {
        if (overflowed)
        {
            throw SystemLimits.TextTooLong(SystemLimits.MaximumUnits + 1L);
        }

        if (!NativeText.TryDecodeStrict(storage.GetBuffer().AsSpan(0, (int)storage.Length), out var text))
        {
            throw ProcessFailureException.InvalidEncoding(program);
        }

        return text.Length > SystemLimits.MaximumUnits ? throw SystemLimits.TextTooLong(text.Length) : text;
    }
}

/// <summary>Decodes child bytes (lenient UTF-8, stateful across chunks) and forwards the text to an output channel.</summary>
internal sealed class ChannelForwardingSink(IOutputChannel channel) : IByteSink
{
    private readonly Decoder decoder = new UTF8Encoding(false).GetDecoder();

    public bool Append(ReadOnlySpan<byte> bytes)
    {
        Forward(bytes, flush: false);
        return true;
    }

    public void Complete() => Forward([], flush: true);

    private void Forward(ReadOnlySpan<byte> bytes, bool flush)
    {
        var characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length) + 2];
        var characterCount = decoder.GetChars(bytes, characters, flush);
        if (characterCount > 0)
        {
            channel.Write(new string(characters, 0, characterCount));
        }
    }
}

/// <summary>
/// Dedicated thread draining a child stream into a sink until EOF. The read is a <c>poll(2)</c> on the pipe with a
/// short timeout and a stop flag, so a detached writer that keeps the write end open cannot pin a blocked thread, and
/// an abort releases the read end (the writer then gets EPIPE) instead of closing a descriptor under a blocked
/// <c>read(2)</c> (P8d companion lot).
/// </summary>
internal sealed class OutputPump
{
    private const int ChunkBytes = 16 * 1024;
    private const int PollTimeoutMilliseconds = 100;

    private readonly Stream stream;
    private readonly IByteSink sink;
    private readonly Thread thread;
    private readonly ManualResetEventSlim completed = new(false);
    private readonly int fileDescriptor;
    private volatile bool stopRequested;

    public OutputPump(Stream stream, IByteSink sink, string threadName)
    {
        this.stream = stream;
        this.sink = sink;
        fileDescriptor = TryGetFileDescriptor(stream);
        thread = new Thread(Run) { IsBackground = true, Name = threadName };
    }

    /// <summary>
    /// Set once the pump has reached EOF. Never disposed: another thread may still be waiting on it, and disposing an
    /// awaited event loses the wake-up (R0). Without a lazy WaitHandle the event holds no OS resource.
    /// </summary>
    public ManualResetEventSlim Completed => completed;

    /// <summary>RuntimeFailure or BrokenPipeTermination raised by the sink while pumping.</summary>
    public Exception? Failure { get; private set; }

    public void Start() => thread.Start();

    /// <summary>Asks the pump to stop at its next poll tick; the thread then completes and the observer releases the pipes.</summary>
    public void Stop() => stopRequested = true;

    private void Run()
    {
        try
        {
            var chunk = new byte[ChunkBytes];
            while (!stopRequested)
            {
                if (!WaitForData())
                {
                    break;
                }

                var byteCount = ReadChunk(chunk);
                if (byteCount == 0)
                {
                    break;
                }

                if (byteCount < 0)
                {
                    continue; // EINTR; the loop re-checks stopRequested and polls again
                }

                if (!Append(chunk.AsSpan(0, byteCount)))
                {
                    break;
                }
            }

            Complete();
        }
        finally
        {
            completed.Set();
        }
    }

    /// <summary>False on EOF/error/invalid descriptor; true when bytes may be read.</summary>
    private bool WaitForData()
    {
        if (fileDescriptor < 0)
        {
            return true; // not a pipe: a blocking Stream.Read below (tests, in-memory streams)
        }

        while (!stopRequested)
        {
            Span<PollFileDescriptor> descriptors = stackalloc PollFileDescriptor[1];
            descriptors[0] = new PollFileDescriptor
            {
                FileDescriptor = fileDescriptor,
                Events = Libc.PollInput,
            };
            var ready = Libc.Poll(descriptors, 1, PollTimeoutMilliseconds);
            if (ready == 0)
            {
                continue;
            }

            if (ready < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == Errno.Interrupted)
                {
                    continue; // a caught signal interrupted poll; the data is still there
                }

                return false;
            }

            if ((descriptors[0].ReturnedEvents & Libc.PollInvalid) != 0)
            {
                return false;
            }

            return true;
        }

        return false;
    }

    private int ReadChunk(byte[] chunk)
    {
        if (fileDescriptor >= 0)
        {
            var bytesRead = Libc.Read(fileDescriptor, chunk, (nuint)chunk.Length);
            if (bytesRead >= 0)
            {
                return (int)bytesRead;
            }

            var errno = Marshal.GetLastPInvokeError();
            return errno == Errno.Interrupted ? -1 : 0;
        }

        try
        {
            return stream.Read(chunk, 0, chunk.Length);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return 0;
        }
    }

    private bool Append(ReadOnlySpan<byte> bytes)
    {
        if (Failure is not null)
        {
            return false;
        }

        try
        {
            return sink.Append(bytes);
        }
        catch (Exception exception) when (exception is RuntimeFailure or BrokenPipeTermination)
        {
            Failure = exception;
            return false;
        }
    }

    private void Complete()
    {
        if (Failure is not null)
        {
            return;
        }

        try
        {
            sink.Complete();
        }
        catch (Exception exception) when (exception is RuntimeFailure or BrokenPipeTermination)
        {
            Failure = exception;
        }
    }

    private static int TryGetFileDescriptor(Stream stream) => stream switch
    {
        FileStream file => (int)file.SafeFileHandle.DangerousGetHandle(),
        System.IO.Pipes.PipeStream pipe => (int)pipe.SafePipeHandle.DangerousGetHandle(),
        _ => -1,
    };
}
