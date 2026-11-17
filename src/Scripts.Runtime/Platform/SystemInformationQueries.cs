using System.Globalization;
using System.Runtime.InteropServices;

namespace Scripts.Runtime.Platform;

internal readonly record struct KernelInformation(string KernelName, string HostName, string KernelRelease, string MachineArchitecture);

/// <summary>hostname / uname / whoami through libc (SPEC-STDLIB §7.4).</summary>
internal static class SystemInformationQueries
{
    private const int UtsnameFieldLength = 65;
    private const int UtsnameFieldCount = 6;
    private const int HostNameBufferLength = 256;
    private const int PasswdBufferInitialLength = 1024;
    private const int PasswdBufferMaximumLength = 1 << 20;

    public static string HostName()
    {
        var buffer = new byte[HostNameBufferLength];
        if (Libc.GetHostName(buffer, (nuint)buffer.Length) != 0)
        {
            return string.Empty;
        }

        return NativeText.DecodeLenient(NativeText.UntilNul(buffer));
    }

    public static KernelInformation Uname()
    {
        var buffer = new byte[UtsnameFieldLength * UtsnameFieldCount];
        if (Libc.Uname(buffer) != 0)
        {
            throw IoFailureException.FromErrno(Marshal.GetLastPInvokeError(), string.Empty);
        }

        string Field(int fieldIndex) =>
            NativeText.DecodeLenient(NativeText.UntilNul(buffer.AsSpan(fieldIndex * UtsnameFieldLength, UtsnameFieldLength)));

        return new KernelInformation(Field(0), Field(1), Field(2), Field(4));
    }

    public static uint EffectiveUserIdentifier() => Libc.GetEffectiveUserIdentifier();

    public static uint EffectiveGroupIdentifier() => Libc.GetEffectiveGroupIdentifier();

    /// <summary>User identifier of <paramref name="userName"/> from the user database (`getpwnam_r(3)`), unknown name → not_found.</summary>
    public static uint UserIdentifier(string userName)
    {
        var nativeName = ToNativeLookupName(userName);
        for (var bufferLength = PasswdBufferInitialLength; bufferLength <= PasswdBufferMaximumLength; bufferLength *= 2)
        {
            // The passwd strings live in this buffer, so it must not move while they are read.
            var buffer = GC.AllocateUninitializedArray<byte>(bufferLength, pinned: true);
            var status = Libc.GetPasswdEntryByName(nativeName, out var entry, buffer, (nuint)bufferLength, out var found);
            if (status == Errno.RangeExceeded)
            {
                continue;
            }

            if (status == Errno.NoSuchProcess)
            {
                throw new IoFailureException(IoErrorKinds.NotFound, userName, "user not found");
            }

            if (status != 0)
            {
                throw IoFailureException.FromErrno(status, userName);
            }

            if (found == 0)
            {
                throw new IoFailureException(IoErrorKinds.NotFound, userName, "user not found");
            }

            return entry.UserIdentifier;
        }

        throw new IoFailureException(IoErrorKinds.Other, userName, "the user-database entry is too large");
    }

    /// <summary>Group identifier of <paramref name="groupName"/> from the group database (`getgrnam_r(3)`), unknown name → not_found.</summary>
    public static uint GroupIdentifier(string groupName)
    {
        var nativeName = ToNativeLookupName(groupName);
        for (var bufferLength = PasswdBufferInitialLength; bufferLength <= PasswdBufferMaximumLength; bufferLength *= 2)
        {
            // The group strings live in this buffer, so it must not move while they are read.
            var buffer = GC.AllocateUninitializedArray<byte>(bufferLength, pinned: true);
            var status = Libc.GetGroupEntryByName(nativeName, out var entry, buffer, (nuint)bufferLength, out var found);
            if (status == Errno.RangeExceeded)
            {
                continue;
            }

            if (status == Errno.NoSuchProcess)
            {
                throw new IoFailureException(IoErrorKinds.NotFound, groupName, "group not found");
            }

            if (status != 0)
            {
                throw IoFailureException.FromErrno(status, groupName);
            }

            if (found == 0)
            {
                throw new IoFailureException(IoErrorKinds.NotFound, groupName, "group not found");
            }

            return entry.GroupIdentifier;
        }

        throw new IoFailureException(IoErrorKinds.Other, groupName, "the group-database entry is too large");
    }

    private static byte[] ToNativeLookupName(string name) =>
        PathText.ContainsNul(name)
            ? throw new IoFailureException(IoErrorKinds.InvalidArgument, name, "the name contains a NUL character")
            : NativeText.ToNullTerminated(name);

    public static string EffectiveUserName()
    {
        var userIdentifier = Libc.GetEffectiveUserIdentifier();
        var fallbackName = userIdentifier.ToString(CultureInfo.InvariantCulture);
        for (var bufferLength = PasswdBufferInitialLength; bufferLength <= PasswdBufferMaximumLength; bufferLength *= 2)
        {
            // The passwd strings live in this buffer, so it must not move while they are read.
            var buffer = GC.AllocateUninitializedArray<byte>(bufferLength, pinned: true);
            var status = Libc.GetPasswdEntryReentrant(userIdentifier, out var entry, buffer, (nuint)bufferLength, out var found);
            if (status == Errno.RangeExceeded)
            {
                continue;
            }

            if (status != 0 || found == 0 || entry.UserName == 0)
            {
                return fallbackName;
            }

            var name = Marshal.PtrToStringUTF8(entry.UserName);
            GC.KeepAlive(buffer);
            return string.IsNullOrEmpty(name) ? fallbackName : name;
        }

        return fallbackName;
    }
}
