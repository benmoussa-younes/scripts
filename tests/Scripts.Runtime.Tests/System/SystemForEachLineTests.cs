using System.Net.Sockets;
using Scripts.Core;
using Scripts.Runtime.Platform;
using Scripts.Runtime.Signals;

namespace Scripts.Runtime.Tests.SystemFunctions;

[Collection(SystemTestCollection.Name)]
public sealed class SystemForEachLineTests
{
    private sealed class VisitorFunction(Func<string, Value> visit) : FunctionValue
    {
        public override int Arity => 1;

        public Value Invoke(string line) => visit(line);
    }

    /// <summary>Lazy stream of identical short lines; the content is never materialized as a whole.</summary>
    private sealed class GeneratedLinesStream(long lineCount, int lineLength, bool crlf) : Stream
    {
        private long lineIndex;
        private int position;

        public long BytesProduced { get; private set; }

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
            var written = 0;
            var lineBytes = lineLength + (crlf ? 2 : 1);
            while (written < count && lineIndex < lineCount)
            {
                if (position >= lineBytes)
                {
                    lineIndex++;
                    position = 0;
                    continue;
                }

                buffer[offset + written] = position < lineLength
                    ? (byte)'x'
                    : crlf && position == lineLength
                        ? (byte)'\r'
                        : (byte)'\n';
                written++;
                position++;
                BytesProduced++;
            }

            return written;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static int OpenDescriptorCount() => Directory.GetFiles("/proc/self/fd").Length;

    private static Value Ok() => ResultValue.Ok(UnitValue.Instance);

    private static Value CallForEachLine(RuntimeContext context, string filePath, Func<string, Value> visit)
    {
        var function = new VisitorFunction(visit);
        context.FunctionInvoker = (value, arguments) => ((VisitorFunction)value).Invoke(((StringValue)arguments[0]).Text);
        return SystemCalls.Call(context, "filesystem.for_each_line", SystemCalls.PathOf(filePath), function);
    }

    [Fact]
    public void Splits_lines_like_string_lines()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Write("lines.txt", "alpha\nbeta\r\ngamma\rlast-no-newline");
        var lines = new List<string>();

        var result = CallForEachLine(context, file, line =>
        {
            lines.Add(line);
            return Ok();
        });

        SystemCalls.OkUnit(result);
        Assert.Equal(["alpha", "beta", "gamma\rlast-no-newline"], lines);
    }

    [Fact]
    public void An_empty_file_calls_no_visitor()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Write("empty.txt", string.Empty);
        var calls = 0;

        SystemCalls.OkUnit(CallForEachLine(context, file, _ =>
        {
            calls++;
            return Ok();
        }));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Trailing_and_final_line_rules_match_string_lines()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var cases = new (string Content, string[] Expected)[]
        {
            ("a\n", ["a"]),
            ("a\n\n", ["a", ""]),
            ("a\r\n", ["a"]),
            ("a\r", ["a\r"]),
            ("a\nb", ["a", "b"]),
        };

