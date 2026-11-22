using Scripts.Runtime.Platform;

namespace Scripts.Runtime.Processes;

/// <summary>Program resolution shared by process.run/process.run_inherit/process.spawn and which (DESIGN §6): PATH only, never the current directory.</summary>
internal static class ProgramResolver
{
    private const uint AnyExecuteBit = 0x49;

    public static void ValidateProgram(string program)
    {
        if (program.Length == 0)
        {
            throw ProcessFailureException.InvalidArgument(program, "empty program name");
        }

        if (PathText.ContainsNul(program))
        {
            throw ProcessFailureException.InvalidArgument(program, "the program name contains a NUL character");
        }
    }

    public static void ValidateArguments(string program, IReadOnlyList<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (PathText.ContainsNul(argument))
            {
                throw ProcessFailureException.InvalidArgument(program, "an argument contains a NUL character");
            }
        }
    }

    /// <summary>Absolute path to hand to the OS; a program containing '/' is only made absolute, without any check.</summary>
    public static string ResolveForLaunch(string program) =>
        ResolveForLaunch(program, null, Environment.GetEnvironmentVariable("PATH"));

    /// <summary>
    /// Resolves against an explicit launch context: <paramref name="baseDirectory"/> is the effective working
    /// directory (null inherits the process directory) and <paramref name="pathVariable"/> is the PATH of the
    /// effective environment (null means no PATH). A program containing '/' is resolved relative to the base.
    /// </summary>
    public static string ResolveForLaunch(string program, string? baseDirectory, string? pathVariable)
    {
        ValidateProgram(program);
        if (program.Contains('/', StringComparison.Ordinal))
        {
            return MakeAbsolute(program, baseDirectory);
        }

        return SearchPath(program, pathVariable) ?? throw ProcessFailureException.NotFound(program, "not found in PATH");
    }

    public static string Which(string programName)
    {
        ValidateProgram(programName);
        if (!programName.Contains('/', StringComparison.Ordinal))
        {
            return SearchPath(programName, Environment.GetEnvironmentVariable("PATH"))
                ?? throw ProcessFailureException.NotFound(programName, "not found in PATH");
        }

        var absolutePath = MakeAbsolute(programName, null);
        var errno = NativeFileSystem.TryGetStatus(NativeText.ToNullTerminated(absolutePath), true, out var status);
        if (errno == 0)
        {
            return IsExecutableFile(status)
                ? absolutePath
                : throw new ProcessFailureException(ProcessErrorKinds.PermissionDenied, programName, "is not an executable file");
        }

        return errno is Errno.AccessDenied or Errno.NotPermitted
            ? throw new ProcessFailureException(ProcessErrorKinds.PermissionDenied, programName, "access denied")
            : throw ProcessFailureException.NotFound(programName, "program not found");
    }

    private static string? SearchPath(string program, string? pathVariable)
    {
        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        foreach (var directory in pathVariable.Split(':'))
        {
            if (directory.Length == 0 || directory[0] != '/')
            {
                continue;
            }

            var candidate = PathText.Join(directory, program);
            if (NativeFileSystem.TryGetStatus(NativeText.ToNullTerminated(candidate), true, out var status) == 0
                && IsExecutableFile(status))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsExecutableFile(NativeFileStatus status) => status.IsRegularFile && (status.Mode & AnyExecuteBit) != 0;

    private static string MakeAbsolute(string program, string? baseDirectory)
    {
        if (program[0] == '/')
        {
            return program;
        }

        if (baseDirectory is not null)
        {
            return PathText.Join(baseDirectory, program);
        }

        if (NativeFileSystem.TryGetCurrentDirectory(out var currentDirectory) != 0)
        {
            throw ProcessFailureException.NotFound(program, "current directory inaccessible");
        }

        return PathText.Join(NativeText.DecodeLenient(currentDirectory), program);
    }
}
