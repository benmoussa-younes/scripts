using Scripts.Runtime.Platform;
using Scripts.Runtime.Processes;
using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>
/// A8 (minor): a child must inherit the environment byte for byte. <see cref="ChildEnvironment"/> reads the raw block
/// from /proc/self/environ, where a shell can export a value such as <c>61 FF 62</c> that a string view cannot hold.
/// </summary>
/// <remarks>
/// P8d: the child is launched by <c>posix_spawn</c> with an explicit <c>envp</c>, so a variable whose raw bytes are
/// not valid UTF-8 passes through untouched. The merge keeps the raw bytes of an unchanged variable, uses the managed
/// value for <c>environment.set_variable</c>, drops it for <c>environment.remove_variable</c>, then applies the request overlay. The
/// end-to-end proof launches scripts from a native parent that exports a raw byte (see the EndToEnd suite).
/// </remarks>
[Collection(SystemTestCollection.Name)]
public sealed class SystemChildEnvironmentTests
{
    [Fact]
    public void The_raw_reader_exposes_the_initial_environment_and_finds_named_values()
    {
        var raw = ChildEnvironment.TryReadRaw();

        Assert.NotNull(raw);
        var path = ChildEnvironment.Lookup(raw!, "PATH");
        Assert.NotNull(path);
        Assert.NotEmpty(NativeText.DecodeLenient(path!));
    }

    [Fact]
    public void A_raw_block_with_non_utf8_bytes_is_detected_as_not_matching_the_string_environment()
    {
        using var context = NewContext(out _);

        // A raw block identical to the string environment, plus a variable holding 0xFF (invalid UTF-8): the reader
        // must flag it, because the string view would decode it to U+FFFD (EF BF BD) and corrupt the child's value.
        var serialized = ChildEnvironment.SerializeStringEnvironment();
        var marker = System.Text.Encoding.ASCII.GetBytes("SCRIPTS_A8_BAD=");
        var polluted = new byte[serialized.Length + marker.Length + 2];
        serialized.CopyTo(polluted, 0);
        marker.CopyTo(polluted, serialized.Length);
        polluted[serialized.Length + marker.Length] = 0xFF;
        polluted[serialized.Length + marker.Length + 1] = 0;

        Assert.False(ChildEnvironment.MatchesStringEnvironment(polluted));
        Assert.Equal([0xFF], ChildEnvironment.Lookup(polluted, "SCRIPTS_A8_BAD")!);
    }

    [Fact]
    public void The_environ_block_keeps_raw_bytes_and_merges_the_managed_view()
    {
        var raw = Block(
            ("PATH", "/bin"u8.ToArray()),
            ("SCRIPTS_A8_BAD", [0x61, 0xFF, 0x62]),
            ("SCRIPTS_REMOVED", "x"u8.ToArray()),
            ("SCRIPTS_CHANGED", "old"u8.ToArray()));
        var managed = new List<KeyValuePair<string, string>>
        {
            new("PATH", "/bin"),
            new("SCRIPTS_A8_BAD", "a\uFFFDb"),
            new("SCRIPTS_CHANGED", "new"),
            new("SCRIPTS_ADDED", "added"),
        };

        var block = ChildEnvironment.BuildEnviron(null, raw, managed);

        Assert.Equal([0x61, 0xFF, 0x62], ChildEnvironment.Lookup(block, "SCRIPTS_A8_BAD")!);
        Assert.Null(ChildEnvironment.Lookup(block, "SCRIPTS_REMOVED"));
        Assert.Equal("new", NativeText.DecodeLenient(ChildEnvironment.Lookup(block, "SCRIPTS_CHANGED")!));
        Assert.Equal("added", NativeText.DecodeLenient(ChildEnvironment.Lookup(block, "SCRIPTS_ADDED")!));
    }