        for (var index = 0; index < cases.Length; index++)
        {
            var (content, expected) = cases[index];
            var file = workspace.Write($"edge-{index}.txt", content);
            var lines = new List<string>();

            SystemCalls.OkUnit(CallForEachLine(context, file, line =>
            {
                lines.Add(line);
                return Ok();
            }));

            Assert.Equal(expected, lines.ToArray());
        }
    }

    [Fact]
    public void Multi_byte_utf8_lines_are_preserved()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Write("utf8.txt", "café 😀\nnaïve\n");
        var lines = new List<string>();

        SystemCalls.OkUnit(CallForEachLine(context, file, line =>
        {
            lines.Add(line);
            return Ok();
        }));

        Assert.Equal(["café 😀", "naïve"], lines);
        Assert.Equal(7, lines[0].Length);
        Assert.Equal(5, lines[1].Length);
    }

    [Fact]
    public void A_two_byte_scalar_across_the_read_buffer_is_not_split()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Combine("split-utf8.txt");
        var bytes = new byte[65538];
        Array.Fill(bytes, (byte)'a', 0, 65535);
        bytes[65535] = 0xC3;
        bytes[65536] = 0xA9;
        bytes[65537] = (byte)'\n';
        File.WriteAllBytes(file, bytes);
        var lines = new List<string>();

        SystemCalls.OkUnit(CallForEachLine(context, file, line =>
        {
            lines.Add(line);
            return Ok();
        }));

        var line = Assert.Single(lines);
        Assert.Equal(new string('a', 65535) + "é", line);
    }

    [Fact]
    public void Crlf_at_the_read_buffer_boundary_drops_the_cr()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Combine("split-crlf.txt");
        var bytes = new byte[65537];
        Array.Fill(bytes, (byte)'a', 0, 65535);
        bytes[65535] = (byte)'\r';
        bytes[65536] = (byte)'\n';
        File.WriteAllBytes(file, bytes);
        var lines = new List<string>();

        SystemCalls.OkUnit(CallForEachLine(context, file, line =>
        {
            lines.Add(line);
            return Ok();
        }));

        Assert.Equal([new string('a', 65535)], lines);
    }

    [Fact]
    public void Bom_and_nul_inside_a_line_are_preserved()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Combine("bom-nul.bin");
        File.WriteAllBytes(file, [0xEF, 0xBB, 0xBF, (byte)'a', 0x00, (byte)'b', (byte)'\n']);
        var lines = new List<string>();

        SystemCalls.OkUnit(CallForEachLine(context, file, line =>
        {
            lines.Add(line);
            return Ok();
        }));

        Assert.Equal(["\uFEFFa\0b"], lines);
    }

    [Fact]
    public void Invalid_utf8_is_reported_as_invalid_encoding()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Combine("invalid.bin");
        File.WriteAllBytes(file, [(byte)'a', 0xFF, (byte)'\n']);

        SystemCalls.AssertIoError(CallForEachLine(context, file, _ => Ok()), "invalid_encoding", file);
    }

    [Fact]
    public void A_line_over_the_cap_fails_with_too_large_without_visiting_it()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Combine("too-large-line.bin");
        const long cap = SystemLimits.MaximumUnits;
        using (var stream = new FileStream(file, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(cap + 1);
        }

        var calls = 0;
        var result = CallForEachLine(context, file, _ =>
        {
            calls++;
            return Ok();
        });

        SystemCalls.AssertIoError(result, "too_large", file);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void File_errors_match_read_file()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Write("file.txt", "x");

        SystemCalls.AssertIoError(
            CallForEachLine(context, workspace.Combine("missing.txt"), _ => Ok()),
            "not_found",
            workspace.Combine("missing.txt"));
        SystemCalls.AssertIoError(CallForEachLine(context, workspace.Root, _ => Ok()), "is_a_directory", workspace.Root);
        SystemCalls.AssertIoError(CallForEachLine(context, file + "/x", _ => Ok()), "not_a_directory", file + "/x");
        SystemCalls.AssertIoError(CallForEachLine(context, "a\0", _ => Ok()), "invalid_path");
    }

    [Fact]
    public void A_fifo_or_device_is_other_without_reading()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var fifo = workspace.Combine("pipe");
        SystemRawFiles.MakeFifo(fifo);

        SystemCalls.AssertIoError(CallForEachLine(context, fifo, _ => Ok()), "other", fifo);
        SystemCalls.AssertIoError(CallForEachLine(context, "/dev/null", _ => Ok()), "other", "/dev/null");
    }

    [Fact]
    public void A_visitor_error_stops_the_read_and_is_returned()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Write("stop.txt", "a\nb\nc");
        var calls = 0;

        var result = CallForEachLine(context, file, _ =>
        {
            calls++;
            return ResultValue.Error(ErrorValues.Error("other", "", "stop"));
        });

        var error = SystemCalls.ErrorRecord(result, "error");
        Assert.Equal("other", error.GetText("kind"));
        Assert.Equal("stop", error.GetText("message"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void An_early_visitor_error_on_a_large_file_returns_after_one_call()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Combine("early-stop.txt");
        const int lineLength = 64;
        const int blockLength = 64 * 1024;
        using (var stream = new FileStream(file, FileMode.Create, FileAccess.Write))
        {
            var line = new byte[lineLength];
            Array.Fill(line, (byte)'x');
            line[^1] = (byte)'\n';
            var block = new byte[blockLength];
            for (var offset = 0; offset < block.Length; offset += line.Length)
            {
                line.CopyTo(block, offset);
            }

            for (var index = 0; index < 8 * 1024 * 1024 / blockLength; index++)
            {
                stream.Write(block);
            }
        }

        var calls = 0;
        var result = CallForEachLine(context, file, _ =>
        {
            calls++;
            return ResultValue.Error(ErrorValues.Error("other", "", "stop"));
        });

        var error = SystemCalls.ErrorRecord(result, "error");
        Assert.Equal("other", error.GetText("kind"));
        Assert.Equal(1, calls);
        File.Delete(file);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void A_64_mib_file_is_streamed_line_by_line()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Combine("large.txt");
        const int lineLength = 64 * 1024;
        const int lineCount = 64 * 1024 * 1024 / lineLength;
        using (var stream = new FileStream(file, FileMode.Create, FileAccess.Write))
        {
            var line = new byte[lineLength];
            Array.Fill(line, (byte)'x');
            line[^1] = (byte)'\n';
            for (var index = 0; index < lineCount; index++)
            {
                stream.Write(line);
            }
        }

        long visited = 0;
        var result = CallForEachLine(context, file, line =>
        {
            Assert.Equal(lineLength - 1, line.Length);
            visited++;
            return Ok();
        });

        SystemCalls.OkUnit(result);
        Assert.Equal(lineCount, visited);
    }

    [Fact]
    public void Sigint_between_lines_stops_the_read()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Write("signal.txt", "a\nb\nc");
        var calls = 0;

        var interruption = Assert.Throws<InterruptionTermination>(() => CallForEachLine(context, file, _ =>
        {
            calls++;
            context.Interruption.Raise(InterruptionMonitor.InterruptSignalName, InterruptionMonitor.InterruptExitCode);
            return Ok();
        }));

        Assert.Equal(InterruptionMonitor.InterruptExitCode, interruption.ExitCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_generated_stream_over_256_mib_is_processed_lazily()
    {
        const int lineLength = 16 * 1024;
        const long lineCount = (SystemLimits.MaximumUnits / lineLength) + 1;
        using var source = new GeneratedLinesStream(lineCount, lineLength, crlf: false);
        long visits = 0;
        long contentBytes = 0;
        long firstVisitBytes = -1;

        LineStream.ForEachLine(source, "generated", line =>
        {
            if (visits == 0)
            {
                firstVisitBytes = source.BytesProduced;
            }

            Assert.Equal(lineLength, line.Length);
            visits++;
            contentBytes += line.Length;
            return true;
        }, () => { });

        Assert.Equal(lineCount, visits);
        Assert.True(firstVisitBytes <= 64 * 1024, $"the first visit happened after {firstVisitBytes} bytes");
        Assert.True(contentBytes > SystemLimits.MaximumUnits, $"visited {contentBytes} bytes");
    }

    [Fact]
    public void An_exact_cap_lf_line_is_accepted()
    {
        using var source = new GeneratedLinesStream(1, SystemLimits.MaximumUnits, crlf: false);
        var visits = 0;

        LineStream.ForEachLine(source, "generated", line =>
        {
            Assert.Equal(SystemLimits.MaximumUnits, line.Length);
            visits++;
            return true;
        }, () => { });

        Assert.Equal(1, visits);
    }

    [Fact]
    public void An_exact_cap_crlf_line_is_accepted()
    {
        using var source = new GeneratedLinesStream(1, SystemLimits.MaximumUnits, crlf: true);
        var visits = 0;

        LineStream.ForEachLine(source, "generated", line =>
        {
            Assert.Equal(SystemLimits.MaximumUnits, line.Length);
            visits++;
            return true;
        }, () => { });

        Assert.Equal(1, visits);
    }

    [Fact]
    public void The_descriptor_is_closed_after_an_early_visitor_error()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Write("close.txt", "a\nb\nc");
        var before = OpenDescriptorCount();

        var result = CallForEachLine(context, file, _ =>
            ResultValue.Error(ErrorValues.Error("other", "", "stop")));

        var after = OpenDescriptorCount();
        SystemCalls.ErrorRecord(result, "error");
        Assert.Equal(before, after);
    }

    [Fact]
    public void Symlinks_are_followed_and_checked_on_the_opened_descriptor()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Write("target.txt", "a\nb");
        var fileLink = workspace.Combine("file-link");
        File.CreateSymbolicLink(fileLink, file);
        var lines = new List<string>();
        SystemCalls.OkUnit(CallForEachLine(context, fileLink, line =>
        {
            lines.Add(line);
            return Ok();
        }));
        Assert.Equal(["a", "b"], lines);

        var directory = workspace.Combine("directory");
        Directory.CreateDirectory(directory);
        var directoryLink = workspace.Combine("directory-link");
        File.CreateSymbolicLink(directoryLink, directory);
        SystemCalls.AssertIoError(CallForEachLine(context, directoryLink, _ => Ok()), "is_a_directory", directoryLink);

        var fifo = workspace.Combine("fifo");
        SystemRawFiles.MakeFifo(fifo);
        var fifoLink = workspace.Combine("fifo-link");
        File.CreateSymbolicLink(fifoLink, fifo);
        SystemCalls.AssertIoError(CallForEachLine(context, fifoLink, _ => Ok()), "other", fifoLink);

        var dangling = workspace.Combine("dangling");
        File.CreateSymbolicLink(dangling, workspace.Combine("missing"));
        SystemCalls.AssertIoError(CallForEachLine(context, dangling, _ => Ok()), "not_found", dangling);
    }

    [Fact]
    public void An_unreadable_file_is_permission_denied()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Write("unreadable.txt", "x");
        File.SetUnixFileMode(file, UnixFileMode.None);
        try
        {
            SystemCalls.AssertIoError(CallForEachLine(context, file, _ => Ok()), "permission_denied", file);
        }
        finally
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void A_visitor_error_on_the_final_unterminated_line_is_returned()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Write("final.txt", "a\nb");
        var calls = 0;

        var result = CallForEachLine(context, file, _ =>
        {
            calls++;
            return calls == 2 ? ResultValue.Error(ErrorValues.Error("other", "", "stop")) : Ok();
        });

        var error = SystemCalls.ErrorRecord(result, "error");
        Assert.Equal("stop", error.GetText("message"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void A_fatal_failure_in_the_visitor_propagates()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Write("fatal.txt", "a\nb");

        var failure = Assert.Throws<RuntimeFailure>(() => CallForEachLine(context, file, _ =>
            throw new RuntimeFailure(ErrorCodes.Runtime0002, ("operation", "1 / 0"))));

        Assert.Equal(ErrorCodes.Runtime0002, failure.Code);
    }

    [Fact]
    public void A_visitor_truncating_the_file_during_the_read_does_not_crash()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Combine("mutate.txt");
        using (var stream = new FileStream(file, FileMode.Create, FileAccess.Write))
        {
            var line = new byte[64];
            Array.Fill(line, (byte)'x');
            line[^1] = (byte)'\n';
            for (var index = 0; index < 128 * 1024 / line.Length; index++)
            {
                stream.Write(line);
            }
        }

        var calls = 0;
        var result = CallForEachLine(context, file, _ =>
        {
            calls++;
            if (calls == 1)
            {
                File.WriteAllText(file, string.Empty);
            }

            return Ok();
        });

        SystemCalls.OkUnit(result);
        Assert.True(calls >= 1);
    }

    [Fact]
    public void For_each_line_is_reentrant()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var outer = workspace.Write("outer.txt", "a\nb");
        var inner = workspace.Write("inner.txt", "x\ny");
        var visited = new List<string>();

        var result = CallForEachLine(context, outer, line =>
        {
            visited.Add("outer:" + line);
            SystemCalls.OkUnit(CallForEachLine(context, inner, innerLine =>
            {
                visited.Add("inner:" + innerLine);
                return Ok();
            }));
            return Ok();
        });

        SystemCalls.OkUnit(result);
        Assert.Equal(["outer:a", "inner:x", "inner:y", "outer:b", "inner:x", "inner:y"], visited);
    }

    [Fact]
    public void An_unreadable_directory_is_still_is_a_directory()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var directory = workspace.Combine("no-read-directory");
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserExecute);
        try
        {
            SystemCalls.AssertIoError(CallForEachLine(context, directory, _ => Ok()), "is_a_directory", directory);
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void An_unreadable_fifo_is_other_without_reading()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var fifo = workspace.Combine("no-read-fifo");
        SystemRawFiles.MakeFifo(fifo);
        File.SetUnixFileMode(fifo, UnixFileMode.None);
        try
        {
            SystemCalls.AssertIoError(CallForEachLine(context, fifo, _ => Ok()), "other", fifo);
        }
        finally
        {
            File.SetUnixFileMode(fifo, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void A_socket_path_is_other_without_reading()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var socketPath = workspace.Combine("socket.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);

        SystemCalls.AssertIoError(CallForEachLine(context, socketPath, _ => Ok()), "other", socketPath);
    }

    [Fact]
    public void Descriptors_are_closed_on_success_and_rejection()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = SystemCalls.NewContext();
        var file = workspace.Write("close-ok.txt", "a\nb");

        var before = OpenDescriptorCount();
        SystemCalls.OkUnit(CallForEachLine(context, file, _ => Ok()));
        var afterSuccess = OpenDescriptorCount();
        Assert.Equal(before, afterSuccess);

        SystemCalls.AssertIoError(CallForEachLine(context, workspace.Root, _ => Ok()), "is_a_directory", workspace.Root);
        var afterReject = OpenDescriptorCount();
        Assert.Equal(afterSuccess, afterReject);
    }

    [Fact]
    public void An_interruption_after_the_open_closes_the_descriptor()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("interrupted.txt", "a\nb");
        var calls = 0;
        // The fourth check runs in the post-open descriptor verification: the readable fd is open at that point.
        Action checkInterrupted = () =>
        {
            if (++calls == 4)
            {
                throw new InvalidOperationException("interrupted");
            }
        };

        var before = OpenDescriptorCount();
        Assert.Throws<InvalidOperationException>(() => LineStream.ForEachLine(file, _ => true, checkInterrupted));
        Assert.Equal(4, calls);
        Assert.Equal(before, OpenDescriptorCount());
    }

    [Fact]
    public void The_descriptor_type_is_checked_after_the_open()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("target.txt", "a\nb");
        var descriptor = Libc.Open(
            NativeFileSystem.ToNativePath(file),
            Libc.OpenReadOnly | Libc.OpenNonBlock | Libc.OpenCloseOnExec,
            0);
        Assert.True(descriptor >= 0);
        try
        {
            // Replace the path with a FIFO after the regular file was opened: the opened object must still be read.
            var replacement = workspace.Combine("replacement");
            SystemRawFiles.MakeFifo(replacement);
            File.Move(replacement, file, overwrite: true);

            var lines = new List<string>();
            LineStream.ForEachLine(descriptor, file, line =>
            {
                lines.Add(line);
                return true;
            }, () => { });
            Assert.Equal(["a", "b"], lines);
        }
        finally
        {
            Libc.Close(descriptor);
        }

        var fifoDescriptor = Libc.Open(
            NativeFileSystem.ToNativePath(file),
            Libc.OpenReadOnly | Libc.OpenNonBlock | Libc.OpenCloseOnExec,
            0);
        Assert.True(fifoDescriptor >= 0);
        try
        {
            var failure = Assert.Throws<IoFailureException>(() =>
                LineStream.ForEachLine(fifoDescriptor, file, _ => true, () => { }));
            Assert.Equal(IoErrorKinds.Other, failure.Kind);
        }
        finally
        {
            Libc.Close(fifoDescriptor);
        }
    }
}
