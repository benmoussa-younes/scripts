using System.Runtime.InteropServices;

namespace Scripts.Runtime.Platform;

/// <summary>
/// Streams a regular file line by line (DESIGN §7.2): a bounded read buffer, strict UTF-8 per line, and the
/// same split as <c>string.lines()</c>/<c>io.read_standard_input_line</c>. The whole file is never materialized.
/// </summary>
internal static class LineStream
{
    private const int ReadChunkBytes = 64 * 1024;

    /// <summary>Per-line cap shared with <c>filesystem.read_file</c> (2^28 bytes).</summary>
    private const int MaximumLineBytes = SystemLimits.MaximumUnits;

    /// <summary>
    /// A CRLF line may carry one extra raw byte (the <c>\r</c>) before it is stripped: raw bytes are bounded by
    /// <see cref="MaximumLineBytes"/> + 1, and <see cref="DecodeLine"/> enforces the cap on the decoded bytes.
    /// </summary>
    private const int MaximumRawLineBytes = MaximumLineBytes + 1;

    /// <summary>
    /// After a large line, keep only this capacity for the next line instead of the largest line seen. The threshold is
    /// deliberately above one read chunk so an ordinary line does not reallocate.
    /// </summary>
    private const int RetainedLineCapacity = 1024 * 1024;

    /// <summary>
    /// Opens and validates the file, then streams it. An <c>O_PATH</c> pre-open classifies the target before any
    /// readable open (a directory/FIFO/device/socket is rejected without I/O side effects), then the readable open with
    /// <c>O_NONBLOCK|O_NOCTTY</c> is followed by a second <c>statx(fd, "", AT_EMPTY_PATH, …)</c>, so a swap between the
    /// two opens is caught. The descriptor is closed on every path. <paramref name="checkInterrupted"/> runs before
    /// opens, before statx and between lines/read chunks.
    /// </summary>
    public static void ForEachLine(string filePath, Func<string, bool> visit, Action checkInterrupted)
    {
        var opened = RegularFileOpener.OpenReadable(NativeFileSystem.ToNativePath(filePath), checkInterrupted);
        var fileDescriptor = Describe(opened, filePath);
        try
        {
            ForEachLine(fileDescriptor, filePath, visit, checkInterrupted);
        }
        finally
        {
            Libc.Close(fileDescriptor);
        }
    }

    /// <summary>Maps the shared opener's fault to the I/O error family (one classification, two callers).</summary>
    private static int Describe(RegularFileOpenResult opened, string filePath) =>
        opened.IsOpened ? opened.Descriptor : throw ToIoFailure(opened, filePath);

    private static IoFailureException ToIoFailure(RegularFileOpenResult opened, string filePath) =>
        opened.Fault switch
        {
            RegularFileFault.Directory => IoFailureException.FromErrno(Errno.IsADirectory, filePath),
            RegularFileFault.NotRegular => IoFailureException.NotARegularFile(filePath),
            _ => IoFailureException.FromErrno(opened.Errno, filePath),
        };

    /// <summary>
    /// Validates an already-open descriptor and streams it. Used by the path entry after opening, and by tests that
    /// prove the descriptor type is what is checked (a path replaced after the open does not change the opened object).
    /// The caller owns <paramref name="fileDescriptor"/>.
    /// </summary>
    internal static void ForEachLine(int fileDescriptor, string filePath, Func<string, bool> visit, Action checkInterrupted)
    {
        var verified = RegularFileOpener.VerifyDescriptor(fileDescriptor, checkInterrupted);
        if (!verified.IsOpened)
        {
            throw ToIoFailure(verified, filePath);
        }

        ForEachLine(new DescriptorStream(fileDescriptor, filePath, checkInterrupted), filePath, visit, checkInterrupted);
    }

    /// <summary>
    /// Internal seam shared by the descriptor path and the streaming proof: reads from any <see cref="Stream"/>.
    /// It never buffers more than one line plus the 64 KiB read chunk.
    /// </summary>
    internal static void ForEachLine(Stream source, string filePath, Func<string, bool> visit, Action checkInterrupted)
    {
        var buffer = new byte[ReadChunkBytes];
        // The MemoryStream retains the largest line's capacity until the end of the read; after a large line the
        // capacity is trimmed back to RetainedLineCapacity, so a file of short lines stays bounded by the largest
        // line seen (at most the per-line cap).
        using var line = new MemoryStream();
        while (true)
        {
            checkInterrupted();
            var bytesRead = source.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
            {
                if (line.Length > 0)
                {
                    checkInterrupted();
                    visit(DecodeLine(line, terminated: false, filePath));
                }

                return;
            }

            var remaining = buffer.AsSpan(0, bytesRead);
            while (remaining.Length > 0)
            {
                var newline = remaining.IndexOf((byte)'\n');
                if (newline < 0)
                {
                    AppendLine(line, remaining, filePath);
                    break;
                }

                AppendLine(line, remaining[..newline], filePath);
                checkInterrupted();
                if (!visit(DecodeLine(line, terminated: true, filePath)))
                {
                    return;
                }

                line.SetLength(0);
                if (line.Capacity > RetainedLineCapacity)
                {
                    line.Capacity = RetainedLineCapacity;
                }

                remaining = remaining[(newline + 1)..];
            }
        }
    }

    private static void AppendLine(MemoryStream line, ReadOnlySpan<byte> bytes, string filePath)
    {
        if (line.Length + bytes.Length > MaximumRawLineBytes)
        {
            throw new IoFailureException(IoErrorKinds.TooLarge, filePath, $"line too large (maximum {MaximumLineBytes} bytes)");
        }

        line.Write(bytes);
    }

    private static string DecodeLine(MemoryStream line, bool terminated, string filePath)
    {
        var bytes = line.GetBuffer().AsSpan(0, (int)line.Length);

        // Same CRLF rule as string.lines(): only a \r immediately before the \n is removed.
        if (terminated && bytes.Length > 0 && bytes[^1] == (byte)'\r')
        {
            bytes = bytes[..^1];
        }

        if (bytes.Length > MaximumLineBytes)
        {
            throw new IoFailureException(IoErrorKinds.TooLarge, filePath, $"line too large (maximum {MaximumLineBytes} bytes)");
        }

        return NativeText.TryDecodeStrict(bytes, out var text)
            ? text
            : throw new IoFailureException(IoErrorKinds.InvalidEncoding, filePath, "the content is not valid UTF-8 text");
    }

    /// <summary>Read-only <see cref="Stream"/> over an owned descriptor; it never closes the descriptor.</summary>
    private sealed class DescriptorStream(int fileDescriptor, string filePath, Action checkInterrupted) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (true)
            {
                var bytesRead = Libc.Read(fileDescriptor, buffer.AsSpan(offset, count), (nuint)count);
                if (bytesRead >= 0)
                {
                    return (int)bytesRead;
                }

                var errno = Marshal.GetLastPInvokeError();
                if (errno == Errno.Interrupted)
                {
                    // A signal may have interrupted the read: observe it here instead of retrying blindly.
                    checkInterrupted();
                    continue;
                }

                throw IoFailureException.FromErrno(errno, filePath);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
