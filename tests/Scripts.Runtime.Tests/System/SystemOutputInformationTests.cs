using System.IO.Pipes;
using System.Text;
using Scripts.Runtime.Output;
using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

[Collection(SystemTestCollection.Name)]
public sealed class SystemOutputTests
{
    private static int FileDescriptorOf(FileStream stream) => (int)stream.SafeFileHandle.DangerousGetHandle();

    [Fact]
    public void Assert_true_returns_unit()
    {
        Assert.Same(UnitValue.Instance, Call("assert", BooleanValue.True, Text("unused")));
    }

    [Fact]
    public void Assert_false_raises_runtime_0021_with_the_script_message()
    {
        var failure = Assert.Throws<RuntimeFailure>(() => Call("assert", BooleanValue.False, Text("boom")));

        Assert.Equal("SCRIPTS-RUNTIME-0021", failure.Code);
        Assert.Equal(("message", "boom"), Assert.Single(failure.Arguments));
    }

    [Fact]
    public void Print_appends_a_newline_and_writes_utf8_without_bom_including_nul_and_empty_text()
    {
        using var context = NewContext(out var captured);

        Call(context, "io.write_standard_output_line", Text("line 1"));
        Call(context, "io.write_standard_output_line", Text("café 😀"));
        Call(context, "io.write_standard_output_line", Text(string.Empty));
        Call(context, "io.write_standard_output_line", Text("a\0b"));
        Call(context, "io.write_standard_error_line", Text("warning"));

        Assert.Equal("line 1\ncafé 😀\n\na\0b\n", captured.ReadStandardOutput());
        Assert.Equal("warning\n", captured.ReadStandardError());
    }

    [Fact]
    public void Stdout_is_buffered_and_flushed_before_each_eprint_preserving_relative_order()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("both.txt");
        using var file = SystemRawFiles.OpenShared(path);
        var descriptor = FileDescriptorOf(file);
        using var context = new RuntimeContext(
            new FileDescriptorOutputChannel(descriptor, "stdout", true),
            new FileDescriptorOutputChannel(descriptor, "stderr", false),
            []);

        Call(context, "io.write_standard_output_line", Text("a"));
        Assert.Equal(0, new FileInfo(path).Length);
        Call(context, "io.write_standard_error_line", Text("b"));
        Assert.Equal("a\nb\n", File.ReadAllText(path));
        Call(context, "io.write_standard_output_line", Text("c"));
        Assert.Equal("a\nb\n", File.ReadAllText(path));
        context.FlushAll();

