using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Scripts.Runtime.Platform;

/// <summary>
/// One native launch (P8d): the resolved absolute program, its arguments, the byte-exact environment block
/// ("NAME=VALUE\0"), an optional absolute working directory, the stdio redirections and whether the child gets its
/// own process group. No shell, no word splitting and no implicit PATH search: the caller has resolved the path.
/// </summary>
internal sealed record SpawnSpec(
    string Program,
    string ResolvedPath,
    IReadOnlyList<string> Arguments,
    byte[] EnvironmentBlock,
    string? WorkingDirectory,
    bool RedirectInput,
    bool RedirectOutputs,
    bool OwnProcessGroup,
    int InputFileDescriptor = -1);

/// <summary>A successfully started child: its pid and the parent ends of the redirected streams (null when inherited).</summary>
internal sealed class SpawnedChild(int processIdentifier, StreamWriter? standardInput, Stream? standardOutput, Stream? standardError)
{
    public int ProcessIdentifier { get; } = processIdentifier;

    public StreamWriter? StandardInput { get; } = standardInput;

    public Stream? StandardOutput { get; } = standardOutput;

    public Stream? StandardError { get; } = standardError;
}

/// <summary>
/// Direct <c>posix_spawn</c> launch with an explicit <c>envp</c> and a real process group, and <c>waitpid</c> for the
/// exit status (P8d). The child is placed in its own group by <c>POSIX_SPAWN_SETPGROUP</c> before <c>exec</c>, so
/// <c>killpg</c> reaches the whole subtree atomically (the parent-side <c>setpgid</c> raced the child's exec).
/// </summary>
internal static unsafe class PosixSpawn
{
    /// <summary>The lowest descriptor a child end is moved to, so a file action never clobbers another action's fd.</summary>
    private const int MinimumChildDescriptor = 10;

