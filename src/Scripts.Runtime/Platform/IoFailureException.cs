using System.Runtime.InteropServices;

namespace Scripts.Runtime.Platform;

internal static class IoErrorKinds
{
    public const string NotFound = "not_found";
    public const string PermissionDenied = "permission_denied";
    public const string AlreadyExists = "already_exists";
    public const string NotADirectory = "not_a_directory";
    public const string IsADirectory = "is_a_directory";
    public const string DirectoryNotEmpty = "directory_not_empty";
    public const string InvalidPath = "invalid_path";
    public const string InvalidEncoding = "invalid_encoding";
    public const string TooLarge = "too_large";
    public const string CrossDevice = "cross_device";
    public const string SameFile = "same_file";
    public const string InvalidArgument = "invalid_argument";
    public const string EndOfFile = "end_of_file";
    public const string Other = "other";
}

/// <summary>Expected filesystem failure carrying the data of an error; stdlib wrappers turn it into an `error` value.</summary>
internal sealed class IoFailureException(string kind, string subject, string reason) : Exception(reason)
{
    public string Kind { get; } = kind;

    public string Subject { get; } = subject;

    public string Reason { get; } = reason;

    public string UserMessage => Subject.Length == 0 ? Reason : $"{Subject} : {Reason}";

    public static IoFailureException FromErrno(int errno, string subject) => errno switch
    {
        Errno.NoSuchEntry => new(IoErrorKinds.NotFound, subject, "not found"),
        Errno.AccessDenied or Errno.NotPermitted => new(IoErrorKinds.PermissionDenied, subject, "permission denied"),
        Errno.AlreadyExists => new(IoErrorKinds.AlreadyExists, subject, "already exists"),
        Errno.NotADirectory => new(IoErrorKinds.NotADirectory, subject, "is not a directory"),
        Errno.IsADirectory => new(IoErrorKinds.IsADirectory, subject, "is a directory"),
        Errno.DirectoryNotEmpty => new(IoErrorKinds.DirectoryNotEmpty, subject, "directory not empty"),
        Errno.CrossDevice => new(IoErrorKinds.CrossDevice, subject, "cannot move between file systems"),
        Errno.NameTooLong => new(IoErrorKinds.InvalidPath, subject, "path name too long"),
        Errno.TooManyLinks => new(IoErrorKinds.InvalidPath, subject, "too many symbolic links encountered"),
        _ => new(IoErrorKinds.Other, subject, "system error: " + Marshal.GetPInvokeErrorMessage(errno)),
    };

    public static IoFailureException InvalidPath(string subject, string reason) => new(IoErrorKinds.InvalidPath, subject, reason);

    public static IoFailureException NotARegularFile(string subject) =>
        new(IoErrorKinds.Other, subject, "is not a regular file");

    public static IoFailureException SameFile(string subject) =>
        new(IoErrorKinds.SameFile, subject, "the source and destination refer to the same file");
}
