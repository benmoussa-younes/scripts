using Scripts.Runtime.Platform;

namespace Scripts.Runtime.Processes;

/// <summary>process.run/process.run_inherit/process.run_shell, process.spawn, process.wait_for_process, process.terminate (SPEC-STDLIB §7.3). Expected failures are ProcessFailureException.</summary>
internal static class ProcessOperations
{
    private static readonly double MaximumTimeoutMilliseconds = TimeSpan.MaxValue.TotalMilliseconds;

    /// <summary>process.run_shell: captured run; standardInput null closes stdin immediately.</summary>
    public static RecordValue Execute(
        RuntimeContext context,
        string program,
        IReadOnlyList<string> arguments,
        string? standardInput,
        long? timeoutMilliseconds)
    {
        TimeSpan? timeout = null;
        if (timeoutMilliseconds is { } milliseconds)
        {
            if (milliseconds < 1)
            {
                throw ProcessFailureException.InvalidArgument(program, "the timeout must be at least 1 ms");
            }

            timeout = milliseconds >= MaximumTimeoutMilliseconds ? null : TimeSpan.FromMilliseconds(milliseconds);
        }

        return RunCaptured(context, program, arguments, standardInput, string.Empty, timeout, timeoutMilliseconds, ProcessLaunchContext.Inherit);
    }

    /// <summary>
    /// process.run: captures one run_request in its own working directory and environment. A zero deadline means no
    /// deadline, a negative one is invalid_argument (DESIGN §7.3).
    /// </summary>
    public static RecordValue ExecuteRequest(RuntimeContext context, RunRequestData request)
    {
        if (request.TimeoutMilliseconds < 0)
        {
            throw ProcessFailureException.InvalidArgument(request.Program, "the timeout must not be negative");
        }

        var timeout = ProcessTimings.ToTimeout(request.TimeoutMilliseconds);

        return RunCaptured(
            context,
            request.Program,
            request.Arguments,
            request.StandardInput,
            request.StandardInputFile,
            timeout,
            request.TimeoutMilliseconds,
            request.LaunchContext);
    }

    private static RecordValue RunCaptured(
        RuntimeContext context,
        string program,
        IReadOnlyList<string> arguments,
        string? standardInput,
        string standardInputFile,
        TimeSpan? timeout,
        long? timeoutMilliseconds,
        ProcessLaunchContext launchContext)
    {
        var inputFile = StandardInputFile.Prepare(program, standardInput ?? string.Empty, standardInputFile, context.ThrowIfInterrupted);
        var usesInputFile = inputFile is not null;
        using var child = LaunchWithInput(
            context,
            program,
            arguments,
            redirectInput: true,
            redirectOutputs: true,
            launchContext,
            inputFile);
        var standardOutput = new BufferingSink(child.Kill);
        var standardError = new BufferingSink(child.Kill);
        child.StartPumps(standardOutput, standardError);
        // A file input is already wired to fd 0 by the launch: scripts never reads its bytes. Otherwise an empty
        // standard input closes the child's stdin immediately: a dedicated feeder thread is only needed when
        // there are bytes to write (SPEC-STDLIB §7.3). This also keeps the failure path of a launch under a low
        // descriptor limit from accumulating feeder resources.
        if (!usesInputFile)
        {
            if (string.IsNullOrEmpty(standardInput))
            {
                child.CloseStandardInput();
            }
            else
            {
                child.FeedStandardInputAndClose(NativeText.ToBytes(standardInput));
            }
        }

        if (!child.WaitForCompletion(context.Interruption, timeout))
        {
            child.Terminate();
            throw ProcessFailureException.TimedOut(program, timeoutMilliseconds!.Value);
        }

        return CreateProcessResult(child.ExitCode, standardOutput, standardError, program);
    }