    /// <summary>
    /// Starts the child. Returns 0 on success (with <paramref name="child"/> owning the parent ends) or the errno of
    /// the failing call. The caller maps the errno to the catalogued process failure.
    /// </summary>
    public static int TryLaunch(SpawnSpec spec, out SpawnedChild? child)
    {
        child = null;
        using var arguments = Utf8Pointers.FromStrings([spec.ResolvedPath, .. spec.Arguments]);
        using var environment = Utf8Pointers.FromBlock(spec.EnvironmentBlock);

        var inputRead = -1;
        var inputWrite = -1;
        var outputRead = -1;
        var outputWrite = -1;
        var errorRead = -1;
        var errorWrite = -1;
        nint fileActions = 0;
        nint attributes = 0;
        nint signalMask = 0;
        nint signalDefault = 0;
        var fileActionsInitialized = false;
        var attributesInitialized = false;

        try
        {
            // A prepared file input replaces the stdin pipe: the descriptor is duplicated to fd 0 and the child
            // reads its bytes directly (no scripts string, no feeder thread). The caller closes its copy after launch.
            if (spec.RedirectInput && spec.InputFileDescriptor < 0)
            {
                var errno = CreatePipe(out inputRead, out inputWrite);
                if (errno != 0)
                {
                    return errno;
                }

                var moved = MoveAboveMinimum(inputRead, out errno);
                if (errno != 0)
                {
                    return errno;
                }

                inputRead = moved;
            }

            if (spec.RedirectOutputs)
            {
                var errno = CreatePipe(out outputRead, out outputWrite);
                if (errno != 0)
                {
                    return errno;
                }

                var movedOutput = MoveAboveMinimum(outputWrite, out errno);
                if (errno != 0)
                {
                    return errno;
                }

                outputWrite = movedOutput;
                errno = CreatePipe(out errorRead, out errorWrite);
                if (errno != 0)
                {
                    return errno;
                }

                var movedError = MoveAboveMinimum(errorWrite, out errno);
                if (errno != 0)
                {
                    return errno;
                }

                errorWrite = movedError;
            }

            fileActions = Marshal.AllocHGlobal(Libc.SpawnFileActionsBytes);
            // posix_spawn_file_actions_* and posix_spawnattr_* return the error number directly (they do not set
            // errno), so capture the return value instead of reading a stale errno.
            var fileActionsError = Libc.SpawnFileActionsInit(fileActions);
            if (fileActionsError != 0)
            {
                return fileActionsError;
            }

            fileActionsInitialized = true;
            if (spec.InputFileDescriptor >= 0)
            {
                var duplicateError = Libc.SpawnFileActionsAddDuplicate(fileActions, spec.InputFileDescriptor, Libc.StandardInput);
                if (duplicateError != 0)
                {
                    return duplicateError;
                }
            }
            else if (spec.RedirectInput)
            {
                var duplicateError = Libc.SpawnFileActionsAddDuplicate(fileActions, inputRead, Libc.StandardInput);
                if (duplicateError != 0)
                {
                    return duplicateError;
                }
            }

            if (spec.RedirectOutputs)
            {
                var outputError = Libc.SpawnFileActionsAddDuplicate(fileActions, outputWrite, 1);
                if (outputError != 0)
                {
                    return outputError;
                }

                var errorError = Libc.SpawnFileActionsAddDuplicate(fileActions, errorWrite, 2);
                if (errorError != 0)
                {
                    return errorError;
                }
            }

            // The child inherits no descriptor of scripts beyond its stdio (no fd-passing API exists). This runs after
            // the dup2 actions so the duplicated 0/1/2 survive; glibc 2.34 provides addclosefrom_np.
            var closeFromError = Libc.SpawnFileActionsAddCloseFrom(fileActions, 3);
            if (closeFromError != 0)
            {
                return closeFromError;
            }

            if (spec.WorkingDirectory is { } workingDirectory)
            {
                var directoryError = Libc.SpawnFileActionsAddChangeDirectory(
                    fileActions,
                    NativeText.ToNullTerminated(workingDirectory));
                if (directoryError != 0)
                {
                    return directoryError;
                }
            }

            attributes = Marshal.AllocHGlobal(Libc.SpawnAttributesBytes);
            var attributesError = Libc.SpawnAttributesInit(attributes);
            if (attributesError != 0)
            {
                return attributesError;
            }

            attributesInitialized = true;
            short flags = Libc.SpawnSetSignalDefault | Libc.SpawnSetSignalMask;
            if (spec.OwnProcessGroup)
            {
                flags |= Libc.SpawnSetProcessGroup;
                var processGroupError = Libc.SpawnAttributesSetProcessGroup(attributes, 0);
                if (processGroupError != 0)
                {
                    return processGroupError;
                }
            }

            signalMask = Marshal.AllocHGlobal(Libc.SignalSetBytes);
            signalDefault = Marshal.AllocHGlobal(Libc.SignalSetBytes);
            // sigset_t is a fixed-size value type: zero it before sigemptyset (defensive against a dirty buffer).
            Clear(signalMask, Libc.SignalSetBytes);
            Clear(signalDefault, Libc.SignalSetBytes);
            if (Libc.SignalSetEmpty(new Span<byte>((void*)signalMask, Libc.SignalSetBytes)) != 0
                || Libc.SignalSetEmpty(new Span<byte>((void*)signalDefault, Libc.SignalSetBytes)) != 0)
            {
                return Marshal.GetLastPInvokeError();
            }

            // .NET ignores SIGPIPE in the parent; a child must start with the POSIX default so a closed pipe kills
            // it (yes | head -1). Every other disposition is left alone: a caught handler is reset by exec, and an
            // ignored SIGINT (background job) must stay ignored.
            if (Libc.SignalSetAdd(new Span<byte>((void*)signalDefault, Libc.SignalSetBytes), 13) != 0)
            {
                return Marshal.GetLastPInvokeError();
            }

            var signalMaskError = Libc.SpawnAttributesSetSignalMask(attributes, signalMask);
            if (signalMaskError != 0)
            {
                return signalMaskError;
            }

            var signalDefaultError = Libc.SpawnAttributesSetSignalDefault(attributes, signalDefault);
            if (signalDefaultError != 0)
            {
                return signalDefaultError;
            }

            var flagsError = Libc.SpawnAttributesSetFlags(attributes, flags);
            if (flagsError != 0)
            {
                return flagsError;
            }

            var spawnError = Libc.PosixSpawn(
                out var processIdentifier,
                NativeText.ToNullTerminated(spec.ResolvedPath),
                fileActions,
                attributes,
                arguments.Pointer,
                environment.Pointer);
            if (spawnError != 0)
            {
                return spawnError;
            }

            // The child owns the duplicated ends now; the parent keeps only its own ends.
            CloseAndForget(ref inputRead);
            CloseAndForget(ref outputWrite);
            CloseAndForget(ref errorWrite);

            var standardInput = inputWrite >= 0 ? CreateTextWriter(inputWrite) : null;
            inputWrite = -1;
            var standardOutput = outputRead >= 0 ? CreateReader(outputRead) : null;
            outputRead = -1;
            var standardError = errorRead >= 0 ? CreateReader(errorRead) : null;
            errorRead = -1;
            child = new SpawnedChild(processIdentifier, standardInput, standardOutput, standardError);
            return 0;
        }
        finally
        {
            CloseAndForget(ref inputRead);
            CloseAndForget(ref inputWrite);
            CloseAndForget(ref outputRead);
            CloseAndForget(ref outputWrite);
            CloseAndForget(ref errorRead);
            CloseAndForget(ref errorWrite);
            if (attributesInitialized)
            {
                _ = Libc.SpawnAttributesDestroy(attributes);
            }

            if (fileActionsInitialized)
            {
                _ = Libc.SpawnFileActionsDestroy(fileActions);
            }

            if (attributes != 0)
            {
                Marshal.FreeHGlobal(attributes);
            }

            if (fileActions != 0)
            {
                Marshal.FreeHGlobal(fileActions);
            }

            if (signalMask != 0)
            {
                Marshal.FreeHGlobal(signalMask);
            }

            if (signalDefault != 0)
            {
                Marshal.FreeHGlobal(signalDefault);
            }
        }
    }

