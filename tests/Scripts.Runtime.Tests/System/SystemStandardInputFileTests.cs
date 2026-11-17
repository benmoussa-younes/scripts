using Scripts.Runtime;
using Scripts.Runtime.Platform;
using Scripts.Runtime.Processes;
using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>
/// <c>run_request.with_standard_input_from_file</c> and the launch-time wiring of the file to the child's fd 0
/// (DESIGN §7.3): the descriptor is validated on the opened file, never read by scripts, and closed on every path.
/// </summary>
[Collection(SystemTestCollection.Name)]
public sealed class SystemStandardInputFileTests
{
    [Fact]
    public void A_text_file_is_fed_byte_for_byte_and_an_empty_file_returns_nothing()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var file = workspace.Write("input.txt", "premiere ligne\ndeuxieme ligne\n");

        var outcome = Ok<RecordValue>(RunRequest(context, "cat", [], standardInputFile: file));
        Assert.Equal("premiere ligne\ndeuxieme ligne\n", outcome.GetText("standard_output"));
        Assert.Equal(0, outcome.GetInteger("exit_code"));

        var empty = workspace.Write("empty.txt", string.Empty);
        var emptyOutcome = Ok<RecordValue>(RunRequest(context, "cat", [], standardInputFile: empty));
        Assert.Equal(string.Empty, emptyOutcome.GetText("standard_output"));
    }

    [Fact]
    public void The_setters_clear_the_other_input_field_in_both_orders()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var file = workspace.Write("order.txt", "du fichier");

        var baseRequest = Assert.IsType<RecordValue>(Call(context, "process.request", Text("cat"), TextList()));
        var fileLast = Assert.IsType<RecordValue>(Call(
            context,
            "run_request.with_standard_input_from_file",
            Assert.IsType<RecordValue>(Call(context, "run_request.with_standard_input", baseRequest, Text("de la chaine"))),
            PathOf(file)));
        Assert.Equal(string.Empty, fileLast.GetText("standard_input"));
        Assert.Equal(file, ((PathValue)fileLast.Get("standard_input_file")).Text);
        Assert.Equal("du fichier", Ok<RecordValue>(Call(context, "process.run", fileLast)).GetText("standard_output"));

        var stringLast = Assert.IsType<RecordValue>(Call(
            context,
            "run_request.with_standard_input",
            Assert.IsType<RecordValue>(Call(context, "run_request.with_standard_input_from_file", baseRequest, PathOf(file))),
            Text("de la chaine")));
        Assert.Equal(string.Empty, ((PathValue)stringLast.Get("standard_input_file")).Text);
        Assert.Equal("de la chaine", stringLast.GetText("standard_input"));
        Assert.Equal("de la chaine", Ok<RecordValue>(Call(context, "process.run", stringLast)).GetText("standard_output"));
    }

    [Fact]
    public void A_missing_file_is_not_found_and_no_child_runs()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var marker = workspace.Combine("marker");

        var result = RunRequest(
            context,
            "touch",
            [marker],
            standardInputFile: workspace.Combine("absent.txt"));

        AssertProcessError(result, "not_found", "touch");
        Assert.False(File.Exists(marker), "the child ran although its input could not be prepared");
    }

    [Fact]
    public void A_directory_is_is_a_directory()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var directory = workspace.Combine("input-directory");
        Directory.CreateDirectory(directory);
        var marker = workspace.Combine("marker");

        var result = RunRequest(context, "touch", [marker], standardInputFile: directory);

        AssertProcessError(result, "is_a_directory", "touch");
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void An_unreadable_directory_is_still_is_a_directory()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var directory = workspace.Combine("no-read-directory");
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserExecute);
        try
        {
            AssertProcessError(RunRequest(context, "cat", [], standardInputFile: directory), "is_a_directory", "cat");
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void A_path_component_that_is_a_file_is_not_a_directory()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var file = workspace.Write("plain.txt", "x");

        AssertProcessError(
            RunRequest(context, "cat", [], standardInputFile: file + "/below"),
            "not_a_directory",
            "cat");
    }

    [Fact]
    public void A_relative_file_path_is_resolved_against_the_process_directory()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var subdirectory = workspace.Combine("sub");
        Directory.CreateDirectory(subdirectory);
        _ = workspace.Write("relative.txt", "depuis le repertoire courant");
        _ = workspace.Write(Path.Combine("sub", "only-sub.txt"), "depuis le sous-repertoire");

        WithWorkingDirectory(workspace.Root, () =>
        {
            var found = RequestWithStandardInputFileAndWorkingDirectory(context, "relative.txt", subdirectory);
            Assert.Equal(
                "depuis le repertoire courant",
                Ok<RecordValue>(Call(context, "process.run", found)).GetText("standard_output"));

            // A file that exists only in working_directory is not found: the path is not resolved there.
            var missing = RequestWithStandardInputFileAndWorkingDirectory(context, "only-sub.txt", subdirectory);
            AssertProcessError(Call(context, "process.run", missing), "not_found", "cat");
        });
    }

    [Fact]
    public void A_symbolic_link_to_a_regular_file_is_followed()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var target = workspace.Write("target.txt", "cible");
        var link = workspace.Combine("link.txt");
        File.CreateSymbolicLink(link, target);

        var outcome = Ok<RecordValue>(RunRequest(context, "cat", [], standardInputFile: link));

        Assert.Equal("cible", outcome.GetText("standard_output"));
    }

    [Fact]
    public void A_symbolic_link_loop_is_invalid_path()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var loop = workspace.Combine("loop");
        File.CreateSymbolicLink(loop, "loop");

        AssertProcessError(RunRequest(context, "cat", [], standardInputFile: loop), "invalid_path", "cat");
    }

    [Fact]
    public void A_path_name_that_is_too_long_is_invalid_path()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var tooLong = workspace.Combine(new string('a', 300));

        AssertProcessError(RunRequest(context, "cat", [], standardInputFile: tooLong), "invalid_path", "cat");
    }

    [Fact]
    public void Run_inherit_feeds_the_file_and_returns_the_exit_code()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext(out var captured);
        var file = workspace.Write("inherit.txt", "herite");

        var exitCode = Ok<IntegerValue>(RunInheritRequest(context, "cat", [], standardInputFile: file));

        Assert.Equal(0, exitCode.Number);
        Assert.Equal("herite", captured.ReadStandardOutput());
    }

    [Fact]
    public void Parallel_run_all_feeds_each_file_and_keeps_the_request_order()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var first = workspace.Write("first.txt", "premier");
        var second = workspace.Write("second.txt", "deuxieme");

        var results = Ok<ListValue>(Call(
            context,
            "parallel.run_all",
            new ListValue([
                Request(context, "cat", [], standardInputFile: first),
                Request(context, "cat", [], standardInputFile: second),
            ]),
            Integer(4)));

        Assert.Equal(2, results.Items.Count);
        Assert.Equal("premier", ((RecordValue)results.Items[0]).GetText("standard_output"));
        Assert.Equal("deuxieme", ((RecordValue)results.Items[1]).GetText("standard_output"));
    }

    [Fact]
    public void A_fifo_is_other_without_reading_or_blocking()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var fifo = workspace.Combine("input-fifo");
        SystemRawFiles.MakeFifo(fifo);

        AssertProcessError(RunRequest(context, "cat", [], standardInputFile: fifo), "other", "cat");
    }

    [Fact]
    public void A_device_is_other()
    {
        using var context = NewContext();

        AssertProcessError(RunRequest(context, "cat", [], standardInputFile: "/dev/null"), "other", "cat");
    }

    [Fact]
    public void A_path_containing_nul_is_invalid_argument()
    {
        using var context = NewContext();

        AssertProcessError(RunRequest(context, "cat", [], standardInputFile: "bad\0name"), "invalid_argument", "cat");
    }

    [Fact]
    public void A_regular_file_without_read_permission_is_permission_denied()
    {
        if (SystemRawFiles.RunningAsRoot)
        {
            return;
        }

        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var file = workspace.Write("no-read.txt", "secret");
        File.SetUnixFileMode(file, UnixFileMode.None);
        try
        {
            AssertProcessError(RunRequest(context, "cat", [], standardInputFile: file), "permission_denied", "cat");
        }
        finally
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Both_input_fields_set_is_invalid_argument_defensively()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var file = workspace.Write("both.txt", "file");
        var marker = workspace.Combine("marker");

        // Not reachable through process.request and its methods; built directly to hold both fields at once.
        var request = new RecordValue(
            "run_request",
            Text("touch"),
            TextList(marker),
            Text("string"),
            PathOf(string.Empty),
            new MapValue(),
            Integer(0),
            PathOf(file));

        AssertProcessError(Call(context, "process.run", request), "invalid_argument", "touch");
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void A_64_mib_binary_file_is_streamed_without_a_bcs_string()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var file = workspace.Combine("large.bin");
        const int BlockSize = 64 * 1024;
        const int Blocks = 1024; // 64 MiB
        var block = new byte[BlockSize];
        Array.Fill(block, (byte)0xFF); // invalid UTF-8: proves the bytes are never decoded
        using (var stream = File.Create(file))
        {
            for (var index = 0; index < Blocks; index++)
            {
                stream.Write(block);
            }
        }

        var request = Request(context, "wc", ["-c"], standardInputFile: file);
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var outcome = Ok<RecordValue>(Call(context, "process.run", request));
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal((BlockSize * Blocks).ToString(System.Globalization.CultureInfo.InvariantCulture),
            outcome.GetText("standard_output").Trim());
        // Anti-regression guard: a 64 MiB scripts string (plus its UTF-16 copy) cannot fit under this bound.
        Assert.True(allocated < 32L * 1024 * 1024, $"process.run allocated {allocated} bytes for a 64 MiB file input");
    }

    [Fact]
    public void The_first_pipeline_stage_uses_the_file_and_later_stages_ignore_their_own()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var file = workspace.Write("pipeline.txt", "du fichier");
        var missingForLaterStage = workspace.Combine("ignored-missing.txt");

        var first = Assert.IsType<RecordValue>(Call(
            context,
            "run_request.with_standard_input_from_file",
            Assert.IsType<RecordValue>(Call(context, "process.request", Text("cat"), TextList())),
            PathOf(file)));
        var second = Assert.IsType<RecordValue>(Call(
            context,
            "run_request.with_standard_input_from_file",
            Assert.IsType<RecordValue>(Call(context, "process.request", Text("cat"), TextList())),
            PathOf(missingForLaterStage)));
        var pipeline = Ok<RecordValue>(Call(context, "process.pipeline", new ListValue([first, second])));

        Assert.Equal("du fichier", pipeline.GetText("standard_output"));
    }

    [Fact]
    public void An_interruption_after_the_open_closes_the_prepared_descriptor()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("interrupted.txt", "contenu");
        var calls = 0;
        // The fourth check runs in the post-open descriptor verification: the readable fd is open at that point.
        Action checkInterrupted = () =>
        {
            if (++calls == 4)
            {
                throw new InvalidOperationException("interrupted");
            }
        };

        var before = Directory.GetFiles("/proc/self/fd").Length;
        Assert.Throws<InvalidOperationException>(() => StandardInputFile.Prepare("cat", string.Empty, file, checkInterrupted));
        Assert.Equal(4, calls);
        Assert.Equal(before, Directory.GetFiles("/proc/self/fd").Length);
    }

    [Fact]
    public void An_unpreparable_pipeline_input_is_spawn_failed()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var missing = workspace.Combine("absent.txt");
        var first = Assert.IsType<RecordValue>(Call(
            context,
            "run_request.with_standard_input_from_file",
            Assert.IsType<RecordValue>(Call(context, "process.request", Text("cat"), TextList())),
            PathOf(missing)));
        var second = Assert.IsType<RecordValue>(Call(context, "process.request", Text("cat"), TextList()));

        AssertProcessError(
            Call(context, "process.pipeline", new ListValue([first, second])),
            "spawn_failed",
            "cat");
    }

    [Fact]
    public void An_unpreparable_parallel_input_is_spawn_failed()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var missing = workspace.Combine("absent.txt");
        var request = Assert.IsType<RecordValue>(Call(
            context,
            "run_request.with_standard_input_from_file",
            Assert.IsType<RecordValue>(Call(context, "process.request", Text("cat"), TextList())),
            PathOf(missing)));

        var failure = ErrorRecord(Call(context, "parallel.run_all", new ListValue([request]), Integer(2)), "error");

        Assert.Equal("spawn_failed", failure.GetText("kind"));
        // Existing parallel mapping: a spawn failure has an empty subject and the program in the message.
        Assert.Equal(string.Empty, failure.GetText("subject"));
        Assert.Contains("cat", failure.GetText("message"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_invalid_file_is_reported_before_an_invalid_program()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var missing = workspace.Combine("absent.txt");

        // The file is prepared before the program is resolved: the empty program would be invalid_argument.
        var request = Request(context, string.Empty, [], standardInputFile: missing);
        AssertProcessError(Call(context, "process.run", request), "not_found", string.Empty);
    }

    [Fact]
    public void A_low_descriptor_is_moved_above_the_standard_descriptors()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("low-fd.txt", "contenu");
        // Save the test process's fd 0 (it may be closed already) and restore it in the finally below.
        var saved = Libc.FileControl(0, Libc.DuplicateAboveCloseOnExec, 3);
        var descriptor = Libc.Open(
            NativeFileSystem.ToNativePath(file),
            Libc.OpenReadOnly | Libc.OpenCloseOnExec,
            0);
        Assert.True(descriptor >= 0, "cannot open the probe file");
        try
        {
            // Put the file at fd 0 on purpose: a plain open would only reach 0 if it happened to be free.
            if (descriptor != 0)
            {
                Assert.Equal(0, Libc.DuplicateTo(descriptor, 0));
                Libc.Close(descriptor);
            }

            var moved = StandardInputFile.MoveAboveStandardDescriptors(0, "cat");
            try
            {
                Assert.True(moved > 2, $"the descriptor stayed at {moved}");
                Span<byte> bytes = stackalloc byte[7];
                Assert.Equal(7, (int)Libc.Read(moved, bytes, (nuint)bytes.Length));
                Assert.Equal("contenu", System.Text.Encoding.UTF8.GetString(bytes));
            }
            finally
            {
                Libc.Close(moved);
            }
        }
        finally
        {
            if (saved >= 0)
            {
                _ = Libc.DuplicateTo(saved, 0);
                Libc.Close(saved);
            }
            else
            {
                Libc.Close(0);
            }
        }
    }

    [Fact]
    public void Descriptors_are_closed_on_success_and_on_every_failure()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var file = workspace.Write("close.txt", "contenu");
        var directory = workspace.Combine("close-directory");
        Directory.CreateDirectory(directory);

        var before = Directory.GetFiles("/proc/self/fd").Length;
        _ = Ok<RecordValue>(RunRequest(context, "cat", [], standardInputFile: file));
        var afterSuccess = Directory.GetFiles("/proc/self/fd").Length;
        Assert.Equal(before, afterSuccess);

        _ = RunRequest(context, "cat", [], standardInputFile: directory);
        Assert.Equal(afterSuccess, Directory.GetFiles("/proc/self/fd").Length);

        _ = RunRequest(context, "cat", [], standardInputFile: workspace.Combine("absent.txt"));
        Assert.Equal(afterSuccess, Directory.GetFiles("/proc/self/fd").Length);

        // A launch failure after the file was opened must close it too.
        _ = RunRequest(context, "/nonexistent/scripts-stdin-program", [], standardInputFile: file);
        Assert.Equal(afterSuccess, Directory.GetFiles("/proc/self/fd").Length);
    }

    private static RecordValue RequestWithStandardInputFileAndWorkingDirectory(
        RuntimeContext context,
        string file,
        string workingDirectory)
    {
        var request = Assert.IsType<RecordValue>(Call(context, "process.request", Text("cat"), TextList()));
        request = Assert.IsType<RecordValue>(Call(context, "run_request.with_standard_input_from_file", request, PathOf(file)));
        return Assert.IsType<RecordValue>(Call(context, "run_request.with_working_directory", request, PathOf(workingDirectory)));
    }
}
