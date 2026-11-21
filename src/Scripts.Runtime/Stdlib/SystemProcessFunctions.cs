using System.Globalization;
using Scripts.Runtime.Platform;
using Scripts.Runtime.Processes;

namespace Scripts.Runtime.Stdlib;

internal static class SystemProcessFunctions
{
    private const string ShellProgram = "/bin/sh";
    private const long MaximumExitScriptCode = 255;

    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["process.run_shell"] = ExecuteShell;
        table["process.find_program_path"] = Which;
        table["process.identifier"] = (_, _) => IntegerValue.From(Environment.ProcessId);
        table["process.set_umask"] = SetUmask;
        table["process.spawn"] = Spawn;
        table["process.wait_for_process"] = WaitForProcess;
        table["process.terminate"] = KillProcess;
        table["process.arguments"] = ScriptArguments;
        table["process.exit_with_code"] = ExitScript;
        table["process.exit_with_message"] = ExitWithMessage;
        table["process.request"] = Request;
        table["process.run"] = Run;
        table["process.run_inherit"] = RunInherit;
        table["process.script_path"] = ScriptPath;
        table["process.pipeline"] = Pipeline;
        table["process.pipeline_to_file_overwrite"] = PipelineToFileOverwrite;
        table["process_result.require_success"] = RequireSuccess;
        table["pipeline_result.require_success"] = RequirePipelineSuccess;
        table["run_request.with_standard_input"] = WithStandardInput;
        table["run_request.with_standard_input_from_file"] = WithStandardInputFromFile;
        table["run_request.with_working_directory"] = WithWorkingDirectory;
        table["run_request.with_environment"] = WithEnvironment;
        table["run_request.with_timeout"] = WithTimeout;
    }

    /// <summary>
    /// process.request(program, arguments): the only constructor of <c>run_request</c> (SPEC-STDLIB §7.3).
    /// It records its inputs verbatim with the documented defaults; the program, directory, environment and
    /// deadline are validated at launch, not here. The other fields are set by the copy methods below, which
    /// is why there is no six-argument form: two adjacent `string` parameters would be silently swappable.
    /// </summary>
    private static Value Request(RuntimeContext context, Value[] arguments)
    {
        var programArguments = SystemValues.TextListAt(arguments, 1);
        var items = new List<Value>(programArguments.Count);
        foreach (var programArgument in programArguments)
        {
            items.Add(new StringValue(programArgument));
        }

        return new RecordValue(
            "run_request",
            new StringValue(SystemValues.TextAt(arguments, 0)),
            new ListValue(items),
            new StringValue(""),
            new PathValue(""),
            new MapValue(),
            IntegerValue.From(0),
            new PathValue(""));
    }

    /// <summary>
    /// run_request.with_standard_input: a copy with another standard input ("" closes it) and no input file; the
    /// two input fields are mutually exclusive, so the last call wins.
    /// </summary>
    private static Value WithStandardInput(RuntimeContext context, Value[] arguments) =>
        CopyRequest(
            (RecordValue)arguments[0],
            standardInput: new StringValue(SystemValues.TextAt(arguments, 1)),
            standardInputFile: new PathValue(""));

    /// <summary>
    /// run_request.with_standard_input_from_file: a copy whose standard input is the regular file at the path (the
    /// string input is cleared). The file is opened and validated at launch, never here.
    /// </summary>
    private static Value WithStandardInputFromFile(RuntimeContext context, Value[] arguments) =>
        CopyRequest(
            (RecordValue)arguments[0],
            standardInput: new StringValue(""),
            standardInputFile: new PathValue(SystemValues.PathAt(arguments, 1)));

    /// <summary>run_request.with_working_directory: a copy with another working directory (p"" inherits).</summary>
    private static Value WithWorkingDirectory(RuntimeContext context, Value[] arguments) =>
        CopyRequest((RecordValue)arguments[0], workingDirectory: new PathValue(SystemValues.PathAt(arguments, 1)));

    /// <summary>run_request.with_environment: a copy with an environment overlay ({} inherits).</summary>
    private static Value WithEnvironment(RuntimeContext context, Value[] arguments) =>
        CopyRequest((RecordValue)arguments[0], environment: (MapValue)((MapValue)arguments[1]).DeepCopy());

    /// <summary>run_request.with_timeout: a copy with another deadline (0 = none; negative rejected at launch).</summary>
    private static Value WithTimeout(RuntimeContext context, Value[] arguments) =>
        CopyRequest((RecordValue)arguments[0], timeout: IntegerValue.From(SystemValues.IntegerAt(arguments, 1)));

    /// <summary>Value semantics (DESIGN 3.6): the copy shares nothing mutable with the receiver.</summary>
    private static RecordValue CopyRequest(
        RecordValue request,
        StringValue? standardInput = null,
        PathValue? standardInputFile = null,
        PathValue? workingDirectory = null,
        MapValue? environment = null,
        IntegerValue? timeout = null) =>
        new(
            "run_request",
            request.Get("program"),
            ((ListValue)request.Get("arguments")).DeepCopy(),
            standardInput ?? request.Get("standard_input"),
            workingDirectory ?? request.Get("working_directory"),
            environment ?? ((MapValue)request.Get("environment_overlay")).DeepCopy(),
            timeout ?? request.Get("timeout_milliseconds"),
            standardInputFile ?? request.Get("standard_input_file"));

    /// <summary>process.script_path: the entry script's absolute canonical path, fixed before execution.</summary>
    private static Value ScriptPath(RuntimeContext context, Value[] arguments) => new PathValue(context.ScriptPath);

    /// <summary>process.run: one request in its own context; a non-zero exit code is a value (SPEC-STDLIB §7.3).</summary>
    private static Value Run(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardProcess(() => SystemValues.Success(
            ProcessOperations.ExecuteRequest(context, RunRequestData.FromRecord((RecordValue)arguments[0]))));

    /// <summary>process.run_inherit: one request sharing scripts's streams, returning the child's exit code (SPEC-STDLIB §7.3).</summary>
    private static Value RunInherit(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardProcess(() => SystemValues.Success(IntegerValue.From(
            ProcessOperations.ExecuteRequestInheriting(context, RunRequestData.FromRecord((RecordValue)arguments[0])))));

    /// <summary>
    /// process_result.require_success(): ok(receiver) when exit_code == 0, otherwise
    /// error(kind=other, subject=program) whose message cites the exit code and, when present, the
    /// first non-blank stderr line.
    /// </summary>
    private static Value RequireSuccess(RuntimeContext context, Value[] arguments)
    {
        var result = (RecordValue)arguments[0];
        var exitCode = result.GetInteger("exit_code");
        if (exitCode == 0)
        {
            return ResultValue.Ok(result);
        }

        return RequireSuccessFailure(result.GetText("program"), exitCode, result.GetText("standard_error"));
    }

    /// <summary>
    /// pipeline_result.require_success(): ok(receiver) when every stage exited with code 0, otherwise the
    /// failure of the first stage in request order whose exit_code is not 0 (,
    /// `pipeline_result.require_success`;).
    /// </summary>
    private static Value RequirePipelineSuccess(RuntimeContext context, Value[] arguments)
    {
        var pipeline = (RecordValue)arguments[0];
        foreach (var stage in ((ListValue)pipeline.Get("stage_results")).Items)
        {
            var stageResult = (RecordValue)stage;
            var exitCode = stageResult.GetInteger("exit_code");
            if (exitCode != 0)
            {
                return RequireSuccessFailure(stageResult.GetText("program"), exitCode, stageResult.GetText("standard_error"));
            }
        }

        return ResultValue.Ok(pipeline);
    }

    /// <summary>The shared failure message of the require_success family: exit code and the stderr excerpt.</summary>
    private static Value SetUmask(RuntimeContext context, Value[] arguments)
    {
        var mask = SystemValues.IntegerAt(arguments, 0);
        if (mask is < 0 or > 0x1FF)
        {
            return ResultValue.Error(ErrorValues.Error(
                "invalid_argument",
                mask.ToString(CultureInfo.InvariantCulture),
                "the umask must be in 0..0o777"));
        }

        var previous = Libc.Umask((uint)mask);
        return ResultValue.Ok(IntegerValue.From(previous));
    }

    private static Value RequireSuccessFailure(string program, long exitCode, string standardError)
    {
        var message = $"the program `{program}` exited with code {exitCode}";
        var excerpt = StderrExcerpt(standardError);
        if (excerpt.Length > 0)
        {
            message += $": {excerpt}";
        }

        return ResultValue.Error(ErrorValues.Error("other", program, message));
    }

    /// <summary>First non-blank line of the child's stderr, trimmed and truncated, or "" when stderr is blank.</summary>
    private static string StderrExcerpt(string standardError)
    {
        const int maximumLength = 120;
        var remaining = standardError.AsSpan();
        while (!remaining.IsEmpty)
        {
            var lineEnd = remaining.IndexOf('\n');
            var line = lineEnd < 0 ? remaining : remaining[..lineEnd];
            remaining = lineEnd < 0 ? default : remaining[(lineEnd + 1)..];
            var trimmed = line.Trim();
            if (trimmed.IsEmpty)
            {
                continue;
            }

            var length = Math.Min(trimmed.Length, maximumLength);
            // Never cut between the two halves of a surrogate pair.
            if (length < trimmed.Length && char.IsHighSurrogate(trimmed[length - 1]))
            {
                length--;
            }

            return length < trimmed.Length ? trimmed[..length].ToString() + "…" : trimmed.ToString();
        }

        return string.Empty;
    }

    private static Value ExecuteShell(RuntimeContext context, Value[] arguments)
    {
        context.StandardOutput.Flush();
        var commandLine = SystemValues.TextAt(arguments, 0);
        return SystemValues.GuardProcess(() =>
            SystemValues.Success(ProcessOperations.Execute(context, ShellProgram, ["-c", commandLine], null, null)));
    }

    private static Value Which(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardProcess(() => SystemValues.Success(new PathValue(ProgramResolver.Which(SystemValues.TextAt(arguments, 0)))));

    private static Value Spawn(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardProcess(() =>
            SystemValues.Success(ProcessOperations.Spawn(context, SystemValues.TextAt(arguments, 0), SystemValues.TextListAt(arguments, 1))));

    private static Value WaitForProcess(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardProcess(() => SystemValues.Success(ProcessOperations.WaitForSpawned(
            context,
            ProcessIdentifierOf(arguments[0]),
            SystemValues.IntegerAt(arguments, 1))));

    private static Value KillProcess(RuntimeContext context, Value[] arguments) =>
        SystemValues.GuardProcess(() =>
        {
            ProcessOperations.KillSpawned(context, ProcessIdentifierOf(arguments[0]));
            return SystemValues.Success();
        });

    private static Value ScriptArguments(RuntimeContext context, Value[] arguments)
    {
        var items = new List<Value>(context.ScriptArguments.Count);
        foreach (var argument in context.ScriptArguments)
        {
            items.Add(new StringValue(argument));
        }

        return new ListValue(items);
    }

    private static Value ExitScript(RuntimeContext context, Value[] arguments)
    {
        var exitCode = SystemValues.IntegerAt(arguments, 0);
        throw exitCode is < 0 or > MaximumExitScriptCode
            ? new RuntimeFailure("SCRIPTS-RUNTIME-0016", ("code", SystemValues.Invariant(exitCode)), ("range", "0..255"))
            : new ScriptExit((int)exitCode);
    }

    /// <summary>L7: writes the message and a newline on stderr (as `io.write_standard_error_line`), then exits like `exit_with_code`.</summary>
    private static Value ExitWithMessage(RuntimeContext context, Value[] arguments)
    {
        var exitCode = SystemValues.IntegerAt(arguments, 0);
        if (exitCode is < 1 or > MaximumExitScriptCode)
        {
            throw new RuntimeFailure("SCRIPTS-RUNTIME-0016", ("code", SystemValues.Invariant(exitCode)), ("range", "1..255"));
        }

        context.StandardOutput.Flush();
        context.StandardError.Write(SystemValues.TextAt(arguments, 1));
        context.StandardError.Write("\n");
        context.StandardError.Flush();
        throw new ScriptExit((int)exitCode);
    }

    /// <summary>
    /// process.pipeline: the pipeline shared with pipeline_to_file_overwrite, with the last stage's stdout captured
    /// in memory and decoded strictly (SPEC-STDLIB §7.10). A non-zero exit code is a value; only a launch, deadline
    /// or decoding failure is an error.
    /// </summary>
    private static Value Pipeline(RuntimeContext context, Value[] arguments) =>
        ProcessPipeline.RunToMemory(context, PipelineStages(arguments));

    /// <summary>
    /// process.pipeline_to_file_overwrite: flattens the list of run_request records and runs the pipeline
    /// (SPEC-STDLIB §7.10). A non-zero exit code is a value; only a launch or file failure is an error.
    /// </summary>
    private static Value PipelineToFileOverwrite(RuntimeContext context, Value[] arguments) =>
        ProcessPipeline.RunToFile(context, PipelineStages(arguments), SystemValues.PathAt(arguments, 1));

    /// <summary>Flattens the run_request records of the first argument into the launcher's stage records.</summary>
    private static List<PipelineStage> PipelineStages(Value[] arguments)
    {
        var requests = ((ListValue)arguments[0]).Items;
        var stages = new List<PipelineStage>(requests.Count);
        foreach (var request in requests)
        {
            var data = RunRequestData.FromRecord((RecordValue)request);
            stages.Add(new PipelineStage(data.Program, data.Arguments, data.StandardInput, data.StandardInputFile, data.LaunchContext, data.TimeoutMilliseconds));
        }

        return stages;
    }

    private static long ProcessIdentifierOf(Value handle) => ((RecordValue)handle).GetInteger("process_identifier");
}