    /// <summary>
    /// Blocks until <paramref name="processIdentifier"/> exits and returns its code (128 + signal when it was killed).
    /// Returns 0 on success or the errno of a wait failure; EINTR is retried.
    /// </summary>
    public static int TryWait(int processIdentifier, out int exitCode)
    {
        exitCode = -1;
        while (true)
        {
            var result = Libc.WaitForProcess(processIdentifier, out var status, 0);
            if (result == processIdentifier)
            {
                exitCode = DecodeWaitStatus(status);
                return 0;
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno == Errno.Interrupted)
            {
                continue;
            }

            return errno;
        }
    }

    /// <summary>waitpid status: WIFEXITED → the exit code; WIFSIGNALED → 128 + the signal (bash convention).</summary>
    internal static int DecodeWaitStatus(int status)
    {
        var signal = status & 0x7F;
        return signal == 0 ? (status >> 8) & 0xFF : 128 + signal;
    }

    private static int CreatePipe(out int readFileDescriptor, out int writeFileDescriptor)
    {
        Span<int> descriptors = stackalloc int[2];
        if (Libc.CreatePipe(descriptors, Libc.OpenCloseOnExec) != 0)
        {
            readFileDescriptor = -1;
            writeFileDescriptor = -1;
            return Marshal.GetLastPInvokeError();
        }

        readFileDescriptor = descriptors[0];
        writeFileDescriptor = descriptors[1];
        return 0;
    }