    /// <summary>
    /// process.run_inherit: runs one run_request sharing scripts's stdin/stdout/stderr, or pumping stdout/stderr into the
    /// capture buffers in capture mode (DESIGN §7.3). The standard input must be empty (it is inherited) and the
    /// deadline must not be negative; a non-zero exit code is data.
    /// </summary>
    public static long ExecuteRequestInheriting(RuntimeContext context, RunRequestData request)
    {
        if (request.StandardInput.Length != 0)
        {
            throw ProcessFailureException.InvalidArgument(
                request.Program, "the standard input must be empty: process.run_inherit inherits stdin");
        }

        if (request.TimeoutMilliseconds < 0)
        {
            throw ProcessFailureException.InvalidArgument(request.Program, "the timeout must not be negative");
        }

        // Flush scripts's output first: a flush failure terminates the call and must not leave a prepared input
        // descriptor undisposed (the file is opened only after every pre-launch step that can fail this way).
        context.FlushAll();
        var inputFile = StandardInputFile.Prepare(request.Program, request.StandardInput, request.StandardInputFile, context.ThrowIfInterrupted);
        using var child = LaunchWithInput(
            context,
            request.Program,
            request.Arguments,
            redirectInput: false,
            redirectOutputs: context.CapturesChildStreams,
            request.LaunchContext,
            inputFile);
        if (context.CapturesChildStreams)
        {
            child.StartPumps(new ChannelForwardingSink(context.StandardOutput), new ChannelForwardingSink(context.StandardError));
        }

        // run_inherit reports the exit code only: it waits for the direct child, never for the end of its pipes
        // (DESIGN §6). Under capture a descendant that kept them open would otherwise block the call until its own
        // end, whatever the child did.
        var timeout = ProcessTimings.ToTimeout(request.TimeoutMilliseconds);
        if (!child.WaitForExitOnly(context.Interruption, timeout))
        {
            child.Terminate();
            throw ProcessFailureException.TimedOut(request.Program, request.TimeoutMilliseconds);
        }

        // The child is gone: give its pipes a short, bounded drain so the bytes it wrote reach the document, then
        // return (a descendant that keeps them open is not waited for, DESIGN §6).
        child.DrainPumpsWithin(ProcessTimings.InheritDrainBudget);
        child.ThrowIfPumpFailed();
        return child.ExitCode;
    }

    public static RecordValue Spawn(RuntimeContext context, string program, IReadOnlyList<string> arguments)
    {
        var child = Launch(context, program, arguments, redirectInput: true, redirectOutputs: true);
        var standardOutput = new BufferingSink(child.Kill);
        var standardError = new BufferingSink(child.Kill);
        child.StartPumps(standardOutput, standardError);
        child.CloseStandardInput();
        context.SpawnedProcesses.Add(new SpawnedProcess(child, standardOutput, standardError, program));
        return new RecordValue("process_handle", IntegerValue.From(child.ProcessIdentifier));
    }

    /// <summary>
    /// process.wait_for_process: waits for the child and for EOF on its streams, then returns its buffered outputs. The
    /// handle is consumed in every outcome (SPEC-STDLIB §7.3). A deadline of 0 waits without bound; a positive deadline
    /// counts from the call and expiry terminates the child tree and returns timed_out with the spawned program as
    /// subject; a negative deadline is invalid_argument before waiting.
    /// </summary>
    public static RecordValue WaitForSpawned(RuntimeContext context, long processIdentifier, long timeoutMilliseconds)
    {
        // Consume the handle first: a later wait/kill must be invalid_argument whatever this call returns.
        var spawned = context.SpawnedProcesses.TakeForWait(processIdentifier)
            ?? throw ProcessFailureException.InvalidArgument(string.Empty, "invalid handle, already waited on or killed");
        try
        {
            if (timeoutMilliseconds < 0)
            {
                // The handle is valid, so the spawned program is known: use it as subject (invalid handle keeps "").
                throw ProcessFailureException.InvalidArgument(spawned.Program, "the timeout must not be negative");
            }

            // 0 means no deadline; a positive value counts from the call, with no upper bound (ProcessTimings).
            var timeout = timeoutMilliseconds == 0 ? null : ProcessTimings.ToTimeout(timeoutMilliseconds);
            if (!spawned.Child.WaitForCompletion(context.Interruption, timeout))
            {
                spawned.Child.Terminate();
                throw ProcessFailureException.TimedOut(spawned.Program, timeoutMilliseconds);
            }

            spawned.Child.ThrowIfPumpFailed();
            return CreateProcessResult(spawned.Child.ExitCode, spawned.StandardOutput, spawned.StandardError, spawned.Program);
        }
        finally
        {
            spawned.Child.Dispose();
        }
    }

