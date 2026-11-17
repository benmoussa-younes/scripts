using System.Globalization;
using Scripts.Runtime.Platform;

namespace Scripts.Runtime.Processes;

internal static class ProcessErrorKinds
{
    public const string NotFound = "not_found";
    public const string NotADirectory = "not_a_directory";
    public const string IsADirectory = "is_a_directory";
    public const string PermissionDenied = "permission_denied";
    public const string InvalidPath = "invalid_path";
    public const string InvalidArgument = "invalid_argument";
    public const string TimedOut = "timed_out";
    public const string InvalidEncoding = "invalid_encoding";
    public const string Other = "other";
}

/// <summary>Expected process failure carrying the data of an error; stdlib wrappers turn it into an `error` value.</summary>
internal sealed class ProcessFailureException(string kind, string program, string reason) : Exception(reason)
{
    public string Kind { get; } = kind;

    public string Program { get; } = program;

    public string Reason { get; } = reason;

    public string UserMessage => Program.Length == 0 ? Reason : $"{Program} : {Reason}";

    public static ProcessFailureException InvalidArgument(string program, string reason) =>
        new(ProcessErrorKinds.InvalidArgument, program, reason);

    public static ProcessFailureException NotFound(string program, string reason) =>
        new(ProcessErrorKinds.NotFound, program, reason);

    public static ProcessFailureException NotADirectory(string program, string reason) =>
        new(ProcessErrorKinds.NotADirectory, program, reason);

    public static ProcessFailureException PermissionDenied(string program, string reason) =>
        new(ProcessErrorKinds.PermissionDenied, program, reason);

    public static ProcessFailureException Other(string program, string reason) =>
        new(ProcessErrorKinds.Other, program, reason);

    /// <summary>
    /// The launch-time classification of a standard-input file (DESIGN §7.3), aligned with
    /// <see cref="IoFailureException.FromErrno"/>: a missing path is <c>not_found</c>, a path component that is not a
    /// directory is <c>not_a_directory</c>, an unreadable file is <c>permission_denied</c>, a directory is
    /// <c>is_a_directory</c> (raised by the caller from the descriptor), a too-long path or a symbolic-link loop is
    /// <c>invalid_path</c>, and anything else is <c>other</c> with the errno in the message.
    /// </summary>
    public static ProcessFailureException FromStdinErrno(int errno, string program) => errno switch
    {
        Errno.NoSuchEntry => NotFound(program, "standard input file not found"),
        Errno.NotADirectory => NotADirectory(program, "a standard input file path component is not a directory"),
        Errno.IsADirectory => new(ProcessErrorKinds.IsADirectory, program, "the standard input file is a directory"),
        Errno.AccessDenied or Errno.NotPermitted => PermissionDenied(program, "standard input file not readable"),
        Errno.NameTooLong => new(ProcessErrorKinds.InvalidPath, program, "standard input file path name too long"),
        Errno.TooManyLinks => new(ProcessErrorKinds.InvalidPath, program, "too many symbolic links in the standard input file path"),
        _ => new(
            ProcessErrorKinds.Other,
            program,
            "the standard input file cannot be opened (errno " + errno.ToString(CultureInfo.InvariantCulture) + ")"),
    };

    public static ProcessFailureException InvalidEncoding(string program) =>
        new(ProcessErrorKinds.InvalidEncoding, program, "the output is not valid UTF-8 text");

    public static ProcessFailureException TimedOut(string program, long timeoutMilliseconds) =>
        new(ProcessErrorKinds.TimedOut, program, $"timeout of {timeoutMilliseconds.ToString(CultureInfo.InvariantCulture)} ms exceeded");

    /// <summary>SPEC-STDLIB §7.3 table: ENOENT/ENOTDIR → not_found, EACCES/EPERM → permission_denied, anything else → other.</summary>
    public static ProcessFailureException FromErrno(int errno, string program) => errno switch
    {
        Errno.NoSuchEntry or Errno.NotADirectory => new(ProcessErrorKinds.NotFound, program, "program not found"),
        Errno.AccessDenied or Errno.NotPermitted => new(ProcessErrorKinds.PermissionDenied, program, "execution not permitted"),
        _ => new(ProcessErrorKinds.Other, program, "cannot launch (errno " + errno.ToString(CultureInfo.InvariantCulture) + ")"),
    };
}
