using Scripts.Runtime.Platform;

namespace Scripts.Runtime.Processes;

/// <summary>
/// A8 (resolved by P8d): the child receives a raw <c>envp</c>, so a value whose bytes are not valid UTF-8 passes
/// through untouched. The native launch is built by <see cref="BuildEnviron"/>, which merges the initial
/// <c>/proc/self/environ</c> block (exact bytes) with the managed view (so <c>environment.set_variable</c>/<c>remove</c> are
/// visible) and then applies the <c>run_request</c> overlay.
/// </summary>
internal static class ChildEnvironment
{
    /// <summary>
    /// Reads the raw environment from /proc/self/environ, where each variable's bytes are kept untouched;
    /// null when the file is unreadable so the caller falls back on the string environment.
    /// </summary>
    public static byte[]? TryReadRaw() =>
        File.Exists("/proc/self/environ") ? SafeRead() : null;

    private static byte[]? SafeRead()
    {
        try
        {
            return File.ReadAllBytes("/proc/self/environ");
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the NUL-separated <c>NAME=VALUE</c> block handed to <c>posix_spawn</c>. A variable whose raw bytes still
    /// match the managed view keeps those bytes (non-UTF-8 included); a changed or added variable uses the managed
    /// value; a variable removed with <c>environment.remove_variable</c> is dropped; the overlay is applied last by name.
    /// </summary>
    public static byte[] BuildEnviron(IReadOnlyDictionary<string, string>? overlay) =>
        BuildEnviron(overlay, TryReadRaw(), ReadManagedEnvironment());

    internal static byte[] BuildEnviron(
        IReadOnlyDictionary<string, string>? overlay,
        byte[]? raw,
        IReadOnlyList<KeyValuePair<string, string>> managed)
    {
        var entries = new List<(byte[] Name, byte[] Value)>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);

        if (raw is { Length: > 0 })
        {
            foreach (var (rawName, rawValue) in Enumerate(raw))
            {
                var nameBytes = rawName.ToArray();
                var valueBytes = rawValue.ToArray();
                if (!NativeText.TryDecodeStrict(nameBytes, out var name))
                {
                    // .NET exposes a non-UTF-8 name decoded with U+FFFD. Match that view so the variable is not
                    // duplicated: keep the raw bytes when it is unchanged, use the managed spelling when it was
                    // changed, and drop it when environment.remove_variable removed the decoded name (audit F4).
                    name = NativeText.DecodeLenient(nameBytes);
                }

                var managedEntry = Find(managed, name);
                if (managedEntry is null)
                {
                    // environment.remove_variable: the raw block still has it, the managed view does not.
                    continue;
                }

                var unchanged = NativeText.DecodeLenient(valueBytes) == managedEntry.Value.Value;
                var entry = (
                    unchanged ? nameBytes : NativeText.ToBytes(name),
                    unchanged ? valueBytes : NativeText.ToBytes(managedEntry.Value.Value));
                if (positions.TryGetValue(name, out var existing))
                {
                    // A duplicated raw name (execve allows it) must produce one child entry, not two (audit F5).
                    entries[existing] = entry;
                }
                else
                {
                    positions[name] = entries.Count;
                    entries.Add(entry);
                }
            }
        }

        foreach (var entry in managed
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                     .Where(pair => !positions.ContainsKey(pair.Key)))
        {
            positions[entry.Key] = entries.Count;
            entries.Add((NativeText.ToBytes(entry.Key), NativeText.ToBytes(entry.Value)));
        }

        if (overlay is not null)
        {
            foreach (var (name, value) in overlay)
            {
                var nameBytes = NativeText.ToBytes(name);
                var valueBytes = NativeText.ToBytes(value);
                if (positions.TryGetValue(name, out var position))
                {
                    entries[position] = (nameBytes, valueBytes);
                }
                else
                {
                    positions[name] = entries.Count;
                    entries.Add((nameBytes, valueBytes));
                }
            }
        }

        var block = new MemoryStream();
        foreach (var (name, value) in entries)
        {
            block.Write(name);
            block.WriteByte((byte)'=');
            block.Write(value);
            block.WriteByte(0);
        }

        return block.ToArray();
    }

    /// <summary>
    /// True when the .NET string environment and the raw bytes agree on every variable; a mismatch means the raw
    /// bytes hold non-UTF-8 values that <see cref="Environment"/> has decoded to U+FFFD.
    /// </summary>
    public static bool MatchesStringEnvironment(byte[] raw)
    {
        var variables = Environment.GetEnvironmentVariables();
        var seen = 0;
        foreach (System.Collections.DictionaryEntry entry in variables)
        {
            if (entry.Key is not string name)
            {
                continue;
            }

            seen++;
            if (Lookup(raw, name) is not { } value
                || !NativeText.DecodeLenient(value).Equals(entry.Value as string ?? string.Empty, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return seen == Count(raw);
    }

    /// <summary>Serializes the .NET string environment into the "name=value\0" block (UTF-8, valid strings only).</summary>
    public static byte[] SerializeStringEnvironment()
    {
        var output = new MemoryStream();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name)
            {
                continue;
            }

            output.Write(NativeText.ToBytes(name + "=" + (entry.Value as string ?? string.Empty)));
            output.WriteByte(0);
        }

        return output.ToArray();
    }

    /// <summary>
    /// True when the raw block defines a name that is not valid UTF-8 but whose lenient spelling is
    /// <paramref name="name"/>; such a variable is inherited by children but cannot be addressed by a scripts string.
    /// </summary>
    public static bool TryFindInvalidName(string name, out byte[]? value)
    {
        value = null;
        var raw = TryReadRaw();
        if (raw is null)
        {
            return false;
        }

        foreach (var (entryName, entryValue) in Enumerate(raw))
        {
            if (!NativeText.TryDecodeStrict(entryName.Span, out _)
                && string.Equals(NativeText.DecodeLenient(entryName.Span), name, StringComparison.Ordinal))
            {
                value = entryValue.ToArray();
                return true;
            }
        }

        return false;
    }

    /// <summary>Raw bytes of the variable name, or null when the block does not define it.</summary>
    public static byte[]? Lookup(byte[] raw, string name)
    {
        var target = NativeText.ToBytes(name);
        foreach (var (entryName, entryValue) in Enumerate(raw))
        {
            if (entryName.Span.SequenceEqual(target))
            {
                return entryValue.ToArray();
            }
        }

        return null;
    }

    private static int Count(byte[] raw)
    {
        var total = 0;
        foreach (var _ in Enumerate(raw))
        {
            total++;
        }

        return total;
    }

    private static List<KeyValuePair<string, string>> ReadManagedEnvironment()
    {
        var entries = new List<KeyValuePair<string, string>>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name)
            {
                entries.Add(new KeyValuePair<string, string>(name, entry.Value as string ?? string.Empty));
            }
        }

        return entries;
    }

    private static KeyValuePair<string, string>? Find(IReadOnlyList<KeyValuePair<string, string>> managed, string name)
    {
        foreach (var entry in managed)
        {
            if (string.Equals(entry.Key, name, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Splits a "/proc/self/environ" block on its NUL separators, yielding (name, value) raw byte pairs.</summary>
    private static IEnumerable<(ReadOnlyMemory<byte> Name, ReadOnlyMemory<byte> Value)> Enumerate(byte[] raw)
    {
        var start = 0;
        while (start < raw.Length)
        {
            var end = Array.IndexOf(raw, (byte)0, start);
            if (end < 0)
            {
                end = raw.Length;
            }

            if (end > start)
            {
                var equal = Array.IndexOf(raw, (byte)'=', start, end - start);
                if (equal > start)
                {
                    yield return (raw.AsMemory(start, equal - start), raw.AsMemory(equal + 1, end - equal - 1));
                }
            }

            start = end + 1;
        }
    }
}
