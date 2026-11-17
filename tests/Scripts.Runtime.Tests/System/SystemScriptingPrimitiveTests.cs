using Scripts.Runtime.Platform;
using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>
/// Regression tests for the P9g scripting primitives (SPEC-STDLIB §7.2, §7.5.1): the adversarial audit
/// found that a destination inside the source was copied into itself, that a read-only source could not
/// be copied, and that the temporary file did not force its private mode.
/// </summary>
[Collection(SystemTestCollection.Name)]
public sealed class SystemScriptingPrimitiveTests
{
    [Fact]
    public void CopyDirectoryTree_refuses_a_destination_inside_the_source_before_any_write()
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Combine("source");
        Directory.CreateDirectory(source);
        workspace.Write("source/a.txt", "one");

        AssertIoError(
            Call("filesystem.copy_directory_tree", PathOf(source), PathOf(Path.Combine(source, "inner"))),
            "invalid_argument",
            Path.Combine(source, "inner"));

        Assert.False(Directory.Exists(Path.Combine(source, "inner")));
        Assert.Equal("one", File.ReadAllText(Path.Combine(source, "a.txt")));
    }

    [Fact]
    public void CopyDirectoryTree_copies_a_read_only_source_and_restores_its_final_mode()
    {
        using var workspace = new SystemTestWorkspace();
        var source = workspace.Combine("source");
        Directory.CreateDirectory(source);
        var file = workspace.Write("source/a.txt", "one");
        File.SetUnixFileMode(file, (UnixFileMode)Convert.ToInt32("444", 8));
        File.SetUnixFileMode(source, (UnixFileMode)Convert.ToInt32("555", 8));
        var destination = workspace.Combine("copy");

        OkUnit(Call("filesystem.copy_directory_tree", PathOf(source), PathOf(destination)));

        Assert.Equal("one", File.ReadAllText(Path.Combine(destination, "a.txt")));
        Assert.Equal((UnixFileMode)Convert.ToInt32("555", 8), SystemRawFiles.ModeOf(destination));
        Assert.Equal((UnixFileMode)Convert.ToInt32("444", 8), SystemRawFiles.ModeOf(Path.Combine(destination, "a.txt")));
    }

    [Fact]
    public void MakeTemporaryFile_creates_an_empty_private_0600_file()
    {
        var result = Call("filesystem.make_temporary_file", new StringValue("scripts-test-"), new StringValue(".tmp"));
        var path = Ok<PathValue>(result).Text;
        try
        {
            Assert.Equal((UnixFileMode)Convert.ToInt32("600", 8), SystemRawFiles.ModeOf(path));
            Assert.Empty(File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetRealPath_maps_a_non_directory_component_to_invalid_path()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("a.txt", "x");

        AssertIoError(
            Call("filesystem.get_real_path", PathOf(Path.Combine(file, "below"))),
            "invalid_path");
    }
}
