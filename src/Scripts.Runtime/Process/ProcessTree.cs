using System.Globalization;
using Scripts.Runtime.Platform;

namespace Scripts.Runtime.Processes;

/// <summary>
/// A2: finds the descendants of a child through /proc. Since P8d every captured child is in its own process group
/// (created by <c>posix_spawn</c> before <c>exec</c>), and <c>killpg</c> covers that group; the walk still runs first
/// so a descendant that called <c>setsid</c> is reached while its parent is alive, and it is the only path for
/// <c>process.run_inherit</c> in normal mode, which keeps scripts's group.
/// </summary>
internal static class ProcessTree
{
    /// <summary>Process identifiers descended from <paramref name="root"/>, deepest first so that no child reparents before it is signalled.</summary>
    public static IReadOnlyList<int> DescendantsDeepestFirst(int root)
    {
        var childrenByParent = ReadProcessTable();
        if (childrenByParent.Count == 0)
        {
            return [];
        }

        var ordered = new List<int>();
        CollectDeepestFirst(root, childrenByParent, ordered);
        return ordered;
    }

    private static void CollectDeepestFirst(int parent, Dictionary<int, List<int>> childrenByParent, List<int> ordered)
    {
        if (!childrenByParent.TryGetValue(parent, out var children))
        {
            return;
        }

        foreach (var child in children)
        {
            CollectDeepestFirst(child, childrenByParent, ordered);
            ordered.Add(child);
        }
    }

    private static Dictionary<int, List<int>> ReadProcessTable()
    {
        var childrenByParent = new Dictionary<int, List<int>>();
        if (!Directory.Exists("/proc"))
        {
            return childrenByParent;
        }

        try
        {
            foreach (var entry in Directory.EnumerateDirectories("/proc"))
            {
                var name = Path.GetFileName(entry);
                if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var processIdentifier)
                    || !TryReadParent(entry, out var parentIdentifier))
                {
                    continue;
                }

                if (!childrenByParent.TryGetValue(parentIdentifier, out var children))
                {
                    children = [];
                    childrenByParent[parentIdentifier] = children;
                }

                children.Add(processIdentifier);
            }
        }
        catch (IOException)
        {
            // /proc went away; there is nothing to signal.
        }

        return childrenByParent;
    }

    /// <summary>
    /// Reads the parent identifier from /proc/PID/stat. The command name is the second field and can contain spaces
    /// and parentheses, so parsing resumes after the last ')' (task state, then the parent identifier).
    /// </summary>
    private static bool TryReadParent(string processDirectory, out int parentIdentifier)
    {
        parentIdentifier = 0;
        try
        {
            var status = File.ReadAllText(Path.Combine(processDirectory, "stat"));
            var closingParenthesis = status.LastIndexOf(')');
            if (closingParenthesis < 0)
            {
                return false;
            }

            var fields = status[(closingParenthesis + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // fields[0] is the state; fields[1] is the parent identifier.
            return fields.Length >= 2
                && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out parentIdentifier);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Sends a signal to every descendant of the direct child (deepest first); returns the errno of the last failure, 0 when all succeeded or none exist.</summary>
    public static int SignalDescendants(int root, int signalNumber)
    {
        var errno = 0;
        foreach (var descendant in DescendantsDeepestFirst(root))
        {
            if (Libc.Kill(descendant, signalNumber) != 0)
            {
                var current = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                if (current != Errno.NoSuchProcess)
                {
                    errno = current;
                }
            }
        }

        return errno;
    }
}
