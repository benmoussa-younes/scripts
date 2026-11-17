using System.Diagnostics;
using System.Runtime.Versioning;
using Scripts.Runtime;
using Scripts.Runtime.Output;

[assembly: SupportedOSPlatform("linux")]

namespace Scripts.Runtime.Tests.SystemFunctions;

/// <summary>All system tests share one collection: they change the process working directory and environment.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SystemTestCollection
{
    public const string Name = "System stdlib (serial)";
}

/// <summary>Per-test scratch directory under the temp root; permissions are restored before deletion.</summary>
public sealed class SystemTestWorkspace : IDisposable
{
    public SystemTestWorkspace() => Root = Directory.CreateTempSubdirectory("scripts-system-test-").FullName;

    public string Root { get; }

    public string Combine(params string[] segments) => Path.Combine([Root, .. segments]);

    public string Write(string relativeName, string content)
    {
        var path = Combine(relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        RestorePermissions(Root);
        Scripts.Runtime.Platform.FileSystemMutations.RemoveDirectoryTree(Root, () => { });
    }

    private static void RestorePermissions(string directory)
    {
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            try
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    RestorePermissions(entry);
                }
                else
                {
                    File.SetUnixFileMode(entry, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch (IOException)
            {
                // Names that are not valid UTF-8 cannot be addressed by System.IO; the cleanup below works on raw bytes.
            }
        }
    }
}

/// <summary>Calls stdlib implementations through the registry, like the interpreter does.</summary>
public static class SystemCalls
{
    public static RuntimeContext NewContext(out CapturedOutput captured, params string[] scriptArguments) =>
        RuntimeContext.CreateForCapture(scriptArguments, out captured, listenForSignals: false);

    public static RuntimeContext NewContext(params string[] scriptArguments) => NewContext(out _, scriptArguments);

    public static Value Call(RuntimeContext context, string function, params Value[] arguments) =>
        StdlibRegistry.Get(function)(context, arguments);

    public static Value Call(string function, params Value[] arguments)
    {
        using var context = NewContext();
        return Call(context, function, arguments);
    }

    public static StringValue Text(string text) => new(text);

    public static PathValue PathOf(string text) => new(text);

    public static IntegerValue Integer(long number) => new(number);

    public static ListValue TextList(params string[] texts) => new([.. texts.Select(text => (Value)new StringValue(text))]);

    /// <summary>Builds a minimal <c>run_request</c> through <c>process.request</c> (empty environment, no deadline).</summary>
    public static RecordValue Request(
        RuntimeContext context,
        string program,
        string[] arguments,
        string standardInput = "",
        string workingDirectory = "",
        long timeoutMilliseconds = 0,
        string standardInputFile = "")
    {
        var request = Assert.IsType<RecordValue>(Call(context, "process.request", Text(program), TextList(arguments)));
        request = Assert.IsType<RecordValue>(Call(context, "run_request.with_standard_input", request, Text(standardInput)));
        if (standardInputFile.Length != 0)
        {
            request = Assert.IsType<RecordValue>(Call(
                context,
                "run_request.with_standard_input_from_file",
                request,
                PathOf(standardInputFile)));
        }

        request = Assert.IsType<RecordValue>(Call(context, "run_request.with_working_directory", request, PathOf(workingDirectory)));
        return timeoutMilliseconds == 0
            ? request
            : Assert.IsType<RecordValue>(Call(context, "run_request.with_timeout", request, Integer(timeoutMilliseconds)));
    }

    public static Value RunRequest(
        RuntimeContext context,
        string program,
        string[] arguments,
        string standardInput = "",
        string workingDirectory = "",
        long timeoutMilliseconds = 0,
        string standardInputFile = "") =>
        Call(context, "process.run", Request(context, program, arguments, standardInput, workingDirectory, timeoutMilliseconds, standardInputFile));

    public static Value RunInheritRequest(
        RuntimeContext context,
        string program,
        string[] arguments,
        string standardInput = "",
        string workingDirectory = "",
        long timeoutMilliseconds = 0,
        string standardInputFile = "") =>
        Call(context, "process.run_inherit", Request(context, program, arguments, standardInput, workingDirectory, timeoutMilliseconds, standardInputFile));

    public static T Ok<T>(Value result)
        where T : Value
    {
        var value = Assert.IsType<ResultValue>(result);
        Assert.True(value.IsOk, value.IsOk ? string.Empty : Describe(value.Payload));
        return Assert.IsType<T>(value.Payload);
    }

    public static void OkUnit(Value result) => Ok<UnitValue>(result);

    public static RecordValue ErrorRecord(Value result, string typeName)
    {
        var value = Assert.IsType<ResultValue>(result);
        Assert.False(value.IsOk, "unexpected ok");
        var record = Assert.IsType<RecordValue>(value.Payload);
        Assert.Equal(typeName, record.TypeName);
        return record;
    }

    public static void AssertIoError(Value result, string kind, string? subject = null)
    {
        var record = ErrorRecord(result, "error");
        Assert.Equal(kind, record.GetText("kind"));
        if (subject is not null)
        {
            Assert.Equal(subject, record.GetText("subject"));
        }

        Assert.False(string.IsNullOrEmpty(record.GetText("message")));
    }

    public static void AssertProcessError(Value result, string kind, string? program = null)
    {
        var record = ErrorRecord(result, "error");
        Assert.Equal(kind, record.GetText("kind"));
        if (program is not null)
        {
            Assert.Equal(program, record.GetText("subject"));
        }

        Assert.False(string.IsNullOrEmpty(record.GetText("message")));
    }

    public static string[] PathTexts(Value result) =>
        [.. Ok<ListValue>(result).Items.Select(item => Assert.IsType<PathValue>(item).Text)];

    public static string Describe(Value value) => value switch
    {
        RecordValue record => record.TypeName + " " + record.GetText("message"),
        StringValue text => text.Text,
        _ => value.GetType().Name,
    };

    /// <summary>Runs a test body from a directory that no test deletes, restoring the previous working directory afterwards.</summary>
    public static void WithWorkingDirectory(string directory, Action body)
    {
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(directory);
        try
        {
            body();
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    public static void WithEnvironment(string name, string? value, Action body)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    public static bool IsAlive(int processIdentifier) => Directory.Exists($"/proc/{processIdentifier}") && !IsZombie(processIdentifier);

    private static bool IsZombie(int processIdentifier)
    {
        try
        {
            var status = File.ReadAllText($"/proc/{processIdentifier}/stat");
            return status[(status.LastIndexOf(')') + 2)..].StartsWith('Z');
        }
        catch (IOException)
        {
            return true;
        }
    }

    public static bool WaitUntil(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }
}