    /// <summary>Moves a child end to a descriptor ≥ 10, so 0/1/2 file actions cannot clobber one another.</summary>
    private static int MoveAboveMinimum(int fileDescriptor, out int errno)
    {
        if (fileDescriptor >= MinimumChildDescriptor)
        {
            errno = 0;
            return fileDescriptor;
        }

        // F_DUPFD_CLOEXEC, not F_DUPFD: a plain duplicate loses FD_CLOEXEC, and the duplicate would survive exec
        // (the AOT binary has only a few descriptors, so this path is always taken there — audit F1).
        var moved = Libc.FileControl(fileDescriptor, Libc.DuplicateAboveCloseOnExec, MinimumChildDescriptor);
        if (moved < 0)
        {
            // Leave the source open: the caller still owns it and its finally closes it exactly once. Closing it
            // here as well was a double close (and a race with another thread's descriptor) (audit F6).
            errno = Marshal.GetLastPInvokeError();
            return -1;
        }

        errno = 0;
        _ = Libc.Close(fileDescriptor);
        return moved;
    }

    private static StreamWriter CreateTextWriter(int fileDescriptor)
    {
        var stream = new FileStream(new SafeFileHandle((nint)fileDescriptor, ownsHandle: true), FileAccess.Write, 0);
        return new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
    }

    private static Stream CreateReader(int fileDescriptor) =>
        new FileStream(new SafeFileHandle((nint)fileDescriptor, ownsHandle: true), FileAccess.Read, 0);

    private static void CloseAndForget(ref int fileDescriptor)
    {
        if (fileDescriptor < 0)
        {
            return;
        }

        _ = Libc.Close(fileDescriptor);
        fileDescriptor = -1;
    }

    private static void Clear(nint address, int count)
    {
        var span = new Span<byte>((void*)address, count);
        span.Clear();
    }

    /// <summary>NUL-terminated UTF-8 strings and a NUL-terminated pointer array, freed on dispose.</summary>
    private sealed class Utf8Pointers : IDisposable
    {
        private readonly List<nint> strings = [];

        private Utf8Pointers(nint pointer, int count)
        {
            Pointer = pointer;
            Count = count;
        }

        public nint Pointer { get; }

        public int Count { get; }

        public static Utf8Pointers FromStrings(IReadOnlyList<string> values)
        {
            var pointer = Marshal.AllocHGlobal((values.Count + 1) * nint.Size);
            var block = new Utf8Pointers(pointer, values.Count);
            try
            {
                for (var index = 0; index < values.Count; index++)
                {
                    var bytes = Encoding.UTF8.GetBytes(values[index]);
                    var stringPointer = Marshal.AllocHGlobal(bytes.Length + 1);
                    block.strings.Add(stringPointer);
                    Marshal.Copy(bytes, 0, stringPointer, bytes.Length);
                    Marshal.WriteByte(stringPointer, bytes.Length, 0);
                    Marshal.WriteIntPtr(pointer, index * nint.Size, stringPointer);
                }

                Marshal.WriteIntPtr(pointer, values.Count * nint.Size, 0);
                return block;
            }
            catch
            {
                block.Dispose();
                throw;
            }
        }

        public static Utf8Pointers FromBlock(byte[] block)
        {
            var entries = new List<(int Start, int Length)>();
            var position = 0;
            while (position < block.Length)
            {
                var end = Array.IndexOf(block, (byte)0, position);
                if (end < 0)
                {
                    end = block.Length;
                }

                if (end > position)
                {
                    entries.Add((position, end - position));
                }

                position = end + 1;
            }

            var pointer = Marshal.AllocHGlobal((entries.Count + 1) * nint.Size);
            var result = new Utf8Pointers(pointer, entries.Count);
            try
            {
                for (var index = 0; index < entries.Count; index++)
                {
                    var (start, length) = entries[index];
                    var stringPointer = Marshal.AllocHGlobal(length + 1);
                    result.strings.Add(stringPointer);
                    Marshal.Copy(block, start, stringPointer, length);
                    Marshal.WriteByte(stringPointer, length, 0);
                    Marshal.WriteIntPtr(pointer, index * nint.Size, stringPointer);
                }

                Marshal.WriteIntPtr(pointer, entries.Count * nint.Size, 0);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var pointer in strings)
            {
                Marshal.FreeHGlobal(pointer);
            }

            strings.Clear();
            Marshal.FreeHGlobal(Pointer);
        }
    }
}
