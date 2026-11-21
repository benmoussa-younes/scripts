namespace Scripts.Runtime;

/// <summary>Pure (no system access beyond environment variables) stdlib implementations; owned by the "pure stdlib" work item.</summary>
public static class PureStdlib
{
    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        ConversionFunctions.Register(table);
        PathFunctions.Register(table);
        StringFunctions.Register(table);
        ListFunctions.Register(table);
        MapFunctions.Register(table);
        ResultFunctions.Register(table);
        EnvironmentFunctions.Register(table);
        JsonFunctions.Register(table);
        EncodingFunctions.Register(table);
        NumericFunctions.Register(table);
        CsvFunctions.Register(table);
    }
}
