namespace Scripts.Runtime.Processes;

/// <summary>
/// The launch context of one <c>run_request</c> (DESIGN §7.3): <c>working_directory == ""</c> inherits the
/// process directory and a null <paramref name="EnvironmentOverlay"/> inherits the whole environment.
/// </summary>
internal sealed record ProcessLaunchContext(string WorkingDirectory, IReadOnlyDictionary<string, string>? EnvironmentOverlay)
{
    public static readonly ProcessLaunchContext Inherit = new(string.Empty, null);

    /// <summary>The PATH the effective environment exposes: the overlay's PATH when it sets one, else the process PATH.</summary>
    public string? EffectivePathVariable =>
        EnvironmentOverlay is not null && EnvironmentOverlay.TryGetValue("PATH", out var overlayed)
            ? overlayed
            : Environment.GetEnvironmentVariable("PATH");
}

/// <summary>One <c>run_request</c> record flattened for the launchers (process.run, parallel.run_all, a pipeline stage).</summary>
internal sealed record RunRequestData(
    string Program,
    IReadOnlyList<string> Arguments,
    string StandardInput,
    string StandardInputFile,
    ProcessLaunchContext LaunchContext,
    long TimeoutMilliseconds)
{
    public static RunRequestData FromRecord(RecordValue record)
    {
        var arguments = new List<string>();
        foreach (var argument in ((ListValue)record.Get("arguments")).Items)
        {
            arguments.Add(((StringValue)argument).Text);
        }

        var environment = (MapValue)record.Get("environment_overlay");
        IReadOnlyDictionary<string, string>? overlay = null;
        if (environment.Count > 0)
        {
            var entries = new Dictionary<string, string>(environment.Count, StringComparer.Ordinal);
            for (var index = 0; index < environment.Keys.Count; index++)
            {
                entries[((StringValue)environment.Keys[index]).Text] = ((StringValue)environment.Values[index]).Text;
            }

            overlay = entries;
        }

        return new RunRequestData(
            record.GetText("program"),
            arguments,
            record.GetText("standard_input"),
            ((PathValue)record.Get("standard_input_file")).Text,
            new ProcessLaunchContext(((PathValue)record.Get("working_directory")).Text, overlay),
            record.GetInteger("timeout_milliseconds"));
    }
}