    public static void KillSpawned(RuntimeContext context, long processIdentifier)
    {
        var spawned = context.SpawnedProcesses.ReserveForKill(processIdentifier)
            ?? throw ProcessFailureException.InvalidArgument(string.Empty, "invalid handle, already waited on or killed");
        try
        {
            // The handle is already reserved (Killed = true under the table gate). If the child exits between the
            // reservation and the signal, kill(2) reports ESRCH, which is the normal no-op outcome.
            var errno = spawned.Child.RequestTermination();
            if (errno is not 0 and not Errno.NoSuchProcess)
            {
                throw new ProcessFailureException(ProcessErrorKinds.Other, string.Empty, "cannot send SIGTERM (errno " + errno + ")");
            }
        }
        finally
        {
            // Release the process object and the pipes once the child has exited and the pumps reached EOF.
            spawned.Child.Dispose();
        }
    }

    /// <summary>
    /// Launches a child and closes the prepared input file's parent descriptor as soon as the child owns its fd 0.
    /// The file is closed on a launch failure too, so no path leaks it.
    /// </summary>
    private static ChildProcess LaunchWithInput(
        RuntimeContext context,
        string program,
        IReadOnlyList<string> arguments,
        bool redirectInput,
        bool redirectOutputs,
        ProcessLaunchContext? launchContext,
        StandardInputFile? inputFile)
    {
        try
        {
            return Launch(context, program, arguments, redirectInput, redirectOutputs, launchContext, inputFile?.Descriptor ?? -1);
        }
        finally
        {
            inputFile?.Dispose();
        }
    }

    internal static ChildProcess Launch(
        RuntimeContext context,
        string program,
        IReadOnlyList<string> arguments,
        bool redirectInput,
        bool redirectOutputs,
        ProcessLaunchContext? launchContext = null,
        int inputFileDescriptor = -1)
    {
        var requestedContext = launchContext ?? ProcessLaunchContext.Inherit;
        ProgramResolver.ValidateProgram(program);
        ProgramResolver.ValidateArguments(program, arguments);
        ValidateEnvironment(program, requestedContext.EnvironmentOverlay);
        var workingDirectory = ResolveWorkingDirectory(program, requestedContext.WorkingDirectory);
        context.ThrowIfInterrupted();
        var resolvedPath = ProgramResolver.ResolveForLaunch(program, workingDirectory, requestedContext.EffectivePathVariable);
        if (program.Contains('/', StringComparison.Ordinal))
        {
            RefuseUnreachableOrDirectory(resolvedPath, program);
        }

        // P8d: posix_spawn with an explicit envp and (for every captured child) its own process group created
        // before exec. process.run_inherit inherits stdin in both modes, so it must keep scripts's foreground group:
        // a background group reading the terminal would stop with SIGTTIN. `redirectInput` is the discriminant:
        // it is true for run/spawn/process.run_shell/pipeline stages, false for run_inherit.
        var ownProcessGroup = redirectInput;
        var spec = new SpawnSpec(
            program,
            resolvedPath,
            arguments,
            ChildEnvironment.BuildEnviron(requestedContext.EnvironmentOverlay),
            workingDirectory,
            redirectInput,
            redirectOutputs,
            OwnProcessGroup: ownProcessGroup,
            InputFileDescriptor: inputFileDescriptor);
        return ChildProcess.Start(spec, context.ChildProcesses, ownProcessGroup: ownProcessGroup);
    }

