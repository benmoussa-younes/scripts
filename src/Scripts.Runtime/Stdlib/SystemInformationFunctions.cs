using Scripts.Runtime.Platform;

namespace Scripts.Runtime.Stdlib;

internal static class SystemInformationFunctions
{
    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["system.get_information"] = Uname;
        table["system.user_name"] = (_, _) => new StringValue(SystemInformationQueries.EffectiveUserName());
        table["system.cpu_count"] = (_, _) => IntegerValue.From(Environment.ProcessorCount);
        table["system.effective_user_id"] = (_, _) => IntegerValue.From(SystemInformationQueries.EffectiveUserIdentifier());
        table["system.effective_group_id"] = (_, _) => IntegerValue.From(SystemInformationQueries.EffectiveGroupIdentifier());
        table["system.user_id"] = UserIdentifier;
        table["system.group_id"] = GroupIdentifier;
    }

    private static Value UserIdentifier(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardIo(() => SystemValues.Success(
            IntegerValue.From(SystemInformationQueries.UserIdentifier(SystemValues.TextAt(arguments, 0)))));

    private static Value GroupIdentifier(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardIo(() => SystemValues.Success(
            IntegerValue.From(SystemInformationQueries.GroupIdentifier(SystemValues.TextAt(arguments, 0)))));

    private static Value Uname(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardIo(() =>
        {
            var information = SystemInformationQueries.Uname();
            return SystemValues.Success(new RecordValue(
                "system_information",
                new StringValue(information.KernelName),
                new StringValue(information.HostName),
                new StringValue(information.KernelRelease),
                new StringValue(information.MachineArchitecture)));
        });
}
