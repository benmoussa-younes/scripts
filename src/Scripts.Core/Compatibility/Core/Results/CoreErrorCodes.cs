// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Results;

/// <summary>
/// The stable error codes a <see cref="CoreError"/> carries, so a consumer can
/// branch on a value instead of a message. A new code is added here first; the
/// source is guarded so every emitted literal is in this catalogue.
/// </summary>
/// <remarks>
/// <para>
/// The codes are grouped by the layer that owns them: <c>bytesize.</c>,
/// <c>pathpattern.</c>, <c>paths.</c>, <c>units.</c> and <c>xdg.</c> are the kernel
/// primitives; <c>io.</c> is the file/atomic layer; <c>platform.</c> is the Linux
/// syscall surface; <c>serialization.</c> is the format layer.
/// </para>
/// <para>
/// These constants are the consumer-facing catalogue. The library's own call sites
/// use the literal code strings; <c>CoreErrorCodesTests</c> scans them and fails if
/// one is missing here, so the catalogue cannot drift. A consumer branches on the
/// code value (or the constant), never on the message.
/// </para>
/// </remarks>
public static class CoreErrorCodes
{
    /// <summary>A byte-size string could not be parsed.</summary>
    public const string ByteSizeInvalid = "bytesize.invalid";

    /// <summary>The host could not create or write its state directory.</summary>
    public const string HostStateUnwritable = "host.state_unwritable";

    /// <summary>The host's single-instance socket path is invalid.</summary>
    public const string HostSocketPathInvalid = "host.socket_path_invalid";

    /// <summary>The host cannot place its single-instance socket because no user runtime directory is set.</summary>
    public const string HostRuntimeDirectoryUnset = "host.runtime_dir_unset";

    /// <summary>A host option is outside its supported range.</summary>
    public const string HostInvalidOptions = "host.invalid_options";

    /// <summary>The host was created without a platform composition.</summary>
    public const string HostPlatformRequired = "host.platform_required";

    /// <summary>The requested executable was not found in the trusted directories.</summary>
    public const string ExecutionNotFound = "execution.not_found";

    /// <summary>The executable was found but the operating system did not start it.</summary>
    public const string ExecutionStartFailed = "execution.start_failed";

    /// <summary>The atomic writer could not commit the artefact.</summary>
    public const string IoCommitFailed = "io.commit_failed";

    /// <summary>The destination is a symbolic link and was refused.</summary>
    public const string IoSymbolicLinkTarget = "io.symbolic_link_target";

    /// <summary>A file could not be read (mapped, streamed or pinned).</summary>
    public const string IoUnreadable = "io.unreadable";

    /// <summary>A path pattern could not be parsed.</summary>
    public const string PathPatternInvalid = "pathpattern.invalid";

    /// <summary>A vendor or application name is not a valid path component.</summary>
    public const string PathsInvalidComponent = "paths.invalid_component";

    /// <summary>A directory could not be opened through the Linux syscall surface.</summary>
    public const string PlatformDirectoryOpenFailed = "platform.directory_open_failed";

    /// <summary>A document does not map to its requested type, or has no valid document form.</summary>
    public const string SerializationInvalidDocument = "serialization.invalid_document";

    /// <summary>A value or document nests deeper than the JSON writer allows.</summary>
    public const string SerializationDepthExceeded = "serialization.depth_exceeded";

    /// <summary>The data file does not exist.</summary>
    public const string SerializationMissing = "serialization.missing";

    /// <summary>The data file is larger than the configured maximum.</summary>
    public const string SerializationTooLarge = "serialization.too_large";

    /// <summary>A CSV document declares more columns than the maximum.</summary>
    public const string SerializationTooManyColumns = "serialization.too_many_columns";

    /// <summary>A query has no non-null value to aggregate.</summary>
    public const string DataEmpty = "data.empty";

    /// <summary>A query was applied to a column that is not numeric.</summary>
    public const string DataNotNumeric = "data.not_numeric";

    /// <summary>A numeric aggregate overflowed its type.</summary>
    public const string DataOverflow = "data.overflow";

    /// <summary>A join specification names a missing or incompatible column.</summary>
    public const string DataInvalidJoin = "data.invalid_join";

    /// <summary>A projection names a missing or duplicate column.</summary>
    public const string DataInvalidProjection = "data.invalid_projection";

    /// <summary>A sort key names a missing or empty column.</summary>
    public const string DataInvalidSort = "data.invalid_sort";

    /// <summary>A URL is not an allowed absolute http, https or mailto URI.</summary>
    public const string LinkInvalid = "link.invalid";

    /// <summary>The desktop could not open a URL.</summary>
    public const string LinkLaunchFailed = "link.launch_failed";

    /// <summary>The data file has a syntax error.</summary>
    public const string SerializationSyntax = "serialization.syntax";

    /// <summary>A tabular document has rows of uneven length.</summary>
    public const string SerializationUnevenRow = "serialization.uneven_row";

    /// <summary>The data file could not be read.</summary>
    public const string SerializationUnreadable = "serialization.unreadable";

    /// <summary>The format is unknown or its codec is not implemented.</summary>
    public const string SerializationUnsupportedFormat = "serialization.unsupported_format";

    /// <summary>The data file could not be written.</summary>
    public const string SerializationUnwritable = "serialization.unwritable";

    /// <summary>A text file is not valid UTF-8.</summary>
    public const string TextInvalidUtf8 = "text.invalid_utf8";

    /// <summary>A text file is larger than the configured maximum.</summary>
    public const string TextTooLarge = "text.too_large";

    /// <summary>A text file could not be written.</summary>
    public const string TextUnwritable = "text.unwritable";

    /// <summary>A required validation field is empty.</summary>
    public const string ValidationRequired = "validation.required";

    /// <summary>A validation value exceeds its maximum length.</summary>
    public const string ValidationTooLong = "validation.too_long";

    /// <summary>A validation value is not a valid TCP or UDP port.</summary>
    public const string ValidationPort = "validation.port";

    /// <summary>A quantity string could not be parsed as a unit.</summary>
    public const string UnitsInvalid = "units.invalid";

    /// <summary>The XDG home directory could not be resolved.</summary>
    public const string XdgHomeUnset = "xdg.home_unset";
}
