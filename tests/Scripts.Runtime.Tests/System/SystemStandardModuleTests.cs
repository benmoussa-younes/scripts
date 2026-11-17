using static Scripts.Runtime.Tests.SystemFunctions.SystemCalls;

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>
/// The intrinsic namespaces <c>io</c> and <c>process</c> (available without import) are implemented by
/// <see cref="SystemStdlib"/> under their catalog name. <c>read_standard_input</c> is covered end to end
/// by Scripts.EndToEnd.Tests (it reads the real file descriptor 0); here we check the registration and <c>defer</c>.
/// </summary>
[Collection(SystemTestCollection.Name)]
public sealed class SystemStandardModuleTests
{
    [Fact]
    public void Defer_registers_the_function_on_the_context()
    {
        using var context = NewContext();

        Assert.IsType<UnitValue>(Call(context, "process.defer", Function()));

        var actions = context.TakeDeferredActionsInReverseOrder();
        Assert.Single(actions);
    }

    [Fact]
    public void Deferred_actions_are_returned_in_reverse_registration_order()
    {
        using var context = NewContext();
        var first = Function();
        var second = Function();

        Assert.IsType<UnitValue>(Call(context, "process.defer", first));
        Assert.IsType<UnitValue>(Call(context, "process.defer", second));
        var actions = context.TakeDeferredActionsInReverseOrder();
        Assert.Equal([second, first], actions);
        // The list is consumed: a second drain returns nothing.
        Assert.Empty(context.TakeDeferredActionsInReverseOrder());
    }

    [Fact]
    public void Read_standard_input_is_registered_and_resolves_through_the_registry()
    {
        Assert.Contains("io.read_standard_input", Scripts.Runtime.StdlibRegistry.ImplementedNames);
        Assert.Contains("io.read_standard_input_line", Scripts.Runtime.StdlibRegistry.ImplementedNames);
        Assert.Contains("io.write_standard_output", Scripts.Runtime.StdlibRegistry.ImplementedNames);
        Assert.Contains("process.defer", Scripts.Runtime.StdlibRegistry.ImplementedNames);
    }

    private static FunctionValue Function() => new TestFunction();

    private sealed class TestFunction : FunctionValue
    {
        public override int Arity => 0;
    }
}
