using System.Runtime.CompilerServices;
using Scripts.Runtime.Stdlib;

[assembly: InternalsVisibleTo("Scripts.Runtime.Tests")]

namespace Scripts.Runtime;

/// <summary>Stdlib implementations that touch the system (stdout/stderr, filesystem, processes, libc).</summary>
internal static class SystemStdlib
{
    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        SystemOutputFunctions.Register(table);
        SystemFileSystemFunctions.Register(table);
        FileLockFunctions.Register(table);
        SystemFileSystemEntriesFunctions.Register(table);
        SystemProcessFunctions.Register(table);
        SystemInformationFunctions.Register(table);
        TimeFunctions.Register(table);
        StandardModuleFunctions.Register(table);
        RandomFunctions.Register(table);
        ParallelFunctions.Register(table);
    }
}
