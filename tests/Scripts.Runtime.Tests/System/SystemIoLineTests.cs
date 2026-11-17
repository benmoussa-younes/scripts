using Scripts.Runtime.Platform;
using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>
/// <c>io.read_line</c> and <c>io.write</c> (SPEC-STDLIB §7.10): line framing (LF/CRLF/lone CR/partial line/EOF),
/// strict UTF-8, NUL preservation, and the single buffered stream shared with <c>io.read_standard_input</c>.
/// The real file descriptor 0 integration is covered by EndToEnd tests; here the run's reader is fed in memory.
/// </summary>
[Collection(SystemTestCollection.Name)]
public sealed class SystemIoLineTests
{
    [Fact]
    public void Read_line_returns_lines_and_strips_the_terminator()
    {
        using var context = ReaderContext("one\ntwo\n");

        Assert.Equal("one", ReadLine(context));
        Assert.Equal("two", ReadLine(context));
        Assert.Equal("end_of_file", ReadKind(context));
    }

    [Fact]
    public void Read_line_keeps_empty_lines()
    {
        using var context = ReaderContext("\n\n");

        Assert.Equal(string.Empty, ReadLine(context));
        Assert.Equal(string.Empty, ReadLine(context));
        Assert.Equal("end_of_file", ReadKind(context));
    }

    [Fact]
    public void Read_line_strips_crlf_but_keeps_a_lone_cr()
    {
        using var context = ReaderContext("a\r\nb\rc\n");

        Assert.Equal("a", ReadLine(context));
        Assert.Equal("b\rc", ReadLine(context));
        Assert.Equal("end_of_file", ReadKind(context));
    }

    [Fact]
    public void Read_line_returns_a_final_partial_line_then_end_of_file()
    {
        using var context = ReaderContext("tail");

        Assert.Equal("tail", ReadLine(context));
        Assert.Equal("end_of_file", ReadKind(context));
    }

    [Fact]
    public void Read_line_keeps_a_trailing_cr_on_a_final_partial_line()
    {
        using var context = ReaderContext("tail\r");

        Assert.Equal("tail\r", ReadLine(context));
        Assert.Equal("end_of_file", ReadKind(context));
    }

    [Fact]
    public void Read_line_on_empty_input_is_end_of_file()
    {
        using var context = ReaderContext(string.Empty);

        Assert.Equal("end_of_file", ReadKind(context));
    }

    [Fact]
    public void Read_line_spans_reader_chunks()
    {
        var line = new string('x', 5000);
        using var context = ReaderContext(line + "\n");

        Assert.Equal(line, ReadLine(context));
        Assert.Equal("end_of_file", ReadKind(context));
    }

    [Fact]
    public void Read_line_is_strict_utf8_and_consumes_the_bad_line()
    {
        using var context = ReaderContext(new byte[] { 0x61, 0xFF, 0x0A });

        Assert.Equal("invalid_encoding", ReadKind(context));
        Assert.Equal("end_of_file", ReadKind(context));
    }

    [Fact]
    public void Read_line_keeps_nul_bytes()
    {
        using var context = ReaderContext(new byte[] { 0x61, 0x00, 0x62, 0x0A });

        Assert.Equal("a\0b", ReadLine(context));
    }

    [Fact]
    public void Read_line_then_read_standard_input_returns_the_buffered_remainder()
    {
        using var context = ReaderContext("first\nrest\n");

        Assert.Equal("first", ReadLine(context));
        Assert.Equal("rest\n", ReadStandardInput(context));
        Assert.Equal("end_of_file", ReadKind(context));
    }

    [Fact]
    public void Read_standard_input_then_read_line_is_end_of_file()
    {
        using var context = ReaderContext("all\ntext");

        Assert.Equal("all\ntext", ReadStandardInput(context));
        Assert.Equal("end_of_file", ReadKind(context));
    }

    /// <summary>
    /// Two or more callers may race on the shared reader (for example <c>parallel.transform(..., n => io.read_standard_input_line())</c>):
    /// the reads are serialized, so every line is returned exactly once and exactly one caller sees <c>end_of_file</c>.
    /// Which caller gets which line remains scheduler-dependent and is not asserted.
    /// </summary>
    [Fact]
    public void Concurrent_read_line_calls_lose_no_bytes_and_see_one_end_of_file()
    {
        var expected = Enumerable.Range(0, 50).Select(index => $"L{index}").ToArray();
        using var context = ReaderContext(string.Join("\n", expected) + "\n", chunkBytes: 7);
        var lines = new System.Collections.Concurrent.ConcurrentBag<string>();
        var kinds = new System.Collections.Concurrent.ConcurrentBag<string>();

        System.Threading.Tasks.Parallel.For(0, expected.Length + 1, _ =>
        {
            var result = Assert.IsType<ResultValue>(Call(context, "io.read_standard_input_line"));
            if (result.IsOk)
            {
                lines.Add(Assert.IsType<StringValue>(result.Payload).Text);
            }
            else
            {
                kinds.Add(Assert.IsType<RecordValue>(result.Payload).GetText("kind"));
            }
        });

        Assert.Equal(expected.Order(), lines.Order());
        Assert.Equal("end_of_file", Assert.Single(kinds));
    }

    [Fact]
    public void Write_writes_without_a_newline_keeps_nul_and_orders_with_io_write_standard_output_line()
    {
        using var context = NewContext(out var captured);

        Assert.IsType<UnitValue>(Call(context, "io.write_standard_output", Text("a")));
        Assert.IsType<UnitValue>(Call(context, "io.write_standard_output", Text(string.Empty)));
        Assert.IsType<UnitValue>(Call(context, "io.write_standard_output", Text("\0")));
        Assert.IsType<UnitValue>(Call(context, "io.write_standard_output", Text("b\n")));
        Call(context, "io.write_standard_output_line", Text("c"));

        Assert.Equal("a\0b\nc\n", captured.ReadStandardOutput());
    }

    private static RuntimeContext ReaderContext(string input, int chunkBytes = int.MaxValue) =>
        ReaderContext(System.Text.Encoding.UTF8.GetBytes(input), chunkBytes);

    private static RuntimeContext ReaderContext(byte[] input, int chunkBytes = int.MaxValue)
    {
        var context = NewContext();
        var offset = 0;
        context.StandardInput = new StandardInputReader((buffer, bufferOffset, count) =>
        {
            var available = input.Length - offset;
            if (available <= 0)
            {
                return 0;
            }

            var take = Math.Min(Math.Min(count, chunkBytes), available);
            input.AsSpan(offset, take).CopyTo(buffer.AsSpan(bufferOffset, take));
            offset += take;
            return take;
        });
        return context;
    }

    private static string ReadLine(RuntimeContext context) =>
        Ok<StringValue>(Call(context, "io.read_standard_input_line")).Text;

    private static string ReadKind(RuntimeContext context) =>
        ErrorRecord(Call(context, "io.read_standard_input_line"), "error").GetText("kind");

    private static string ReadStandardInput(RuntimeContext context) =>
        Ok<StringValue>(Call(context, "io.read_standard_input")).Text;
}
