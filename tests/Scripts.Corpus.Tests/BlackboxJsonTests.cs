using System.Text;
using System.Text.Json;

namespace Scripts.Corpus.Tests;

/// <summary>
/// xUnit port of the black-box machine-output axis: the describe document is
/// deterministic and complete, <c>--json-out</c> carries the DESIGN §8.3/§8.4
/// fields on success and every failure, escaping is valid, and standard input is
/// named <c>&lt;stdin&gt;</c> in both documents.
/// </summary>
[Collection("corpus")]
public sealed class BlackboxJsonTests
{
    private static readonly string[] DescribeKeys =
    [
        "language", "version", "schema_version", "keywords", "reserved_identifiers",
        "future_reserved", "operators", "precedence", "types", "functions",
        "error_codes", "exit_codes", "limits", "grammar_ebnf",
    ];

    private static readonly string[] FunctionKeys =
    [
        "qualified_name", "kind", "receiver", "parameters", "returns", "error_types",
        "error_kinds", "dangerous", "destructive", "deterministic", "effects", "description",
    ];

    [Fact(DisplayName = "describe_deterministic")]
    public void DescribeDeterministic()
    {
        var first = RunBinary(["describe", "--format", "json"]);
        var second = RunBinary(["describe", "--format", "json"]);
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.True(
            first.StandardOutput == second.StandardOutput,
            "describe_deterministic: two different runs");
        BlackboxRunner.AssertNoTrace(first, "describe_deterministic");
        BlackboxRunner.AssertNoTrace(second, "describe_deterministic");
    }

    [Fact(DisplayName = "describe_schema")]
    public void DescribeSchema()
    {
        var result = RunBinary(["describe", "--format", "json"]);
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        foreach (var key in DescribeKeys)
        {
            Assert.True(root.TryGetProperty(key, out _), $"describe_schema: missing key {key}");
        }

        Assert.Equal("scripts", root.GetProperty("language").GetString());
        Assert.Equal(VersionOf(), root.GetProperty("version").GetString());
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        var functions = root.GetProperty("functions");
        Assert.True(functions.GetArrayLength() >= 85, $"describe_schema: {functions.GetArrayLength()} functions");
        var errorCodes = root.GetProperty("error_codes");
        Assert.True(errorCodes.GetArrayLength() >= 154, $"describe_schema: {errorCodes.GetArrayLength()} error codes");
        var function = functions[0];
        foreach (var key in FunctionKeys)
        {
            Assert.True(function.TryGetProperty(key, out _), $"describe_schema: function missing key {key}");
        }

        BlackboxRunner.AssertNoTrace(result, "describe_schema");
    }

