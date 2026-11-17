using Scripts.Runtime.Platform;
using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

[Collection(SystemTestCollection.Name)]
public sealed class SystemOwnershipTests
{
    [Fact]
    public void Path_status_appends_owner_device_inode_link_and_timestamp_fields()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("owned.txt", "content");
        using var context = NewContext();

        var status = Ok<RecordValue>(Call(context, "filesystem.get_path_status", PathOf(file)));
        var native = NativeFileSystem.GetStatusOrThrow(file, followLinks: false);

        Assert.Equal((long)Libc.GetEffectiveUserIdentifier(), status.GetInteger("owner_user_id"));
        Assert.Equal((long)Libc.GetEffectiveGroupIdentifier(), status.GetInteger("owner_group_id"));
        Assert.Equal((long)native.DeviceMajor, status.GetInteger("device_major"));
        Assert.Equal((long)native.DeviceMinor, status.GetInteger("device_minor"));
        Assert.Equal((long)native.InodeNumber, status.GetInteger("inode"));
        Assert.True(status.GetInteger("link_count") >= 1);
        Assert.True(status.GetInteger("inode") > 0);
        Assert.True(status.GetInteger("accessed_unix_seconds") > 0);
        Assert.True(status.GetInteger("changed_unix_seconds") > 0);
    }

    [Fact]
    public void Set_owner_and_set_owner_no_follow_accept_the_current_ids_and_are_reflected()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("owned.txt", "content");
        using var context = NewContext();
        long userId = Libc.GetEffectiveUserIdentifier();
        long groupId = Libc.GetEffectiveGroupIdentifier();

        OkUnit(Call(context, "filesystem.set_owner", PathOf(file), Integer(userId), Integer(groupId)));
        var status = Ok<RecordValue>(Call(context, "filesystem.get_path_status", PathOf(file)));
        Assert.Equal(userId, status.GetInteger("owner_user_id"));
        Assert.Equal(groupId, status.GetInteger("owner_group_id"));

        OkUnit(Call(context, "filesystem.set_owner_no_follow", PathOf(file), Integer(userId), Integer(groupId)));
        status = Ok<RecordValue>(Call(context, "filesystem.get_path_status", PathOf(file)));
        Assert.Equal(userId, status.GetInteger("owner_user_id"));
        Assert.Equal(groupId, status.GetInteger("owner_group_id"));
    }

    [Fact]
    public void Set_owner_no_follow_changes_the_symlink_and_leaves_the_target_unchanged()
    {
        using var workspace = new SystemTestWorkspace();
        var target = workspace.Write("target.txt", "content");
        var link = workspace.Combine("link.txt");
        File.CreateSymbolicLink(link, target);
        using var context = NewContext();
        long userId = Libc.GetEffectiveUserIdentifier();
        long groupId = Libc.GetEffectiveGroupIdentifier();
        var before = Ok<RecordValue>(Call(context, "filesystem.get_path_status", PathOf(target)));

        OkUnit(Call(context, "filesystem.set_owner_no_follow", PathOf(link), Integer(userId), Integer(groupId)));

        var after = Ok<RecordValue>(Call(context, "filesystem.get_path_status", PathOf(target)));
        Assert.Equal(before.GetInteger("owner_user_id"), after.GetInteger("owner_user_id"));
        Assert.Equal(before.GetInteger("owner_group_id"), after.GetInteger("owner_group_id"));
        var linkStatus = Ok<RecordValue>(Call(context, "filesystem.get_path_status", PathOf(link)));
        Assert.Equal("symlink", linkStatus.GetText("kind"));
        Assert.Equal(userId, linkStatus.GetInteger("owner_user_id"));
        Assert.Equal(groupId, linkStatus.GetInteger("owner_group_id"));
    }

    [Fact]
    public void Set_owner_follows_a_final_symlink_but_set_owner_no_follow_does_not()
    {
        using var workspace = new SystemTestWorkspace();
        var dangling = workspace.Combine("dangling.txt");
        File.CreateSymbolicLink(dangling, workspace.Combine("absent.txt"));
        using var context = NewContext();
        long userId = Libc.GetEffectiveUserIdentifier();
        long groupId = Libc.GetEffectiveGroupIdentifier();

        AssertIoError(Call(context, "filesystem.set_owner", PathOf(dangling), Integer(userId), Integer(groupId)), "not_found", dangling);
        OkUnit(Call(context, "filesystem.set_owner_no_follow", PathOf(dangling), Integer(userId), Integer(groupId)));
    }

    [Fact]
    public void Unknown_user_and_group_names_are_not_found_with_the_name_as_subject()
    {
        using var context = NewContext();

        AssertIoError(Call(context, "system.user_id", Text("scripts-definitely-not-a-user")), "not_found", "scripts-definitely-not-a-user");
        AssertIoError(Call(context, "system.group_id", Text("scripts-definitely-not-a-group")), "not_found", "scripts-definitely-not-a-group");
    }

    [Fact]
    public void Identifiers_outside_the_unsigned_identifier_range_are_invalid_argument()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("owned.txt", "content");
        using var context = NewContext();
        long userId = Libc.GetEffectiveUserIdentifier();
        long groupId = Libc.GetEffectiveGroupIdentifier();

        AssertIoError(Call(context, "filesystem.set_owner", PathOf(file), Integer(-1), Integer(groupId)), "invalid_argument", file);
        AssertIoError(Call(context, "filesystem.set_owner", PathOf(file), Integer(userId), Integer(4_294_967_296)), "invalid_argument", file);
        AssertIoError(Call(context, "filesystem.set_owner", PathOf(file), Integer(4_294_967_295), Integer(groupId)), "invalid_argument", file);
        AssertIoError(Call(context, "filesystem.set_owner", PathOf(file), Integer(userId), Integer(4_294_967_295)), "invalid_argument", file);
        AssertIoError(Call(context, "filesystem.set_owner_no_follow", PathOf(file), Integer(-1), Integer(groupId)), "invalid_argument", file);
        AssertIoError(Call(context, "filesystem.set_owner_no_follow", PathOf(file), Integer(userId), Integer(4_294_967_296)), "invalid_argument", file);
        AssertIoError(Call(context, "filesystem.set_owner_no_follow", PathOf(file), Integer(4_294_967_295), Integer(groupId)), "invalid_argument", file);
        AssertIoError(Call(context, "filesystem.set_owner_no_follow", PathOf(file), Integer(userId), Integer(4_294_967_295)), "invalid_argument", file);
    }

    [Fact]
    public void Names_containing_a_nul_character_are_invalid_argument()
    {
        using var context = NewContext();

        AssertIoError(Call(context, "system.user_id", Text("a\0b")), "invalid_argument", "a\0b");
        AssertIoError(Call(context, "system.group_id", Text("a\0b")), "invalid_argument", "a\0b");
    }

    [Fact]
    public void Effective_identifiers_round_trip_the_user_and_group_databases()
    {
        using var context = NewContext();

        var effectiveUserId = Assert.IsType<IntegerValue>(Call(context, "system.effective_user_id")).Number;
        var effectiveGroupId = Assert.IsType<IntegerValue>(Call(context, "system.effective_group_id")).Number;
        Assert.Equal((long)Libc.GetEffectiveUserIdentifier(), effectiveUserId);
        Assert.Equal((long)Libc.GetEffectiveGroupIdentifier(), effectiveGroupId);

        var userName = Assert.IsType<StringValue>(Call(context, "system.user_name")).Text;
        Assert.Equal(effectiveUserId, Ok<IntegerValue>(Call(context, "system.user_id", Text(userName))).Number);
        var groupName = SystemScripts.Exec(context, "id", "-gn").GetText("standard_output").Trim();
        Assert.Equal(effectiveGroupId, Ok<IntegerValue>(Call(context, "system.group_id", Text(groupName))).Number);
    }

    [Fact]
    public void No_follow_and_path_status_trim_a_trailing_slash()
    {
        using var workspace = new SystemTestWorkspace();
        var target = workspace.Write("target.txt", "content");
        var link = workspace.Combine("link.txt");
        File.CreateSymbolicLink(link, target);
        var dangling = workspace.Combine("dangling.txt");
        File.CreateSymbolicLink(dangling, workspace.Combine("absent.txt"));
        using var context = NewContext();
        long userId = Libc.GetEffectiveUserIdentifier();
        long groupId = Libc.GetEffectiveGroupIdentifier();
        var before = Ok<RecordValue>(Call(context, "filesystem.get_path_status", PathOf(target)));

        var linkStatus = Ok<RecordValue>(Call(context, "filesystem.get_path_status", PathOf(link + "/")));
        Assert.Equal("symlink", linkStatus.GetText("kind"));
        var danglingStatus = Ok<RecordValue>(Call(context, "filesystem.get_path_status", PathOf(dangling + "/")));
        Assert.Equal("symlink", danglingStatus.GetText("kind"));

        OkUnit(Call(context, "filesystem.set_owner_no_follow", PathOf(link + "/"), Integer(userId), Integer(groupId)));
        OkUnit(Call(context, "filesystem.set_owner_no_follow", PathOf(dangling + "/"), Integer(userId), Integer(groupId)));

        var after = Ok<RecordValue>(Call(context, "filesystem.get_path_status", PathOf(target)));
        Assert.Equal(before.GetInteger("owner_user_id"), after.GetInteger("owner_user_id"));
        Assert.Equal(before.GetInteger("owner_group_id"), after.GetInteger("owner_group_id"));
    }

    [Fact]
    public void Set_owner_does_not_trim_a_trailing_slash_on_a_regular_file()
    {
        using var workspace = new SystemTestWorkspace();
        var file = workspace.Write("file.txt", "content");
        using var context = NewContext();
        long userId = Libc.GetEffectiveUserIdentifier();
        long groupId = Libc.GetEffectiveGroupIdentifier();

        AssertIoError(
            Call(context, "filesystem.set_owner", PathOf(file + "/"), Integer(userId), Integer(groupId)),
            "not_a_directory",
            file + "/");
    }
}
