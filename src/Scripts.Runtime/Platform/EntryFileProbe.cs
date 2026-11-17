namespace Scripts.Runtime.Platform;

/// <summary>What an entry script path resolves to when its final symbolic link is followed.</summary>
public enum EntryFileKind
{
    /// <summary>A regular file: the supported entry form.</summary>
    Regular,

    /// <summary>A character device such as <c>/dev/null</c>, accepted as an empty script.</summary>
    CharacterDevice,

    /// <summary>A directory, refused before the loader opens it.</summary>
    Directory,

    /// <summary>A FIFO, refused: opening it for reading would block until a writer appears.</summary>
    Fifo,

    /// <summary>A socket, refused: <c>open(2)</c> reports <c>ENXIO</c>.</summary>
    Socket,

    /// <summary>A block device, refused like every other non-entry file.</summary>
    BlockDevice,

    /// <summary>Another file type, refused like every other non-entry file.</summary>
    Other,

    /// <summary>The path is missing or cannot be inspected; the loader reports its own SCRIPTS-CLI-0009.</summary>
    MissingOrUnreadable,
}

/// <summary>
/// Best-effort classification of the entry script path before the Syntax loader opens it (BUG-020): a FIFO or a
/// socket would block or fail in <c>open(2)</c>, so <c>run</c>/<c>check</c>/<c>explain</c> refuse them first.
/// The check and the loader open are separate syscalls (TOCTOU): a path swapped to a FIFO after this call can
/// still block, a local denial of service on a path the caller names, not a privilege escalation. A missing path
/// is left to the loader so the pre-check introduces no new error.
/// </summary>
public static class EntryFileProbe
{
    /// <summary>The reason reported inside the existing <c>SCRIPTS-CLI-0009</c> diagnostic for a refused path.</summary>
    public const string NotRegularReason = "not a regular file";

    /// <summary>Stats <paramref name="path"/> following the final symbolic link and classifies its target.</summary>
    public static EntryFileKind Classify(string path)
    {
        var nativePath = NativeFileSystem.ToNativePath(path);
        if (NativeFileSystem.TryGetStatus(nativePath, followLinks: true, out var status) != 0)
        {
            return EntryFileKind.MissingOrUnreadable;
        }

        return (status.Mode & Libc.FileTypeMask) switch
        {
            Libc.FileTypeRegular => EntryFileKind.Regular,
            Libc.FileTypeCharacterDevice => EntryFileKind.CharacterDevice,
            Libc.FileTypeDirectory => EntryFileKind.Directory,
            Libc.FileTypeFifo => EntryFileKind.Fifo,
            Libc.FileTypeSocket => EntryFileKind.Socket,
            Libc.FileTypeBlockDevice => EntryFileKind.BlockDevice,
            _ => EntryFileKind.Other,
        };
    }

    /// <summary>
    /// True when the loader may open the path: a regular file, a character device, or a missing/unreadable path
    /// (whose existing <c>SCRIPTS-CLI-0009</c> report is the loader's).
    /// </summary>
    public static bool IsLoadable(EntryFileKind kind) =>
        kind is EntryFileKind.Regular or EntryFileKind.CharacterDevice or EntryFileKind.MissingOrUnreadable;
}
