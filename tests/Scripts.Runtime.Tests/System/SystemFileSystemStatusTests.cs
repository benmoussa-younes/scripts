using System.Globalization;
using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

[Collection(SystemTestCollection.Name)]
public sealed class SystemFileSystemStatusTests
{
    [Fact]
    public void Reports_the_same_block_and_name_values_as_stat_f()
    {
        using var workspace = new SystemTestWorkspace();
        using var context = NewContext();
        var status = Ok<RecordValue>(Call(context, "filesystem.get_file_system_status", PathOf(workspace.Root)));
        var text = SystemScripts.Exec(context, "stat", "-f", "-c", "%s %S %b %f %a %c %d %l", workspace.Root)
            .GetText("standard_output")
            .Trim();
        var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(8, fields.Length);

        Assert.Equal(Parse(fields[0]), status.GetInteger("block_size"));
        Assert.Equal(Parse(fields[1]), status.GetInteger("fragment_size"));
        Assert.Equal(Parse(fields[2]), status.GetInteger("blocks"));
        Assert.Equal(Parse(fields[3]), status.GetInteger("free_blocks"));
        Assert.Equal(Parse(fields[4]), status.GetInteger("available_blocks"));
        Assert.Equal(Parse(fields[5]), status.GetInteger("total_inodes"));
        Assert.Equal(Parse(fields[6]), status.GetInteger("free_inodes"));
        Assert.Equal(Parse(fields[7]), status.GetInteger("maximum_name_length"));
    }

    [Fact]
    public void Missing_path_is_not_found()
    {
        using var context = NewContext();
        var missing = Path.Combine(Path.GetTempPath(), "scripts-statvfs-missing-" + Guid.NewGuid().ToString("N"));
        AssertIoError(Call(context, "filesystem.get_file_system_status", PathOf(missing)), "not_found");
    }

    private static long Parse(string text) => long.Parse(text, CultureInfo.InvariantCulture);
}
