using System.Diagnostics;
using System.Text;

namespace Scripts.Corpus.Tests;

/// <summary>
/// xUnit driver for the command/document corpus (JSON and describe). The command
/// line comes from <c>.cmd</c>; the structural `check:` assertions are evaluated
/// by a small Python helper, as in the former shell runner. No shell runner is
/// shipped: this class is the test, and the data lives under tests/Corpus.
/// </summary>
[Collection("corpus")]
public sealed class CommandCorpusTests
{
    public static TheoryData<string, string, string> Cases()
    {
        var root = CorpusPaths.RepositoryRoot;
        var data = new TheoryData<string, string, string>();
        foreach (var family in new[] { "json", "describe" })
        {
            var directory = Path.Combine(root, "tests", "Corpus", family);
            foreach (var expect in Directory.EnumerateFiles(directory, "*.expect").Order(StringComparer.Ordinal))
            {
                data.Add(family, Path.GetFileNameWithoutExtension(expect), directory);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case(string family, string name, string directory)
    {
        var expectation = CommandExpectation.Read(Path.Combine(directory, name + ".expect"));
        var commandFile = Path.Combine(directory, name + ".cmd");
        Assert.True(File.Exists(commandFile), $"{family}/{name}: missing .cmd");
        var command = File.ReadAllLines(commandFile)[0];

        var work = Directory.CreateTempSubdirectory("scripts-command-");
        try
        {
            CopyTree(Path.Combine(directory, name + ".files"), work.FullName);
            foreach (var source in Directory.EnumerateFiles(directory, "*.scs"))
            {
                File.Copy(source, Path.Combine(work.FullName, Path.GetFileName(source)));
            }

            var binary = CorpusPaths.BinaryPath();
            var version = VersionOf(binary);
            var tokens = command == "scripts" ? string.Empty : command["scripts ".Length..];
            var (exitCode, standardOutput, standardError) = Run(work.FullName, binary, tokens, expectation.Signal);

            if (expectation.ExitCode is { } expectedExit)
            {
                Assert.True(exitCode == expectedExit, $"{family}/{name}: exit {exitCode}, expected {expectedExit}\n{standardError}");
            }

            var stream = expectation.Stream == "stderr" ? standardError : standardOutput;
            foreach (var literal in expectation.TextContains)
            {
                Assert.True(stream.Contains(literal.Replace("@VERSION@", version, StringComparison.Ordinal), StringComparison.Ordinal),
                    $"{family}/{name}: expected text absent: {literal}");
            }

            if (expectation.TextEquals is { } exact)
            {
                exact = exact.Replace("@VERSION@", version, StringComparison.Ordinal);
                Assert.True(stream == exact || stream.TrimEnd('\n') == exact,
                    $"{family}/{name}: stream differs from text-equals\n--- expected ---\n{exact}\n--- actual ---\n{stream}");
            }

            if (expectation.Deterministic is { } runs && runs > 1)
            {
                for (var run = 1; run < runs; run++)
                {
                    var repeat = Run(work.FullName, binary, tokens, expectation.Signal);
                    Assert.True(repeat.StandardOutput == standardOutput, $"{family}/{name}: run {run + 1} differs");
                }
            }

            if (expectation.Checks.Count > 0)
            {
                var rawPath = Path.Combine(work.FullName, ".stream");
                File.WriteAllText(rawPath, stream);
                var (pythonExit, pythonOutput, pythonError) = RunPython(
                    Path.Combine(directory, name + ".expect"), rawPath, $"{family}/{name}", version);
                Assert.True(pythonExit == 0, $"{family}/{name}: JSON assertions failed\n{pythonOutput}{pythonError}");
            }
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    private static (int ExitCode, string StandardOutput, string StandardError) Run(
        string workingDirectory, string binary, string tokens, Signal? signal)
    {
        string shell;
        if (signal is { } requested)
        {
            shell = $"exec \"{binary}\" {tokens} < /dev/null & child=$!; sleep {requested.DelayMilliseconds / 1000.0:0.###}; kill -{requested.Name} $child; wait $child";
        }
        else
        {
            shell = $"exec \"{binary}\" {tokens} < /dev/null";
        }

        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(shell);
        using var process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunPython(
        string expectPath, string rawPath, string label, string version)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/python3")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(PythonChecks);
        startInfo.ArgumentList.Add(expectPath);
        startInfo.ArgumentList.Add(rawPath);
        startInfo.ArgumentList.Add(label);
        startInfo.ArgumentList.Add(version);
        using var process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }

    private static string VersionOf(string binary)
    {
        var startInfo = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--version");
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var parts = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[1].Trim() : string.Empty;
    }

    private static void CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(entry));
            var link = new FileInfo(entry).LinkTarget;
            if (link is not null)
            {
                File.CreateSymbolicLink(target, link);
            }
            else if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(target);
                CopyTree(entry, target);
            }
            else
            {
                File.Copy(entry, target);
            }
        }
    }

    private sealed record Signal(string Name, int DelayMilliseconds);

    private sealed record CommandExpectation(
        int? ExitCode,
        string Stream,
        string? TextEquals,
        IReadOnlyList<string> TextContains,
        IReadOnlyList<string> Checks,
        int? Deterministic,
        Signal? Signal)
    {
        public static CommandExpectation Read(string path)
        {
            int? exitCode = null;
            var stream = "stdout";
            string? textEquals = null;
            var textContains = new List<string>();
            var checks = new List<string>();
            int? deterministic = null;
            Signal? signal = null;
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (line.StartsWith("exit:", StringComparison.Ordinal))
                {
                    exitCode = int.Parse(line["exit:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                }
                else if (line.StartsWith("stream:", StringComparison.Ordinal))
                {
                    stream = line["stream:".Length..].Trim();
                }
                else if (line.StartsWith("text-equals:", StringComparison.Ordinal))
                {
                    textEquals = line["text-equals:".Length..].TrimStart();
                }
                else if (line.StartsWith("text-contains:", StringComparison.Ordinal))
                {
                    textContains.Add(line["text-contains:".Length..].TrimStart());
                }
                else if (line.StartsWith("check:", StringComparison.Ordinal))
                {
                    checks.Add(line["check:".Length..]);
                }
                else if (line.StartsWith("deterministic:", StringComparison.Ordinal))
                {
                    deterministic = int.Parse(line["deterministic:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                }
                else if (line.StartsWith("signal:", StringComparison.Ordinal))
                {
                    var parts = line["signal:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        signal = new Signal(parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }

            return new CommandExpectation(exitCode, stream, textEquals, textContains, checks, deterministic, signal);
        }
    }

    private const string PythonChecks = """
import json
import sys

expect_path, raw_path, label, version = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
with open(expect_path, "r", encoding="utf-8") as handle:
    expect_lines = handle.read().splitlines()
with open(raw_path, "r", encoding="utf-8") as handle:
    raw = handle.read()

stream = "stdout"
json_last = False
checks = []
for line in expect_lines:
    if line.startswith("stream:"):
        stream = line[len("stream:"):].strip()
    elif line.strip() == "json-last":
        json_last = True
    elif line.startswith("check:"):
        checks.append(line[len("check:"):].strip())

document_text = raw
if json_last:
    candidate_lines = [line for line in raw.splitlines() if line.strip() != ""]
    if not candidate_lines:
        print(f"FAIL {label} (no non-empty line for json-last)")
        sys.exit(1)
    document_text = candidate_lines[-1]

doc = None
if checks:
    try:
        doc = json.loads(document_text)
    except Exception as error:
        print(f"FAIL {label} (invalid JSON: {error})")
        sys.exit(1)

environment = {"doc": doc, "raw": raw, "json": json, "version": version}
failed = []
for expression in checks:
    try:
        if not eval(expression, environment):
            failed.append(expression)
    except Exception as error:
        failed.append(f"{expression}  -> error: {error}")

if failed:
    print(f"FAIL {label} (assertion(s) false)")
    for expression in failed:
        print(f"    check: {expression}")
    print(f"    document: {document_text[:400]}")
    sys.exit(1)
sys.exit(0)
""";
}
