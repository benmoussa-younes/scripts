using System.Runtime.InteropServices;
using Scripts.Runtime.Signals;

namespace Scripts.Runtime.Platform;

/// <summary>Reads raw bytes from the standard input of the script; 0 means end of stream.</summary>
internal delegate int StandardInputRead(byte[] buffer, int offset, int count);

/// <summary>
/// One buffered reader per run for file descriptor 0, shared by <c>io.read_standard_input</c> and
/// <c>io.read_line</c> (SPEC-STDLIB §7.10): each function sees exactly the bytes the other did not consume.
/// The reader is per <see cref="RuntimeContext"/> so a test context never inherits another context's buffer.
/// <para>
/// LIMITATION (documented): <see cref="ReadLine"/> reads ahead in chunks, so the descriptor position advances past
/// the rest of the prefetched bytes (they stay in the reader's pending buffer). A later <c>process.run_inherit</c> child
/// (which inherits file descriptor 0) therefore starts at the current descriptor position and does not see the
/// buffered remainder; <c>process.run_shell</c> closes stdin instead. The buffering is intentionally not changed for that.
/// </para>
/// <para>
/// The reader created by <see cref="RuntimeContext"/> polls descriptor 0 with a short timeout and checks the
/// interruption: <c>SIGINT</c> aborts a blocked <c>io.read_line</c>/<c>io.read_standard_input</c>; the
/// rolled back and the session continues; a script follows the normal signal path). The delegate constructor used by
/// tests keeps a plain blocking read.
/// </para>
/// </summary>
internal sealed class StandardInputReader
{
    private const int ChunkBytes = 4096;

    /// <summary>The 256 MiB cap shared with <c>filesystem.read_file</c> (2^28 bytes); the length is in bytes, not UTF-16 units.</summary>
    private const int MaximumBytes = SystemLimits.MaximumUnits;

    private readonly StandardInputRead read;
    private readonly InterruptionMonitor? interruption;

    // Serializes ReadLine/ReadToEnd so concurrent callers (for example `parallel.transform(..., n -> io.read_standard_input_line())`)
    // never observe or corrupt the shared pending buffer. The assignment of a line to a caller stays
    // scheduler-dependent: which worker gets which line is not deterministic, but no byte is lost or duplicated.
    private readonly object gate = new();

    // Bytes already read from the descriptor but not consumed by ReadLine yet (a line may stop before a chunk's end).
    private byte[] pending = [];
    private int pendingOffset;

    public StandardInputReader()
        : this(null, null)
    {
    }

    internal StandardInputReader(InterruptionMonitor interruption)
        : this(interruption, null)
    {
    }

    internal StandardInputReader(StandardInputRead read)
        : this(null, read)
    {
    }

    private StandardInputReader(InterruptionMonitor? interruption, StandardInputRead? read)
    {
        this.interruption = interruption;
        this.read = read ?? ReadFromDescriptor;
    }

    /// <summary>
    /// Reads the remaining standard input up to EOF (a previously buffered remainder first), decodes it in strict UTF-8
    /// and reports <c>invalid_encoding</c> / <c>too_large</c> like <c>filesystem.read_file</c>. A closed descriptor is an empty stream.
    /// </summary>
    public string ReadToEnd()
    {
        lock (gate)
        {
            return ReadToEndCore();
        }
    }

    private string ReadToEndCore()
    {
        var content = new MemoryStream();
        Append(content, pending.AsSpan(pendingOffset));
        pending = [];
        pendingOffset = 0;

        var chunk = new byte[ChunkBytes];
        while (true)
        {
            var bytesRead = read(chunk, 0, chunk.Length);
            if (bytesRead == 0)
            {
                break;
            }

            Append(content, chunk.AsSpan(0, bytesRead));
        }

        return Decode(content.GetBuffer().AsSpan(0, (int)content.Length));
    }

    /// <summary>
    /// Reads one line (terminator included, then removed) with the CRLF rule of SPEC-STDLIB §7.10: the returned text
    /// excludes the trailing <c>\n</c> and a <c>\r</c> immediately before it, so a lone <c>\r</c> is kept. A final
    /// partial line is returned once; the next call reports <c>end_of_file</c>.
    /// </summary>
    public string ReadLine()
    {
        lock (gate)
        {
            return ReadLineCore();
        }
    }

