using System.Runtime.InteropServices;
using Scripts.Runtime.Platform;

namespace Scripts.Runtime.Processes;

/// <summary>
/// The child's standard input opened from a regular file (DESIGN §7.3). The shared <see cref="RegularFileOpener"/>
/// validates it (O_PATH preview, readable open with <c>O_NONBLOCK|O_NOCTTY|O_CLOEXEC</c>, statx on the opened
/// descriptor); scripts never reads the content. After validation the descriptor is moved above the standard descriptors
/// (3+) so a closed scripts stdin cannot make the file itself fd 0, where <c>adddup2(fd, 0)</c> would be a no-op and
/// <c>O_CLOEXEC</c> would close it at exec. The descriptor is closed by <see cref="Dispose"/> on every path.
/// </summary>
internal sealed class StandardInputFile : IDisposable
{
    private int descriptor;

    private StandardInputFile(int descriptor) => this.descriptor = descriptor;

    public int Descriptor => descriptor;

    /// <summary>
    /// Opens <paramref name="standardInputFile"/>, or returns null for the empty path ("none"). A request that also
    /// carries a <c>standard_input</c> string is refused defensively: the two input fields are mutually exclusive.
    /// </summary>
    public static StandardInputFile? Prepare(string program, string standardInput, string standardInputFile, Action checkInterrupted)
    {
        if (standardInputFile.Length == 0)
        {
            return null;
        }

        if (standardInput.Length != 0)
        {
            throw ProcessFailureException.InvalidArgument(program, "standard_input and standard_input_file cannot both be set");
        }

        if (PathText.ContainsNul(standardInputFile))
        {
            throw ProcessFailureException.InvalidArgument(program, "the standard input file contains a NUL character");
        }

        var opened = RegularFileOpener.OpenReadable(NativeText.ToNullTerminated(standardInputFile), checkInterrupted);
        if (!opened.IsOpened)
        {
            throw ToProcessFailure(opened, program);
        }

        var fileDescriptor = opened.Descriptor;
        try
        {
            fileDescriptor = MoveAboveStandardDescriptors(fileDescriptor, program);
            return new StandardInputFile(fileDescriptor);
        }
        catch
        {
            _ = Libc.Close(fileDescriptor);
            throw;
        }
    }

    /// <summary>Maps the shared opener's fault to the process error family (one classification, two callers).</summary>
    private static ProcessFailureException ToProcessFailure(RegularFileOpenResult opened, string program) =>
        opened.Fault switch
        {
            RegularFileFault.Directory => ProcessFailureException.FromStdinErrno(Errno.IsADirectory, program),
            RegularFileFault.NotRegular => ProcessFailureException.Other(program, "the standard input is not a regular file"),
            _ => ProcessFailureException.FromStdinErrno(opened.Errno, program),
        };

    /// <summary>
    /// A descriptor 0/1/2 would collide with the child's stdio actions: with fd 0 the duplicate is a no-op;
    /// with fd 1 (<c>run_inherit</c>) the output redirection would overwrite it; and <c>O_CLOEXEC</c> would close
    /// it at exec. <c>F_DUPFD_CLOEXEC</c> moves it to >= 3 and keeps the flag for the parent copy.
    /// </summary>
    internal static int MoveAboveStandardDescriptors(int fileDescriptor, string program)
    {
        if (fileDescriptor > 2)
        {
            return fileDescriptor;
        }

        var moved = Libc.FileControl(fileDescriptor, Libc.DuplicateAboveCloseOnExec, 3);
        if (moved < 0)
        {
            throw ProcessFailureException.FromStdinErrno(Marshal.GetLastPInvokeError(), program);
        }

        _ = Libc.Close(fileDescriptor);
        return moved;
    }

    public void Dispose()
    {
        if (descriptor < 0)
        {
            return;
        }

        _ = Libc.Close(descriptor);
        descriptor = -1;
    }
}