    [Fact]
    public void A_non_utf8_name_is_not_duplicated_and_remove_drops_it()
    {
        var raw = new MemoryStream();
        raw.Write("PATH=/bin\0"u8);
        raw.Write([(byte)'B', (byte)'A', (byte)'D', 0xFF, (byte)'N', (byte)'A', (byte)'M', (byte)'E', (byte)'=', (byte)'x', 0]);
        var withInvalidName = new List<KeyValuePair<string, string>>
        {
            new("PATH", "/bin"),
            new("BAD\uFFFDNAME", "x"),
        };

        var kept = ChildEnvironment.BuildEnviron(null, raw.ToArray(), withInvalidName);

        Assert.Equal(1, kept.Count(value => value == 0xFF));
        Assert.True(kept.AsSpan().IndexOf(new byte[] { 0xEF, 0xBF, 0xBD }) < 0, "the U+FFFD ghost entry is present");
        Assert.Equal(2, kept.Count(value => value == 0));

        var removed = ChildEnvironment.BuildEnviron(
            null,
            raw.ToArray(),
            [new KeyValuePair<string, string>("PATH", "/bin")]);

        Assert.DoesNotContain((byte)0xFF, removed);
        Assert.Equal(1, removed.Count(value => value == 0));
    }


    [Fact]
    public void A_duplicate_raw_name_produces_one_child_entry()
    {
        var raw = Block(("DUP", "a"u8.ToArray()), ("DUP", "b"u8.ToArray()));
        var managed = new List<KeyValuePair<string, string>> { new("DUP", "a") };

        var block = ChildEnvironment.BuildEnviron(null, raw, managed);

        Assert.Equal(1, block.Count(value => value == 0));
        Assert.Equal("a", NativeText.DecodeLenient(ChildEnvironment.Lookup(block, "DUP")!));
    }

    [Fact]
    public void The_overlay_replaces_a_name_and_appends_a_new_one()
    {
        var raw = Block(("PATH", "/bin"u8.ToArray()), ("KEEP", "1"u8.ToArray()));
        var managed = new List<KeyValuePair<string, string>> { new("PATH", "/bin"), new("KEEP", "1") };
        var overlay = new Dictionary<string, string> { ["PATH"] = "/overlay", ["NEW"] = "2" };

        var block = ChildEnvironment.BuildEnviron(overlay, raw, managed);

        Assert.Equal("/overlay", NativeText.DecodeLenient(ChildEnvironment.Lookup(block, "PATH")!));
        Assert.Equal("2", NativeText.DecodeLenient(ChildEnvironment.Lookup(block, "NEW")!));
        Assert.Equal("1", NativeText.DecodeLenient(ChildEnvironment.Lookup(block, "KEEP")!));
    }

    [Fact]
    public void Valid_multibyte_environment_values_reach_the_child_exactly()
    {
        using var context = NewContext(out _);
        using (WithEnvironmentVariable("SCRIPTS_A8_CHILD", "é😀"))
        {
            var lines = SystemScripts.Exec(context, "/usr/bin/env").GetText("standard_output").Split('\n');

            Assert.Contains("SCRIPTS_A8_CHILD=é😀", lines);
        }
    }

    [Fact]
    public void A_removed_variable_is_absent_from_the_child_environment()
    {
        using var context = NewContext();
        Environment.SetEnvironmentVariable("SCRIPTS_A8_REMOVED", "x");
        try
        {
            Assert.Contains("SCRIPTS_A8_REMOVED=x", SystemScripts.Exec(context, "/usr/bin/env").GetText("standard_output"), StringComparison.Ordinal);

            OkUnit(Call(context, "environment.remove_variable", Text("SCRIPTS_A8_REMOVED")));

            Assert.DoesNotContain("SCRIPTS_A8_REMOVED=x", SystemScripts.Exec(context, "/usr/bin/env").GetText("standard_output"), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SCRIPTS_A8_REMOVED", null);
        }
    }

    private static byte[] Block(params (string Name, byte[] Value)[] entries)
    {
        var block = new MemoryStream();
        foreach (var (name, value) in entries)
        {
            block.Write(System.Text.Encoding.UTF8.GetBytes(name + "="));
            block.Write(value);
            block.WriteByte(0);
        }

        return block.ToArray();
    }

    private static IDisposable WithEnvironmentVariable(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        return new Restore(name);
    }

    private sealed class Restore(string name) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable(name, null);
    }
}