    private string ReadLineCore()
    {
        var line = new MemoryStream();
        while (true)
        {
            var remaining = pending.AsSpan(pendingOffset);
            var newline = remaining.IndexOf((byte)'\n');
            if (newline >= 0)
            {
                Append(line, remaining[..newline]);
                pendingOffset += newline + 1;
                return DecodeLine(line, terminated: true);
            }

            Append(line, remaining);
            pending = [];
            pendingOffset = 0;

            var chunk = new byte[ChunkBytes];
            var bytesRead = read(chunk, 0, chunk.Length);
            if (bytesRead == 0)
            {
                if (line.Length == 0)
                {
                    throw EndOfFile();
                }

                return DecodeLine(line, terminated: false);
            }

            pending = chunk[..bytesRead];
            pendingOffset = 0;
        }
    }

    private int ReadFromDescriptor(byte[] buffer, int offset, int count)
    {
        while (true)
        {
            if (interruption is not null)
            {
                // SIGINT must reach a chunk blocked on io.read_line/io.read_standard_input. poll(2) with a short
                // timeout lets the interruption be observed without a blocked read(2) and without closing the
                // descriptor under it (P8d companion lot / DESIGN §9).
                interruption.ThrowIfInterrupted();
                var ready = PollStandardInput();
                if (ready == PollInvalidDescriptor)
                {
                    // poll reports POLLNVAL at once for a closed descriptor; poll again would spin. It is EOF.
                    return 0;
                }

                if (ready == 0)
                {
                    continue;
                }

                if (ready < 0)
                {
                    var pollError = Marshal.GetLastPInvokeError();
                    if (pollError == Errno.Interrupted)
                    {
                        continue;
                    }

                    if (pollError == Errno.BadFileDescriptor)
                    {
                        return 0;
                    }

                    throw IoFailureException.FromErrno(pollError, string.Empty);
                }

                interruption.ThrowIfInterrupted();
            }

            var bytesRead = Libc.Read(Libc.StandardInput, buffer.AsSpan(offset, count), (nuint)count);
            if (bytesRead >= 0)
            {
                return (int)bytesRead;
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno == Errno.Interrupted)
            {
                continue;
            }

            // EBADF (a closed or absent standard input) means "no stream": EOF, never a failure (SPEC-STDLIB §7.10).
            if (errno == Errno.BadFileDescriptor)
            {
                return 0;
            }

            // A directory descriptor answers read(2) with EISDIR; standard input has no path, so this is a generic
            // read failure (`other`), not the filesystem `is_a_directory` kind (SPEC-STDLIB §7.10 kind sets).
            if (errno == Errno.IsADirectory)
            {
                throw new IoFailureException(IoErrorKinds.Other, string.Empty, "standard input is a directory");
            }

            throw IoFailureException.FromErrno(errno, string.Empty);
        }
    }

    /// <summary>poll(2) on stdin: 1 readable, 0 timeout or closed, -1 error (errno through the P/Invoke).</summary>
    private const int PollInvalidDescriptor = -2;

    private static int PollStandardInput()
    {
        Span<PollFileDescriptor> descriptors = stackalloc PollFileDescriptor[1];
        descriptors[0] = new PollFileDescriptor
        {
            FileDescriptor = Libc.StandardInput,
            Events = Libc.PollInput,
        };
        var ready = Libc.Poll(descriptors, 1, 100);
        if (ready > 0 && (descriptors[0].ReturnedEvents & Libc.PollInvalid) != 0)
        {
            return PollInvalidDescriptor;
        }

        return ready;
    }

    private static void Append(MemoryStream content, ReadOnlySpan<byte> bytes)
    {
        if (content.Length + bytes.Length > MaximumBytes)
        {
            throw TooLarge();
        }

        content.Write(bytes);
    }

    private static string DecodeLine(MemoryStream line, bool terminated)
    {
        var length = (int)line.Length;
        var buffer = line.GetBuffer();
        // The CRLF rule applies only to a line terminated by '\n'; a final partial line keeps a trailing '\r'.
        if (terminated && length > 0 && buffer[length - 1] == '\r')
        {
            length--;
        }

        return Decode(buffer.AsSpan(0, length));
    }

    private static string Decode(ReadOnlySpan<byte> bytes) =>
        NativeText.TryDecodeStrict(bytes, out var text)
            ? text
            : throw new IoFailureException(
                IoErrorKinds.InvalidEncoding,
                string.Empty,
                "the standard input is not valid UTF-8 text");

    private static IoFailureException TooLarge() =>
        new(IoErrorKinds.TooLarge, string.Empty, $"the standard input exceeds {MaximumBytes} bytes");

    private static IoFailureException EndOfFile() =>
        new(IoErrorKinds.EndOfFile, string.Empty, "end of standard input");
}