        Assert.Equal("a\nb\nc\n", File.ReadAllText(path));
        Assert.Equal([0x61, 0x0A, 0x62, 0x0A, 0x63, 0x0A], File.ReadAllBytes(path));
    }

    [Fact]
    public void Large_texts_and_surrogate_pairs_at_chunk_boundaries_are_encoded_exactly()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("big.txt");
        var text = new string('a', 8191) + "😀" + new string('é', 20_000) + "😀" + new string('z', 300_000);
        using (var file = SystemRawFiles.OpenShared(path))
        {
            var channel = new FileDescriptorOutputChannel(FileDescriptorOf(file), "stdout", true);
            channel.Write(text);
            channel.Write("\n");
            channel.Flush();
        }

        Assert.Equal(new UTF8Encoding(false).GetBytes(text + "\n"), File.ReadAllBytes(path));
    }

    [Fact]
    public void The_column_query_tracks_the_last_character_written()
    {
        using var context = NewContext();

        Assert.True(context.StandardError.IsAtLineStart);
        context.StandardError.Write("partiel");
        Assert.False(context.StandardError.IsAtLineStart);
        context.StandardError.Write("\n");
        Assert.True(context.StandardError.IsAtLineStart);
        Call(context, "io.write_standard_error_line", Text("x"));
        Assert.True(context.StandardError.IsAtLineStart);
        context.StandardOutput.Write("abc");
        Assert.False(context.StandardOutput.IsAtLineStart);
    }

    [Fact]
    public void A_write_failure_is_runtime_0019_and_later_output_is_discarded()
    {
        using var full = File.OpenHandle("/dev/full", FileMode.Open, FileAccess.Write);
        var channel = new FileDescriptorOutputChannel((int)full.DangerousGetHandle(), "stdout", true);

        channel.Write("x");
        var failure = Assert.Throws<RuntimeFailure>(channel.Flush);

        Assert.Equal("SCRIPTS-RUNTIME-0019", failure.Code);
        Assert.Equal("stdout", failure.Arguments.Single(argument => argument.Name == "stream").Value);
        Assert.False(string.IsNullOrEmpty(failure.Arguments.Single(argument => argument.Name == "reason").Value));
        channel.Write("more");
        channel.Flush();
    }

    [Fact]
    public void Print_on_a_full_device_raises_runtime_0019_when_the_buffer_is_flushed_by_io_write_standard_error_line()
    {
        using var full = File.OpenHandle("/dev/full", FileMode.Open, FileAccess.Write);
        using var context = new RuntimeContext(
            new FileDescriptorOutputChannel((int)full.DangerousGetHandle(), "stdout", true),
            new MemoryOutputChannel(),
            []);
        Call(context, "io.write_standard_output_line", Text("x"));

        var failure = Assert.Throws<RuntimeFailure>(() => Call(context, "io.write_standard_error_line", Text("y")));

        Assert.Equal("SCRIPTS-RUNTIME-0019", failure.Code);
    }

    [Fact]
    public void Writing_to_a_closed_pipe_ends_stdout_silently_but_fails_stderr()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using (var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle))
        {
            server.DisposeLocalCopyOfClientHandle();
        }

        var descriptor = (int)server.SafePipeHandle.DangerousGetHandle();
        var standardOutput = new FileDescriptorOutputChannel(descriptor, "stdout", true);
        var standardError = new FileDescriptorOutputChannel(descriptor, "stderr", false);

        standardOutput.Write("x");
        Assert.Throws<BrokenPipeTermination>(standardOutput.Flush);
        standardOutput.Flush();
        standardError.Write("y");
        var failure = Assert.Throws<RuntimeFailure>(standardError.Flush);

        Assert.Equal("SCRIPTS-RUNTIME-0019", failure.Code);
    }

    [Fact]
    public void Try_flush_reports_failure_instead_of_throwing()
    {
        using var full = File.OpenHandle("/dev/full", FileMode.Open, FileAccess.Write);
        var channel = new FileDescriptorOutputChannel((int)full.DangerousGetHandle(), "stdout", true);
        channel.Write("x");

        Assert.False(channel.TryFlush(TimeSpan.FromMilliseconds(100)));
        Assert.True(new MemoryOutputChannel().TryFlush(TimeSpan.Zero));
    }

    [Fact]
    public void Shutdown_flushes_pending_output()
    {
        using var workspace = new SystemTestWorkspace();
        var path = workspace.Combine("out.txt");
        using var file = SystemRawFiles.OpenShared(path);
        var context = new RuntimeContext(new FileDescriptorOutputChannel(FileDescriptorOf(file), "stdout", true), new MemoryOutputChannel(), []);
        Call(context, "io.write_standard_output_line", Text("final"));

        context.Shutdown();
        context.Dispose();

        Assert.Equal("final\n", File.ReadAllText(path));
    }

    [Fact]
    public void Capture_mode_exposes_stdout_and_stderr_buffers_and_reports_process_run_inherit_children()
    {
        using var context = RuntimeContext.CreateForCapture(["x"], out var captured, listenForSignals: false);

        Assert.True(context.CapturesChildStreams);
        Assert.Equal(["x"], context.ScriptArguments);
        Call(context, "io.write_standard_output_line", Text("a"));
        Call(context, "io.write_standard_error_line", Text("b"));

        Assert.Equal("a\n", captured.ReadStandardOutput());
        Assert.Equal("b\n", captured.ReadStandardError());
    }

    [Fact]
    public void Console_context_installs_no_handlers_when_asked_not_to_and_the_parameterless_context_is_plain()
    {
        using var console = RuntimeContext.CreateForConsole([], listenForSignals: false);
        using var plain = new RuntimeContext();

        Assert.False(console.CapturesChildStreams);
        Assert.False(plain.CapturesChildStreams);
        Assert.Empty(plain.ScriptArguments);
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemInformationTests
{
    [Fact]
    public void Uname_reports_the_kernel_values()
    {
        var information = Ok<RecordValue>(Call("system.get_information"));

        Assert.Equal("system_information", information.TypeName);
        Assert.Equal("Linux", information.GetText("kernel_name"));
        Assert.Equal(File.ReadAllText("/proc/sys/kernel/hostname").Trim(), information.GetText("host_name"));
        Assert.Equal(File.ReadAllText("/proc/sys/kernel/osrelease").Trim(), information.GetText("kernel_release"));
        // x86-64 only: the suite runs on the one supported architecture.
        Assert.Equal("x86_64", information.GetText("machine_architecture"));
    }

    [Fact]
    public void Whoami_matches_the_id_command()
    {
        using var context = NewContext();

        var expected = SystemScripts.Exec(context, "id", "-un").GetText("standard_output").Trim();

        Assert.Equal(expected, Assert.IsType<StringValue>(Call(context, "system.user_name")).Text);
    }
}

[Collection(SystemTestCollection.Name)]
public sealed class SystemCoverageTests
{
    private static readonly string[] SystemFunctionNames =
    [
        "assert",
    ];

    /// <summary>The system-scope methods (not global functions): process_result.require_success, pipeline_result.require_success and file_lock.release.</summary>
    private static readonly string[] SystemMethodNames =
    [
        "process_result.require_success",
        "pipeline_result.require_success",
        "file_lock.release",
    ];

    /// <summary>The run_request copy methods (SPEC-STDLIB §7.3, the alternative to the six-argument constructor).</summary>
    private static readonly string[] ExecRequestMethodNames =
    [
        "run_request.with_standard_input",
        "run_request.with_standard_input_from_file",
        "run_request.with_working_directory",
        "run_request.with_environment",
        "run_request.with_timeout",
    ];

    /// <summary>
    /// The intrinsic-namespace entries implemented by <see cref="SystemStdlib"/> (always available, never imported): they are
    /// registered under their fully qualified catalog name, not as global functions.
    /// </summary>
    private static readonly string[] StandardModuleNames =
    [
        "io.read_standard_input", "io.read_standard_input_line", "io.write_standard_output", "io.write_standard_output_line", "io.write_standard_error_line", "io.write_standard_error", "process.arguments", "process.defer", "process.find_program_path", "process.identifier", "process.set_umask", "process.request", "process.run",
        "process.run_inherit",
        "process.spawn", "process.wait_for_process", "process.terminate",
        "process.script_path",
        "process.pipeline",
        "process.exit_with_code",
        "process.exit_with_message",
        "process.pipeline_to_file_overwrite",
        "time.unix_seconds", "time.monotonic_milliseconds", "time.sleep_milliseconds", "time.to_utc_iso8601", "time.to_local_iso8601", "time.parse_iso8601",
        "random.bytes_hex", "random.bytes_base64", "random.integer_below",
        "filesystem.read_file", "filesystem.read_file_no_follow", "filesystem.for_each_line", "filesystem.read_file_bounded", "filesystem.write_file_overwrite",
        "filesystem.write_file_overwrite_atomically", "filesystem.append_file", "filesystem.path_exists",
        "filesystem.get_path_status", "filesystem.get_file_system_status", "filesystem.remove_file", "filesystem.remove_empty_directory",
        "filesystem.remove_directory_tree", "filesystem.make_directory", "filesystem.make_directory_with_permissions", "filesystem.make_directory_tree",
        "filesystem.copy_file", "filesystem.copy_file_overwrite", "filesystem.move_path",
        "filesystem.move_path_overwrite", "filesystem.get_current_directory", "filesystem.set_current_directory",
        "filesystem.make_temporary_directory", "filesystem.list_directory", "filesystem.walk_directory",
        "filesystem.sha256_file",
        "filesystem.copy_directory_tree", "filesystem.make_symbolic_link", "filesystem.make_hard_link", "filesystem.read_link",
        "filesystem.get_real_path", "filesystem.set_permissions", "filesystem.make_temporary_file",
        "filesystem.write_file_exclusive", "filesystem.write_file_exclusive_with_permissions", "filesystem.touch", "filesystem.glob",
        "filesystem.acquire_file_lock",
        "parallel.transform", "parallel.run_all", "parallel.walk_directory",
        "system.get_information", "system.user_name", "system.cpu_count",
        "system.effective_user_id", "system.effective_group_id", "system.user_id", "system.group_id",
        "filesystem.set_owner", "filesystem.set_owner_no_follow",
    ];

    [Fact]
    public void Every_system_catalog_entry_is_registered_and_nothing_else()
    {
        var table = new Dictionary<string, StdlibImplementation>(StringComparer.Ordinal);

        SystemStdlib.Register(table);

        var expected = SystemFunctionNames.Concat(SystemMethodNames).Concat(ExecRequestMethodNames).Concat(StandardModuleNames).Order(StringComparer.Ordinal).ToArray();
        Assert.Single(SystemFunctionNames);
        Assert.Equal(expected, table.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_registered_name_is_a_global_function_of_the_catalog_and_resolvable_through_the_registry()
    {
        foreach (var name in SystemFunctionNames)
        {
            var entry = Scripts.Core.StdlibCatalog.FindGlobalFunction(name);

            Assert.NotNull(entry);
            Assert.Equal(name, entry.QualifiedName);
            Assert.Contains(name, StdlibRegistry.ImplementedNames);
        }

        // The system method is registered and resolvable too, but it is a method, not a global function.
        Assert.All(SystemMethodNames, name => Assert.Contains(name, StdlibRegistry.ImplementedNames));
    }

    [Theory]
    [InlineData("spawn")]
    [InlineData("wait_for_process")]
    [InlineData("terminate")]
    public void Every_process_module_entry_is_registered_and_resolvable(string name)
    {
        var entry = Scripts.Core.StdlibCatalog.FindModuleFunction("process", name);

        Assert.NotNull(entry);
        Assert.Equal("process." + name, entry.QualifiedName);
        Assert.Contains("process." + name, StdlibRegistry.ImplementedNames);
    }

    [Fact]
    public void Catalog_functions_that_touch_the_system_are_all_in_scope()
    {
        var systemEffects = new[] { "filesystem_read", "filesystem_write", "process", "stdout", "stderr", "system_read" };
        var inScopeNames = SystemFunctionNames.Concat(StandardModuleNames).ToHashSet(StringComparer.Ordinal);

        var uncovered = Scripts.Core.StdlibCatalog.All
            .Where(entry => entry.Effects.Any(effect => systemEffects.Contains(effect)) && !inScopeNames.Contains(entry.QualifiedName))
            .Select(entry => entry.QualifiedName)
            .ToArray();

        Assert.Empty(uncovered);
    }
}
