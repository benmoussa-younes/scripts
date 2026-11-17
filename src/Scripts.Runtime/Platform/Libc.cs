using System.Runtime.InteropServices;

namespace Scripts.Runtime.Platform;

[StructLayout(LayoutKind.Sequential)]
internal struct StatxTimestamp
{
    public long Seconds;
    public uint Nanoseconds;
    public int Reserved;
}

/// <summary>Linux (x86-64) struct statx (256 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Size = 256)]
internal struct StatxBuffer
{
    public uint Mask;
    public uint BlockSize;
    public ulong Attributes;
    public uint LinkCount;
    public uint OwnerIdentifier;
    public uint GroupIdentifier;
    public ushort Mode;
    public ushort Spare;
    public ulong InodeNumber;
    public ulong SizeBytes;
    public ulong BlockCount;
    public ulong AttributesMask;
    public StatxTimestamp AccessTime;
    public StatxTimestamp BirthTime;
    public StatxTimestamp ChangeTime;
    public StatxTimestamp ModificationTime;
    public uint RawDeviceMajor;
    public uint RawDeviceMinor;
    public uint DeviceMajor;
    public uint DeviceMinor;
}

/// <summary>Linux (x86-64) struct statvfs (112 bytes): eleven unsigned longs and six spare ints.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StatVfsBuffer
{
    public ulong BlockSize;
    public ulong FragmentSize;
    public ulong Blocks;
    public ulong FreeBlocks;
    public ulong AvailableBlocks;
    public ulong TotalInodes;
    public ulong FreeInodes;
    public ulong AvailableInodes;
    public ulong FileSystemIdentifier;
    public ulong MountFlags;
    public ulong MaximumNameLength;
    public int Spare0;
    public int Spare1;
    public int Spare2;
    public int Spare3;
    public int Spare4;
    public int Spare5;
}

/// <summary>Linux <c>struct pollfd</c> (8 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PollFileDescriptor
{
    public int FileDescriptor;
    public short Events;
    public short ReturnedEvents;
}

/// <summary>glibc struct passwd (64-bit Linux).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PasswdEntry
{
    public nint UserName;
    public nint Password;
    public uint UserIdentifier;
    public uint GroupIdentifier;
    public nint Gecos;
    public nint HomeDirectory;
    public nint Shell;
}

/// <summary>glibc struct group (64-bit Linux).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GroupEntry
{
    public nint GroupName;
    public nint Password;
    public uint GroupIdentifier;
    public nint Members;
}

/// <summary>Source-generated libc bindings. Path arguments are NUL-terminated UTF-8 byte spans.</summary>
internal static partial class Libc
{
    private const string Library = "libc";

    /// <summary>File descriptor of the process standard input (<c>io.read_standard_input</c>).</summary>
    public const int StandardInput = 0;

    public const int CurrentDirectoryFileDescriptor = -100;
    public const int SymbolicLinkNoFollow = 0x100;
    public const uint StatxBasicStatistics = 0x7ff;
    public const uint RenameNoReplace = 1;

    public const int OpenReadOnly = 0;
    public const int OpenWriteOnly = 1;
    public const int OpenReadWrite = 2;
    public const int OpenCreate = 0x40;
    public const int OpenExclusive = 0x80;
    public const int OpenTruncate = 0x200;
    public const int OpenAppend = 0x400;
    public const int OpenNonBlock = 0x800;
    public const int OpenNoCtty = 0x100;
    public const int OpenCloseOnExec = 0x80000;
    public const int OpenDirectoryFlag = 0x10000;
    public const int OpenNoFollow = 0x20000;
    public const int OpenPath = 0x200000;

    /// <summary>statx(2): use the directory file descriptor itself, with the empty path.</summary>
    public const int AtEmptyPath = 0x1000;

    /// <summary>statx(2): the default synchronization mode, as stat(2).</summary>
    public const int AtStatxSyncAsStat = 0x0;

    public const int FlockExclusive = 2;
    public const int FlockNonBlocking = 4;

    public const int RemoveDirectoryAtFlag = 0x200;