    [Fact(DisplayName = "json_out_success")]
    public void JsonOutSuccess()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-json-");
        WriteSource(work.Path, "ok.scs", """
            io.write_standard_output_line("sortie")
            return 42
            """);
        var result = RunJsonOut(work.Path, "ok.scs");
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(0, root.GetProperty("exit_code").GetInt32());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("normal", root.GetProperty("termination").GetString());
        Assert.Equal("sortie\n", root.GetProperty("stdout").GetString());
        Assert.Equal(string.Empty, root.GetProperty("stderr").GetString());
        Assert.Equal(42, root.GetProperty("result").GetInt32());
        Assert.Equal(0, root.GetProperty("errors").GetArrayLength());
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        BlackboxRunner.AssertNoTrace(result, "json_out_success");
    }

    [Fact(DisplayName = "json_out_static")]
    public void JsonOutStatic()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-json-");
        WriteSource(work.Path, "static.scs", """
            let x: string = 42
            """);
        var result = RunJsonOut(work.Path, "static.scs");
        Assert.Equal(251, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(251, root.GetProperty("exit_code").GetInt32());
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal("static_error", root.GetProperty("termination").GetString());
        var errors = root.GetProperty("errors");
        Assert.True(errors.GetArrayLength() >= 1, "json_out_static: no error");
        Assert.Equal("error", errors[0].GetProperty("severity").GetString());
        BlackboxRunner.AssertNoTrace(result, "json_out_static");
    }

    [Fact(DisplayName = "json_out_runtime")]
    public void JsonOutRuntime()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-json-");
        WriteSource(work.Path, "rt.scs", """
            let n = "9223372036854775807".parse_integer()?
            io.write_standard_output_line("{n + 1}")
            """);
        var result = RunJsonOut(work.Path, "rt.scs");
        Assert.Equal(253, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(253, root.GetProperty("exit_code").GetInt32());
        Assert.Equal("runtime_error", root.GetProperty("termination").GetString());
        Assert.False(root.GetProperty("success").GetBoolean());
        BlackboxRunner.AssertNoTrace(result, "json_out_runtime");
    }

    [Fact(DisplayName = "json_out_exit_script")]
    public void JsonOutExitScript()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-json-");
        WriteSource(work.Path, "es.scs", """
            io.write_standard_output_line("avant")
            process.exit_with_code(7)
            """);
        var result = RunJsonOut(work.Path, "es.scs");
        Assert.Equal(7, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(7, root.GetProperty("exit_code").GetInt32());
        Assert.Equal("exit_script", root.GetProperty("termination").GetString());
        Assert.Equal("avant\n", root.GetProperty("stdout").GetString());
        BlackboxRunner.AssertNoTrace(result, "json_out_exit_script");
    }

    [Fact(DisplayName = "check_json_success")]
    public void CheckJsonSuccess()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-json-");
        WriteSource(work.Path, "ok.scs", """
            io.write_standard_output_line("sortie")
            return 42
            """);
        var result = RunCheckJson(work.Path, "ok.scs");
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(0, root.GetProperty("errors").GetArrayLength());
        BlackboxRunner.AssertNoTrace(result, "check_json_success");
    }

    [Fact(DisplayName = "check_json_failure")]
    public void CheckJsonFailure()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-json-");
        WriteSource(work.Path, "static.scs", """
            let x: string = 42
            """);
        var result = RunCheckJson(work.Path, "static.scs");
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("errors").GetArrayLength() >= 1, "check_json_failure: no error");
        BlackboxRunner.AssertNoTrace(result, "check_json_failure");
    }

    [Fact(DisplayName = "json_escaping")]
    public void JsonEscaping()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-json-");
        WriteSource(work.Path, "esc.scs", """
            io.write_standard_output_line("tab\tquote\"back\\slash")
            """);
        var result = RunJsonOut(work.Path, "esc.scs");
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("tab\tquote\"back\\slash\n", document.RootElement.GetProperty("stdout").GetString());
        BlackboxRunner.AssertNoTrace(result, "json_escaping");
    }

    [Fact(DisplayName = "json_single_line")]
    public void JsonSingleLine()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-json-");
        WriteSource(work.Path, "ok.scs", """
            io.write_standard_output_line("sortie")
            return 42
            """);
        var result = RunJsonOut(work.Path, "ok.scs");
        var newlines = result.StandardOutput.Count(character => character == '\n');
        Assert.True(newlines <= 1, $"json_single_line: the document has {newlines} lines");
        BlackboxRunner.AssertNoTrace(result, "json_single_line");
    }

    [Fact(DisplayName = "json_result_structures")]
    public void JsonResultStructures()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-json-");
        WriteSource(work.Path, "structures.scs", """
            let m = {"nom": "Ada", "ville": "Paris"}
            return m
            """);
        var result = RunJsonOut(work.Path, "structures.scs");
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var resultElement = document.RootElement.GetProperty("result");
        Assert.Equal(JsonValueKind.Object, resultElement.ValueKind);
        Assert.Equal(2, resultElement.EnumerateObject().Count());
        Assert.Equal("Ada", resultElement.GetProperty("nom").GetString());
        Assert.Equal("Paris", resultElement.GetProperty("ville").GetString());
        BlackboxRunner.AssertNoTrace(result, "json_result_structures");
    }

    [Fact(DisplayName = "json_out_no_human")]
    public void JsonOutNoHuman()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-json-");
        WriteSource(work.Path, "ok.scs", """
            io.write_standard_output_line("sortie")
            return 42
            """);
        var result = RunJsonOut(work.Path, "ok.scs");
        var trimmed = result.StandardOutput.TrimEnd('\n');
        Assert.False(trimmed.Contains('\n'), "json_out_no_human: human output present");
        BlackboxRunner.AssertNoTrace(result, "json_out_no_human");
    }

    [Fact(DisplayName = "json_out_stdin")]
    public void JsonOutStdin()
    {
        var input = Encoding.UTF8.GetBytes("io.write_standard_output_line(\"sortie\")\nreturn 7\n");
        var result = RunBinary(["run", "--json-out", "-"], input);
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(0, root.GetProperty("exit_code").GetInt32());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("normal", root.GetProperty("termination").GetString());
        Assert.Equal("sortie\n", root.GetProperty("stdout").GetString());
        Assert.Equal(7, root.GetProperty("result").GetInt32());
        BlackboxRunner.AssertNoTrace(result, "json_out_stdin");
    }

    [Fact(DisplayName = "check_json_stdin")]
    public void CheckJsonStdin()
    {
        var input = Encoding.UTF8.GetBytes("let n: integer = \"abc\"\n");
        var result = RunBinary(["check", "--errors-json", "-"], input);
        Assert.Equal(251, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        var error = root.GetProperty("errors")[0];
        Assert.Equal("SCRIPTS-TYPE-0012", error.GetProperty("code").GetString());
        Assert.Equal("<stdin>", error.GetProperty("file").GetString());
        Assert.Equal(1, error.GetProperty("line").GetInt32());
        BlackboxRunner.AssertNoTrace(result, "check_json_stdin");
    }

    [Fact(DisplayName = "check_json_stdin_directory")]
    public void CheckJsonStdinDirectory()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-json-");
        var result = BlackboxRunner.RunShell(
            work.Path,
            "exec \"$1\" check --errors-json - < /",
            [BlackboxRunner.Binary],
            10_000);
        Assert.Equal(252, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        var error = root.GetProperty("errors")[0];
        Assert.Equal("SCRIPTS-CLI-0010", error.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, error.GetProperty("file").ValueKind);
        BlackboxRunner.AssertNoTrace(result, "check_json_stdin_directory");
    }

    [Fact(DisplayName = "json_out_stdin_directory")]
    public void JsonOutStdinDirectory()
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-json-");
        var result = BlackboxRunner.RunShell(
            work.Path,
            "exec \"$1\" run --json-out - < /",
            [BlackboxRunner.Binary],
            10_000);
        Assert.Equal(252, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(252, root.GetProperty("exit_code").GetInt32());
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal("static_error", root.GetProperty("termination").GetString());
        Assert.Equal("SCRIPTS-CLI-0010", root.GetProperty("errors")[0].GetProperty("code").GetString());
        BlackboxRunner.AssertNoTrace(result, "json_out_stdin_directory");
    }

    private static void WriteSource(string work, string name, string source)
    {
        File.WriteAllText(
            Path.Combine(work, name),
            source,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static BlackboxProcessResult RunJsonOut(string work, string name)
    {
        return BlackboxRunner.Run(
            BlackboxRunner.Binary,
            ["run", "--json-out", name],
            work,
            20_000);
    }

    private static BlackboxProcessResult RunCheckJson(string work, string name)
    {
        return BlackboxRunner.Run(
            BlackboxRunner.Binary,
            ["check", "--errors-json", name],
            work,
            20_000);
    }

    private static BlackboxProcessResult RunBinary(
        IReadOnlyList<string> arguments,
        byte[]? standardInput = null)
    {
        return BlackboxRunner.Run(
            BlackboxRunner.Binary,
            arguments,
            CorpusPaths.RepositoryRoot,
            20_000,
            standardInput);
    }

    private static string VersionOf()
    {
        var result = RunBinary(["--version"]);
        Assert.Equal(0, result.ExitCode);
        var parts = result.StandardOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(parts.Length > 1, $"describe_schema: cannot read the version from [{result.StandardOutput}]");
        return parts[1].Trim();
    }
}
