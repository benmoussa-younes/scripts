namespace Scripts.Runtime.Platform;

/// <summary>Linux (x86-64) errno values.</summary>
internal static class Errno
{
    public const int NotPermitted = 1;
    public const int NoSuchEntry = 2;
    public const int NoSuchProcess = 3;
    public const int Interrupted = 4;
    public const int BadFileDescriptor = 9;
    public const int TryAgain = 11;
    public const int AccessDenied = 13;
    public const int AlreadyExists = 17;
    public const int CrossDevice = 18;
    public const int NotADirectory = 20;
    public const int IsADirectory = 21;
    public const int InvalidArgument = 22;
    public const int BrokenPipe = 32;
    public const int RangeExceeded = 34;
    public const int NameTooLong = 36;
    public const int DirectoryNotEmpty = 39;
    public const int TooManyLinks = 40;
    public const int NotSupported = 95;
}