    public const uint FileTypeMask = 0xF000;
    public const uint FileTypeRegular = 0x8000;
    public const uint FileTypeDirectory = 0x4000;
    public const uint FileTypeSymbolicLink = 0xA000;
    public const uint FileTypeFifo = 0x1000;
    public const uint FileTypeCharacterDevice = 0x2000;
    public const uint FileTypeBlockDevice = 0x6000;
    public const uint FileTypeSocket = 0xC000;

    public const byte DirectoryEntryTypeUnknown = 0;
    public const byte DirectoryEntryTypeFifo = 1;
    public const byte DirectoryEntryTypeCharacterDevice = 2;
    public const byte DirectoryEntryTypeDirectory = 4;
    public const byte DirectoryEntryTypeBlockDevice = 6;
    public const byte DirectoryEntryTypeRegular = 8;
    public const byte DirectoryEntryTypeSymbolicLink = 10;
    public const byte DirectoryEntryTypeSocket = 12;

    public const int SignalHangUp = 1;
    public const int SignalInterrupt = 2;
    public const int SignalTerminate = 15;

    public const int PathMaximumLength = 4096;

    [LibraryImport(Library, EntryPoint = "statx", SetLastError = true)]
    public static partial int StatX(int directoryFileDescriptor, ReadOnlySpan<byte> path, int flags, uint mask, out StatxBuffer buffer);

    [LibraryImport(Library, EntryPoint = "mkdir", SetLastError = true)]
    public static partial int MakeDirectory(ReadOnlySpan<byte> path, uint mode);

    [LibraryImport(Library, EntryPoint = "rmdir", SetLastError = true)]
    public static partial int RemoveDirectory(ReadOnlySpan<byte> path);

    [LibraryImport(Library, EntryPoint = "unlink", SetLastError = true)]
    public static partial int Unlink(ReadOnlySpan<byte> path);

    [LibraryImport(Library, EntryPoint = "rename", SetLastError = true)]
    public static partial int Rename(ReadOnlySpan<byte> sourcePath, ReadOnlySpan<byte> destinationPath);

    [LibraryImport(Library, EntryPoint = "renameat2", SetLastError = true)]
    public static partial int RenameAt2(
        int sourceDirectoryFileDescriptor,
        ReadOnlySpan<byte> sourcePath,
        int destinationDirectoryFileDescriptor,
        ReadOnlySpan<byte> destinationPath,
        uint flags);

    [LibraryImport(Library, EntryPoint = "chdir", SetLastError = true)]
    public static partial int ChangeDirectory(ReadOnlySpan<byte> path);

    [LibraryImport(Library, EntryPoint = "umask")]
    public static partial uint Umask(uint mask);

    [LibraryImport(Library, EntryPoint = "statvfs", SetLastError = true)]
    public static partial int StatVfs(ReadOnlySpan<byte> path, out StatVfsBuffer buffer);

    [LibraryImport(Library, EntryPoint = "getcwd", SetLastError = true)]
    public static partial nint GetCurrentDirectory(Span<byte> buffer, nuint bufferSize);

    [LibraryImport(Library, EntryPoint = "realpath", SetLastError = true)]
    public static partial nint ResolvePath(ReadOnlySpan<byte> path, Span<byte> resolvedPathBuffer);

    [LibraryImport(Library, EntryPoint = "opendir", SetLastError = true)]
    public static partial nint OpenDirectory(ReadOnlySpan<byte> path);

    [LibraryImport(Library, EntryPoint = "openat", SetLastError = true)]
    public static partial int OpenAt(int directoryFileDescriptor, ReadOnlySpan<byte> path, int flags, uint mode);

    [LibraryImport(Library, EntryPoint = "fdopendir", SetLastError = true)]
    public static partial nint OpenDirectoryFromFileDescriptor(int fileDescriptor);

    [LibraryImport(Library, EntryPoint = "unlinkat", SetLastError = true)]
    public static partial int UnlinkAt(int directoryFileDescriptor, ReadOnlySpan<byte> path, int flags);

    [LibraryImport(Library, EntryPoint = "readdir", SetLastError = true)]
    public static partial nint ReadDirectory(nint directoryStream);

