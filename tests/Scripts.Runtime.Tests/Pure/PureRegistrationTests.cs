using Scripts.Core;

namespace Scripts.Runtime.Tests.Pure;

public class PureRegistrationTests
{
    private static readonly string[] GlobalFunctionNames = ["to_float", "to_integer", "range"];

    private static bool IsPureScope(StdlibEntry entry) => entry.Kind switch
    {
        // process_result, pipeline_result, run_request and file_lock methods live in the system scope (SystemStdlib).
        StdlibKind.Method => entry.ReceiverTypeText is not ("process_result" or "pipeline_result" or "run_request" or "file_lock"),
        StdlibKind.ModuleFunction => entry.ModuleName is "path" or "environment" or "json" or "encoding",
        _ => GlobalFunctionNames.Contains(entry.Name),
    };

    private static HashSet<string> RegisteredByPureStdlib()
    {
        var table = new Dictionary<string, StdlibImplementation>(StringComparer.Ordinal);
        PureStdlib.Register(table);
        return [.. table.Keys];
    }

    [Fact]
    public void ScopeHasTheExpectedCatalogEntryCount() =>
        Assert.Equal(117, StdlibCatalog.All.Count(IsPureScope));

    [Fact]
    public void EveryCatalogEntryInScopeIsRegistered()
    {
        var registered = RegisteredByPureStdlib();
        var missing = StdlibCatalog.All.Where(IsPureScope).Select(entry => entry.QualifiedName).Where(name => !registered.Contains(name)).ToArray();
        Assert.Empty(missing);
    }

    [Fact]
    public void NothingOutsideTheScopeIsRegistered()
    {
        var expected = StdlibCatalog.All.Where(IsPureScope).Select(entry => entry.QualifiedName).ToHashSet(StringComparer.Ordinal);
        var unexpected = RegisteredByPureStdlib().Where(name => !expected.Contains(name)).ToArray();
        Assert.Empty(unexpected);
    }

    [Fact]
    public void RegistryExposesEveryPureImplementation()
    {
        foreach (var name in RegisteredByPureStdlib())
        {
            Assert.NotNull(StdlibRegistry.Get(name));
        }
    }
}