    /// <summary>
    /// Returns the absolute effective working directory, or null to inherit the process directory (DESIGN §7.3).
    /// A relative directory is resolved against the process directory at launch; an absent or non-directory path
    /// is <c>not_found</c> / <c>not_a_directory</c>. The validation happens before the program is resolved.
    /// </summary>
    private static string? ResolveWorkingDirectory(string program, string workingDirectory)
    {
        if (workingDirectory.Length == 0)
        {
            return null;
        }

        if (PathText.ContainsNul(workingDirectory))
        {
            throw ProcessFailureException.InvalidArgument(program, "the working directory contains a NUL character");
        }

        string effective;
        if (workingDirectory[0] == '/')
        {
            effective = workingDirectory;
        }
        else
        {
            if (NativeFileSystem.TryGetCurrentDirectory(out var currentDirectory) != 0)
            {
                throw ProcessFailureException.NotFound(program, "current directory inaccessible");
            }

            effective = PathText.Join(NativeText.DecodeLenient(currentDirectory), workingDirectory);
        }

        var errno = NativeFileSystem.TryGetStatus(NativeText.ToNullTerminated(effective), true, out var status);
        if (errno != 0)
        {
            // A cwd failure names the working directory, never the program: the subject stays the program.
            throw errno switch
            {
                Errno.NoSuchEntry => ProcessFailureException.NotFound(program, "working directory not found"),
                Errno.NotADirectory => ProcessFailureException.NotADirectory(program, "working directory is not a directory"),
                Errno.AccessDenied or Errno.NotPermitted =>
                    ProcessFailureException.PermissionDenied(program, "working directory not accessible"),
                _ => ProcessFailureException.FromErrno(errno, program),
            };
        }

        if (!status.IsDirectory)
        {
            throw ProcessFailureException.NotADirectory(program, "working directory is not a directory");
        }

        // `statx` succeeds on a directory even when it lacks every search bit, but `chdir` then fails with EACCES.
        // Detect that here so the diagnostic names the working directory, not the program.
        if ((status.Mode & DirectorySearchBits) == 0)
        {
            throw ProcessFailureException.PermissionDenied(program, "working directory not accessible");
        }

        return effective;
    }

    /// <summary>The three execute/search permission bits of a directory (0o111).</summary>
    private const uint DirectorySearchBits = 0x49;

    /// <summary>
    /// A malformed overlay entry is invalid_argument before launch (DESIGN §7.3): an empty key, '=' or NUL in a
    /// key, or NUL in a value.
    /// </summary>
    private static void ValidateEnvironment(string program, IReadOnlyDictionary<string, string>? overlay)
    {
        if (overlay is null)
        {
            return;
        }

        foreach (var (name, value) in overlay)
        {
            if (name.Length == 0)
            {
                throw ProcessFailureException.InvalidArgument(program, "an environment variable name is empty");
            }

            if (name.Contains('=', StringComparison.Ordinal) || PathText.ContainsNul(name))
            {
                throw ProcessFailureException.InvalidArgument(program, "an environment variable name contains '=' or NUL");
            }

            if (PathText.ContainsNul(value))
            {
                throw ProcessFailureException.InvalidArgument(program, "an environment variable value contains NUL");
            }
        }
    }

    // execve fails with the errno of the path walk (and EACCES for a directory); report it exactly as the kernel would.
    private static void RefuseUnreachableOrDirectory(string resolvedPath, string program)
    {
        var errno = NativeFileSystem.TryGetStatus(NativeText.ToNullTerminated(resolvedPath), true, out var status);
        if (errno != 0)
        {
            throw ProcessFailureException.FromErrno(errno, program);
        }

        if (status.IsDirectory)
        {
            throw ProcessFailureException.FromErrno(Errno.AccessDenied, program);
        }
    }

    // The `program` field is the program string as passed to the call, appended last (DESIGN §3.4).
    private static RecordValue CreateProcessResult(int exitCode, BufferingSink standardOutput, BufferingSink standardError, string program) =>
        new(
            "process_result",
            IntegerValue.From(exitCode),
            new StringValue(standardOutput.ReadText(program)),
            new StringValue(standardError.ReadText(program)),
            new StringValue(program));
}