    [LibraryImport(Library, EntryPoint = "closedir")]
    public static partial int CloseDirectory(nint directoryStream);

    [LibraryImport(Library, EntryPoint = "dup2", SetLastError = true)]
    public static partial int DuplicateTo(int fileDescriptor, int destinationFileDescriptor);

    [LibraryImport(Library, EntryPoint = "open", SetLastError = true)]
    public static partial int Open(ReadOnlySpan<byte> path, int flags, uint mode);

    [LibraryImport(Library, EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fileDescriptor);

    [LibraryImport(Library, EntryPoint = "dup", SetLastError = true)]
    public static partial int Duplicate(int fileDescriptor);

    [LibraryImport(Library, EntryPoint = "read", SetLastError = true)]
    public static partial nint Read(int fileDescriptor, Span<byte> buffer, nuint count);

    public const short PollInput = 0x001;
    public const short PollError = 0x008;
    public const short PollHangUp = 0x010;
    public const short PollInvalid = 0x020;

    // termios flags and control-character indexes (Linux/glibc).
    public const uint LocalInterruptSignal = 0x0001; // ISIG
    public const uint LocalCanonical = 0x0002;       // ICANON
    public const uint LocalEcho = 0x0008;            // ECHO
    public const uint LocalEchoErase = 0x0010;       // ECHOE
    public const uint LocalEchoKill = 0x0020;        // ECHOK
    public const uint LocalEchoNewline = 0x0040;     // ECHONL
    public const uint LocalExtended = 0x8000;        // IEXTEN
    public const uint InputFlowControl = 0x0400;     // IXON
    public const int TerminalAttributesBytes = 64;
    public const int TerminalLocalFlagsOffset = 12;
    public const int TerminalControlCharactersOffset = 17;
    public const int TerminalControlIndexMinimum = 6; // VMIN
    public const int TerminalControlIndexTimeout = 5; // VTIME
    public const int SetAttributesNow = 0;            // TCSANOW

    [LibraryImport(Library, EntryPoint = "isatty")]
    public static partial int IsTerminal(int fileDescriptor);

    /// <summary>Fills a 64-byte buffer with <c>struct termios</c> (the kernel writes 60 bytes on x86-64 Linux).</summary>
    [LibraryImport(Library, EntryPoint = "tcgetattr", SetLastError = true)]
    public static partial int GetTerminalAttributes(int fileDescriptor, Span<byte> attributes);

    [LibraryImport(Library, EntryPoint = "tcsetattr", SetLastError = true)]
    public static partial int SetTerminalAttributes(int fileDescriptor, int optionalActions, ReadOnlySpan<byte> attributes);

    /// <summary>poll(2): waits for one of the events, or <paramref name="timeoutMilliseconds"/> (0 = immediate, -1 = infinite).</summary>
    [LibraryImport(Library, EntryPoint = "poll", SetLastError = true)]
    public static partial int Poll(Span<PollFileDescriptor> fileDescriptors, nuint count, int timeoutMilliseconds);

    [LibraryImport(Library, EntryPoint = "write", SetLastError = true)]
    public static partial nint Write(int fileDescriptor, ReadOnlySpan<byte> buffer, nuint count);

    [LibraryImport(Library, EntryPoint = "fchmod", SetLastError = true)]
    public static partial int ChangeFileMode(int fileDescriptor, uint mode);
    [LibraryImport(Library, EntryPoint = "chmod", SetLastError = true)]
    public static partial int ChangeMode(ReadOnlySpan<byte> path, uint mode);

    [LibraryImport(Library, EntryPoint = "symlink", SetLastError = true)]
    public static partial int MakeSymbolicLink(ReadOnlySpan<byte> targetPath, ReadOnlySpan<byte> linkPath);

    [LibraryImport(Library, EntryPoint = "link", SetLastError = true)]
    public static partial int MakeHardLink(ReadOnlySpan<byte> existingPath, ReadOnlySpan<byte> newPath);

    [LibraryImport(Library, EntryPoint = "readlink", SetLastError = true)]
    public static partial nint ReadSymbolicLink(ReadOnlySpan<byte> path, Span<byte> buffer, nuint bufferSize);

    [LibraryImport(Library, EntryPoint = "fsync", SetLastError = true)]
    public static partial int SynchronizeFile(int fileDescriptor);

    [LibraryImport(Library, EntryPoint = "uname", SetLastError = true)]
    public static partial int Uname(Span<byte> buffer);

    [LibraryImport(Library, EntryPoint = "gethostname", SetLastError = true)]
    public static partial int GetHostName(Span<byte> buffer, nuint bufferSize);

    [LibraryImport(Library, EntryPoint = "geteuid")]
    public static partial uint GetEffectiveUserIdentifier();

    [LibraryImport(Library, EntryPoint = "getegid")]
    public static partial uint GetEffectiveGroupIdentifier();

    [LibraryImport(Library, EntryPoint = "getpwuid_r")]
    public static partial int GetPasswdEntryReentrant(uint userIdentifier, out PasswdEntry entry, Span<byte> buffer, nuint bufferSize, out nint result);

    [LibraryImport(Library, EntryPoint = "getpwnam_r")]
    public static partial int GetPasswdEntryByName(ReadOnlySpan<byte> name, out PasswdEntry entry, Span<byte> buffer, nuint bufferSize, out nint result);

    [LibraryImport(Library, EntryPoint = "getgrnam_r")]
    public static partial int GetGroupEntryByName(ReadOnlySpan<byte> name, out GroupEntry entry, Span<byte> buffer, nuint bufferSize, out nint result);

    [LibraryImport(Library, EntryPoint = "chown", SetLastError = true)]
    public static partial int ChangeOwner(ReadOnlySpan<byte> path, uint owner, uint group);

    [LibraryImport(Library, EntryPoint = "lchown", SetLastError = true)]
    public static partial int ChangeOwnerNoFollow(ReadOnlySpan<byte> path, uint owner, uint group);

    [LibraryImport(Library, EntryPoint = "kill", SetLastError = true)]
    public static partial int Kill(int processIdentifier, int signalNumber);

    [LibraryImport(Library, EntryPoint = "sigaction", SetLastError = true)]
    public static partial int InspectSignalAction(int signalNumber, nint newAction, Span<byte> previousAction);

    // --- A2: process groups so that a signal can terminate a whole child subtree ---------------------------

    public const int SignalKill = 9;

    /// <summary>getpgid(2): process group of processIdentifier, or -1 on error (errno through SetLastError).</summary>
    [LibraryImport(Library, EntryPoint = "getpgid", SetLastError = true)]
    public static partial int GetProcessGroup(int processIdentifier);

    /// <summary>killpg(2): sends signalNumber to the whole process group groupIdentifier.</summary>
    [LibraryImport(Library, EntryPoint = "killpg", SetLastError = true)]
    public static partial int KillProcessGroup(int groupIdentifier, int signalNumber);

    // --- P8d: native process launch (posix_spawn, waitpid) ------------------------------------------------

    /// <summary>F_DUPFD_CLOEXEC: duplicate at or above the given minimum and keep FD_CLOEXEC (fcntl(2), Linux).</summary>
    public const int DuplicateAboveCloseOnExec = 1030;

    /// <summary>F_GETFD: read the per-descriptor flags (fcntl(2)).</summary>
    public const int GetFileDescriptorFlags = 1;

    /// <summary>FD_CLOEXEC: the descriptor is closed on exec; the runtime's own startup files carry it.</summary>
    public const int FileDescriptorCloseOnExec = 1;

    /// <summary>POSIX_SPAWN_SETPGROUP: put the child in the process group named by setpgroup (0 = its pid).</summary>
    public const short SpawnSetProcessGroup = 0x02;

    /// <summary>POSIX_SPAWN_SETSIGDEF: reset the listed signals to SIG_DFL before exec.</summary>
    public const short SpawnSetSignalDefault = 0x04;

    /// <summary>POSIX_SPAWN_SETSIGMASK: install the given signal mask before exec.</summary>
    public const short SpawnSetSignalMask = 0x08;

    /// <summary>Size of glibc's opaque <c>posix_spawnattr_t</c> (336 bytes on x86-64); a larger aligned buffer is safe.</summary>
    public const int SpawnAttributesBytes = 512;

    /// <summary>Size of glibc's opaque <c>posix_spawn_file_actions_t</c> (80 bytes on x86-64).</summary>
    public const int SpawnFileActionsBytes = 256;

    /// <summary>Size of glibc's <c>sigset_t</c> (128 bytes on x86-64).</summary>
    public const int SignalSetBytes = 128;

    [LibraryImport(Library, EntryPoint = "posix_spawn", SetLastError = true)]
    public static partial int PosixSpawn(
        out int processIdentifier,
        ReadOnlySpan<byte> path,
        nint fileActions,
        nint attributes,
        nint arguments,
        nint environment);

    [LibraryImport(Library, EntryPoint = "posix_spawn_file_actions_init", SetLastError = true)]
    public static partial int SpawnFileActionsInit(nint fileActions);

    [LibraryImport(Library, EntryPoint = "posix_spawn_file_actions_destroy", SetLastError = true)]
    public static partial int SpawnFileActionsDestroy(nint fileActions);

    [LibraryImport(Library, EntryPoint = "posix_spawn_file_actions_adddup2", SetLastError = true)]
    public static partial int SpawnFileActionsAddDuplicate(nint fileActions, int fileDescriptor, int destinationFileDescriptor);

    [LibraryImport(Library, EntryPoint = "posix_spawn_file_actions_addchdir_np", SetLastError = true)]
    public static partial int SpawnFileActionsAddChangeDirectory(nint fileActions, ReadOnlySpan<byte> path);

    [LibraryImport(Library, EntryPoint = "posix_spawn_file_actions_addclosefrom_np", SetLastError = true)]
    public static partial int SpawnFileActionsAddCloseFrom(nint fileActions, int firstFileDescriptor);

    [LibraryImport(Library, EntryPoint = "posix_spawnattr_init", SetLastError = true)]
    public static partial int SpawnAttributesInit(nint attributes);

    [LibraryImport(Library, EntryPoint = "posix_spawnattr_destroy", SetLastError = true)]
    public static partial int SpawnAttributesDestroy(nint attributes);

    [LibraryImport(Library, EntryPoint = "posix_spawnattr_setflags", SetLastError = true)]
    public static partial int SpawnAttributesSetFlags(nint attributes, short flags);

    [LibraryImport(Library, EntryPoint = "posix_spawnattr_setpgroup", SetLastError = true)]
    public static partial int SpawnAttributesSetProcessGroup(nint attributes, int processGroup);

    [LibraryImport(Library, EntryPoint = "posix_spawnattr_setsigmask", SetLastError = true)]
    public static partial int SpawnAttributesSetSignalMask(nint attributes, nint signalSet);

    [LibraryImport(Library, EntryPoint = "posix_spawnattr_setsigdefault", SetLastError = true)]
    public static partial int SpawnAttributesSetSignalDefault(nint attributes, nint signalSet);

    [LibraryImport(Library, EntryPoint = "pipe2", SetLastError = true)]
    public static partial int CreatePipe(Span<int> fileDescriptors, int flags);

    [LibraryImport(Library, EntryPoint = "fcntl", SetLastError = true)]
    public static partial int FileControl(int fileDescriptor, int command, nint argument);

    /// <summary>flock(2): advisory lock on an open file description; non-blocking requests fail with EWOULDBLOCK.</summary>
    [LibraryImport(Library, EntryPoint = "flock", SetLastError = true)]
    public static partial int Flock(int fileDescriptor, int operation);

    [LibraryImport(Library, EntryPoint = "waitpid", SetLastError = true)]
    public static partial int WaitForProcess(int processIdentifier, out int status, int options);

    [LibraryImport(Library, EntryPoint = "sigemptyset", SetLastError = true)]
    public static partial int SignalSetEmpty(Span<byte> signalSet);

    [LibraryImport(Library, EntryPoint = "sigaddset", SetLastError = true)]
    public static partial int SignalSetAdd(Span<byte> signalSet, int signalNumber);
}
