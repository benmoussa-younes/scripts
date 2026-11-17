using System.Runtime.InteropServices;

namespace Scripts.Runtime.Platform;

/// <summary>
/// Outcome of <see cref="StandardInputSource.Read"/>: the bytes collected from file descriptor 0 and, when the stream
/// could not be read, a non-empty English <see cref="FailureReason"/> (empty on success).
/// </summary>
public sealed record StandardInputReadResult(byte[] Bytes, string FailureReason)
{
    /// <summary>True when the whole stream was read (an empty stream included).</summary>
    public bool Succeeded => FailureReason.Length == 0;
}

/// <summary>
/// Reads the script from the standard input (<c>scripts run -</c> / <c>scripts check -</c>, DESIGN §9), before any interpreter
/// starts. Raw <c>read(2)</c> on file descriptor 0 through <see cref="Libc.Read"/>: no <see cref="Console"/> and no
/// <see cref="FileStream"/>, which would fall back to another source and block when the descriptor is closed.
/// <para>
/// The result is reported, never thrown: the CLI turns a failure into <c>SCRIPTS-CLI-0010</c> (code 252). A standard input
/// **closed on entry** is an **empty script**, never a failure — the same rule as <c>io.read_standard_input</c>
/// (DESIGN §9, SPEC-STDLIB §7.10). A script delivered in pieces is read to end of file; a stream that stalls (a closed
/// descriptor a runtime left open, a writer that never writes) counts as ended after <see cref="ReadTimeout"/>.
/// </para>
/// <para>
/// At most <c>maximumBytes</c> are collected, then reading stops even if the stream continues: the caller passes
/// <c>Limits.MaximumSourceBytes + 1</c> so that the loader can report <c>SCRIPTS-PARSE-0004</c> (source too large).
/// </para>
/// </summary>
public static class StandardInputSource
{
    /// <summary>
    /// Bytes requested by one <c>read(2)</c>. A pipe delivers at most 64 KiB at once (the kernel's buffer), so a single
    /// read of a piped script asks for exactly what the writer put there, while a regular file answers with the whole
    /// chunk. The loader never asks for more than the source limit.
    /// </summary>
    private const int OneReadBytes = 64 * 1024;

    /// <summary>Initial buffer of the collected script (grows as needed, up to the source limit).</summary>
    private const int InitialCapacity = 64 * 1024;



    /// <summary><c>S_IFMT</c> and the file types the reader distinguishes.</summary>
    private const ushort FileTypeMask = 0xF000;
    private const ushort DirectoryType = 0x4000;
    private const ushort CharacterDevice = 0x2000;

    /// <summary>The inode the kernel assigns to <c>/dev/urandom</c> (a character device with a zero device major).</summary>
    private const ulong UrandomInode = 9;

    /// <summary>The <c>/proc</c> entry of descriptor 0; it exists only while the descriptor is open.</summary>
    private const string StandardInputPath = "/proc/self/fd/0";

    /// <summary><c>/proc/self/fd/0</c> as a NUL-terminated UTF-8 path (<c>statx(2)</c> reports the open file itself).</summary>
    private static readonly byte[] StandardInputPathBytes = "/proc/self/fd/0\0"u8.ToArray();

