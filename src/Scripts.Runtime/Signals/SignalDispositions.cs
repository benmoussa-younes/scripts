using System.Runtime.InteropServices;
using Scripts.Runtime.Platform;

namespace Scripts.Runtime.Signals;

internal static class SignalDispositions
{
    private const int SigactionBufferLength = 256;
    private const nint IgnoreHandler = 1;

    /// <summary>Reads the current disposition without changing it (sigaction with a null new action).</summary>
    public static bool IsIgnored(int signalNumber)
    {
        var previousAction = new byte[SigactionBufferLength];
        return Libc.InspectSignalAction(signalNumber, 0, previousAction) == 0
            && MemoryMarshal.Read<nint>(previousAction) == IgnoreHandler;
    }
}