    /// <summary>
    /// How long the reader waits for a stream that has delivered **nothing**. A script delivered by a pipe, a file or a
    /// here-document answers at once; the delay only bounds a stream that never starts (a closed descriptor a runtime
    /// left open), which then counts as ended — an empty script (DESIGN §9). Once a script has started, it is read to
    /// end of file without a deadline (a writer may pause).
    /// </summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Reads file descriptor 0 up to <paramref name="maximumBytes"/> bytes, to end of file. A closed descriptor yields
    /// zero bytes and success; a descriptor that cannot be read (a directory, EISDIR; any other error) yields a
    /// non-empty <see cref="StandardInputReadResult.FailureReason"/> (SCRIPTS-CLI-0010).
    /// </summary>
    /// <param name="maximumBytes">Strict upper bound of the returned length; 0 reads nothing and succeeds.</param>
    public static StandardInputReadResult Read(int maximumBytes)
    {
        if (maximumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), maximumBytes, "the maximum byte count cannot be negative");
        }

        if (IsRuntimeOwnedDescriptor(Libc.StandardInput))
        {
            // The .NET host opened one of its own files on a standard input that was closed before the process
            // started; those descriptors carry FD_CLOEXEC, unlike a shell redirection the user wrote. The stream is
            // absent (DESIGN §9), never a script.
            return new StandardInputReadResult([], string.Empty);
        }

        if (IsRuntimeRandomStream())
        {
            // Older hosts leave /dev/urandom instead of a regular file; the inode check keeps that case covered.
            return new StandardInputReadResult([], string.Empty);
        }

        if (IsDirectory())
        {
            // The one stream the specification reports (SCRIPTS-CLI-0010): a person who redirects a directory made a mistake.
            return new StandardInputReadResult([], Reason(Errno.IsADirectory));
        }

        var collected = new List<byte>(Math.Min(maximumBytes, InitialCapacity));
        var reading = new Reading(collected, maximumBytes);
        var reader = new Thread(reading.DrainToEndOfFile) { IsBackground = true, Name = "scripts standard input" };
        reader.Start();

        // The wait is bounded: a blocked read(2) on an abandoned thread never keeps the process alive (DESIGN §9). The
        // bound only applies to a stream that has delivered nothing (a runtime leftover on a closed descriptor 0);
        // once the script has started, its remaining bytes are read to end of file, however long the writer pauses.
        var finished = reading.Finished.Wait(ReadTimeout);
        if (!finished && collected.Count == 0)
        {
            // Nothing arrived at all: the stream is not a script now. The read is abandoned (a writer that starts later
            // is not waited for, and the background thread never keeps the process alive).
            return new StandardInputReadResult([], string.Empty);
        }

        if (!finished)
        {
            reading.Finished.Wait();
        }

        return reading.Failure is { } failure
            ? new StandardInputReadResult([], failure)
            : new StandardInputReadResult([.. collected], string.Empty);
    }

    /// <summary>
    /// One read of descriptor 0 to end of file, on its own thread, so that a stalled <c>read(2)</c> is bounded. The
    /// script is read in large chunks until the bytes already available are exhausted: a source file is read to end
    /// of file (the last <c>read(2)</c> returns 0), while a <c>read(2)</c> that would wait for a writer is never
    /// started, so nothing is consumed beyond the script (DESIGN §9). A descriptor in non-blocking mode is handled the
    /// same way (<c>EAGAIN</c> ends the script), never retried in a spin.
    /// </summary>
    private sealed class Reading(List<byte> collected, int maximumBytes)
    {
        public ManualResetEventSlim Finished { get; } = new(false);

        public string? Failure { get; private set; }

        public void DrainToEndOfFile()
        {
            try
            {
                var buffer = new byte[Math.Min(maximumBytes - collected.Count, OneReadBytes)];
                while (collected.Count < maximumBytes)
                {
                    var bytesRead = Libc.Read(Libc.StandardInput, buffer, (nuint)buffer.Length);
                    if (bytesRead > 0)
                    {
                        var accepted = Math.Min((int)bytesRead, maximumBytes - collected.Count);
                        collected.AddRange(buffer.AsSpan(0, accepted).ToArray());
                        continue;
                    }

                    if (bytesRead == 0)
                    {
                        return;
                    }

                    var errno = Marshal.GetLastPInvokeError();
                    if (errno == Errno.Interrupted)
                    {
                        continue;
                    }

                    // EAGAIN (a non-blocking descriptor: nothing more is available now) and EBADF (a closed standard
                    // input) are end of stream: the caller's empty-script rule (DESIGN §9). No descriptor at all is
                    // ever put in a state where a read would spin.
                    if (errno == Errno.TryAgain || errno == Errno.BadFileDescriptor)
                    {
                        return;
                    }

                    Failure = Reason(errno);
                    return;
                }
            }
            finally
            {
                Finished.Set();
            }
        }
    }

    /// <summary>
    /// Substitutes the standard input of an in-process run (library use, tests): <paramref name="standardInputPath"/>
    /// is opened read-only and becomes descriptor 0. The CLI never calls it; it exists so that the reader can be tested
    /// without changing the test process's own descriptor table.
    /// </summary>
    /// <returns>False when the path cannot be opened.</returns>
    public static bool UseStandardInputFrom(string standardInputPath)
    {
        var descriptor = Libc.Open(System.Text.Encoding.UTF8.GetBytes(standardInputPath + "\0"), Libc.OpenReadOnly, 0);
        if (descriptor < 0)
        {
            return false;
        }

        if (Libc.DuplicateTo(descriptor, Libc.StandardInput) != 0)
        {
            Libc.Close(descriptor);
            return false;
        }

        Libc.Close(descriptor);
        return true;
    }

    /// <summary>
    /// True when a descriptor was opened by the runtime with FD_CLOEXEC. A shell redirection (<c>&lt; file</c>,
    /// <c>&lt; /dev/null</c>, a pipe) never carries that flag: the flag is cleared by <c>exec</c> and not set by the
    /// shell, so the check distinguishes a user stream from the host's own startup leftovers on a closed descriptor 0.
    /// </summary>
    internal static bool IsRuntimeOwnedDescriptor(int descriptor)
    {
        var flags = Libc.FileControl(descriptor, Libc.GetFileDescriptorFlags, 0);
        return flags >= 0 && (flags & Libc.FileDescriptorCloseOnExec) != 0;
    }

    /// <summary>
    /// True when descriptor 0 is the kernel's random device. The host leaves that stream on a descriptor 0 that was
    /// closed before the process started; its <c>statx(2)</c> identity is a character device with inode 9 and a zero
    /// device major.
    /// </summary>
    internal static bool IsRuntimeRandomStream()
    {
        if (!TryStatStandardInput(out var status))
        {
            return false;
        }

        return (status.Mode & FileTypeMask) == CharacterDevice
            && status.InodeNumber == UrandomInode
            && status.DeviceMajor == 0;
    }

    /// <summary>True when descriptor 0 is a directory (the path of a FIFO or of a deleted file keeps its own type).</summary>
    private static bool IsDirectory() =>
        TryStatStandardInput(out var status) && (status.Mode & FileTypeMask) == DirectoryType;

    /// <summary>Stats the file descriptor 0 is open on, through its <c>/proc</c> entry.</summary>
    private static bool TryStatStandardInput(out StatxBuffer status)
    {
        status = new StatxBuffer();
        return Libc.StatX(Libc.CurrentDirectoryFileDescriptor, StandardInputPathBytes, 0, Libc.StatxBasicStatistics, out status) == 0;
    }

    /// <summary>English reason of <c>SCRIPTS-CLI-0010</c> ("cannot read the script from standard input ({reason})").</summary>
    private static string Reason(int errno) => errno switch
    {
        Errno.IsADirectory => "the standard input is a directory",
        Errno.AccessDenied => "permission denied",
        Errno.NotPermitted => "permission denied",
        Errno.InvalidArgument => "the standard input cannot be read",
        // Anything else (EIO for a background terminal, an unknown errno…): the system wording keeps the cause visible.
        _ => "system error: " + Marshal.GetPInvokeErrorMessage(errno),
    };
}
