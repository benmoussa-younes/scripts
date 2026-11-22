using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Scripts.EndToEnd.Tests;

/// <summary>End-to-end checks on the real scripts process: commands, exit codes of DESIGN §6, JSON documents.</summary>
public sealed class CommandEndToEndTests
{
    [Fact]
    public void The_documented_env_s_shebang_runs_a_script()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = Path.Combine(directory, "hello.scs");
            File.WriteAllText(script, "#!/usr/bin/env -S scripts run\nio.write_standard_output_line(\"shebang\")\n");
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            var startInfo = new ProcessStartInfo(script)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.Environment["PATH"] =
                Path.GetDirectoryName(ScriptsProcess.Binary) + ":" + Environment.GetEnvironmentVariable("PATH");

            using var process = Process.Start(startInfo)!;
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("shebang\n", standardOutput);
            Assert.Equal(string.Empty, standardError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Run_resolves_a_name_without_extension_to_the_scs_source()
    {
        var directory = Directory.CreateTempSubdirectory("scripts-e2e-resolve-");
        try
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, "hello.scs"),
                "io.write_standard_output_line(\"resolved\")\n");

            var result = ScriptsProcess.RunInDirectoryWithStandardInput(
                directory.FullName, string.Empty, "run", "hello");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("resolved\n", result.StandardOutput);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Run_prefers_an_exact_path_over_the_scs_suffix()
    {
        var directory = Directory.CreateTempSubdirectory("scripts-e2e-exact-");
        try
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, "hello"),
                "io.write_standard_output_line(\"exact\")\n");
            File.WriteAllText(
                Path.Combine(directory.FullName, "hello.scs"),
                "io.write_standard_output_line(\"suffix\")\n");

            var result = ScriptsProcess.RunInDirectoryWithStandardInput(
                directory.FullName, string.Empty, "run", "hello");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("exact\n", result.StandardOutput);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Run_reports_a_missing_name_as_typed()
    {
        var directory = Directory.CreateTempSubdirectory("scripts-e2e-missing-");
        try
        {
            var result = ScriptsProcess.RunInDirectoryWithStandardInput(
                directory.FullName, string.Empty, "run", "absent");

            Assert.Equal(252, result.ExitCode);
            Assert.Contains("`absent`", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Version_prints_the_version_and_exits_zero()
    {
        var result = ScriptsProcess.Run("--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"scripts {Scripts.Core.LanguageReference.Version}\n", result.StandardOutput);
    }

    [Fact]
    public void Help_on_standard_output_exits_zero()
    {
        var result = ScriptsProcess.Run("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("USAGE", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("scripts run", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_arguments_reports_cli_0001_on_standard_error_with_code_250()
    {
        var result = ScriptsProcess.Run();

        Assert.Equal(250, result.ExitCode);
        Assert.Contains("SCRIPTS-CLI-0001", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardOutput);
    }

    [Fact]
    public void A_cli_parse_error_under_json_out_writes_a_static_error_document()
    {
        var result = ScriptsProcess.Run("run", "--json-out", "--bogus", "script.scs");

        Assert.Equal(250, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(250, document.RootElement.GetProperty("exit_code").GetInt32());
        Assert.Equal("static_error", document.RootElement.GetProperty("termination").GetString());
        Assert.Contains("SCRIPTS-CLI-0003", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public void A_cli_parse_error_under_errors_json_appends_its_json_line_on_standard_error()
    {
        var result = ScriptsProcess.Run("run", "--errors-json", "--bogus", "script.scs");

        Assert.Equal(250, result.ExitCode);
        Assert.Contains("SCRIPTS-CLI-0003", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("\"success\":false", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_writes_the_language_document_on_standard_output()
    {
        var result = ScriptsProcess.Run("describe", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("scripts", document.RootElement.GetProperty("language").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("schema_version").GetInt32());
        Assert.True(document.RootElement.GetProperty("functions").GetArrayLength() >= Scripts.Core.StdlibCatalog.All.Count);
        Assert.Equal(
            Scripts.Core.ErrorCatalog.All.Count,
            document.RootElement.GetProperty("error_codes").GetArrayLength());
    }

    [Fact]
    public void Run_executes_a_valid_script_with_code_zero()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "ok.scs", "io.write_standard_output_line(\"hello\")\n");
            var result = ScriptsProcess.Run("run", script);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("hello\n", result.StandardOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Run_on_a_static_error_reports_code_251_and_executes_nothing()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "bad.scs", "io.write_standard_output_line(\"before\")\nlet n = \"abc\".size()\nio.write_standard_output_line(\"after\")\n");
            var result = ScriptsProcess.Run("run", script);

            Assert.Equal(251, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Contains("SCRIPTS-TYPE-0008", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_applies_a_safe_rename_and_rechecks()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "fix.scs", "let total = 1\nio.write_standard_output_line(\"{totl}\")\n");
            var result = ScriptsProcess.Run("check", "--fix", script);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal($"applied 1 fix(es) to {script}\n", result.StandardOutput);
            Assert.Equal("let total = 1\nio.write_standard_output_line(\"{total}\")\n", File.ReadAllText(script));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_renames_unread_bindings_so_deny_warnings_passes()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            // L5: SCRIPTS-LINT-0013 carries a rename-to-`_name` fix; once applied the script checks with --deny-warnings.
            var script = ScriptsProcess.WriteScript(
                directory,
                "unread.scs",
                "let name = \"a\".trim_whitespace()\nfor index in range(0, 2) {\n    io.write_standard_output_line(\"t\")\n}\n");
            Assert.Equal(251, ScriptsProcess.Run("check", "--deny-warnings", script).ExitCode);

            var fixing = ScriptsProcess.Run("check", "--fix", script);

            Assert.Equal(0, fixing.ExitCode);
            Assert.Equal($"applied 2 fix(es) to {script}\n", fixing.StandardOutput);
            Assert.Equal(
                "let _name = \"a\".trim_whitespace()\nfor _index in range(0, 2) {\n    io.write_standard_output_line(\"t\")\n}\n",
                File.ReadAllText(script));
            Assert.Equal(0, ScriptsProcess.Run("check", "--deny-warnings", script).ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_rewrites_double_braces_then_applies_checker_fixes()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            // L6: each `{{` string is a parse error with its own fix; the checker's rename is applied after them.
            var script = ScriptsProcess.WriteScript(
                directory,
                "fix.scs",
                "let total = 1\nlet a = \"a {{total}}\"\nlet b = \"b {{x}} {total}\"\nio.write_standard_output_line(\"{totl}{a}{b}\")\n");
            var result = ScriptsProcess.Run("check", "--fix", script);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal($"applied 3 fix(es) to {script}\n", result.StandardOutput);
            Assert.Equal(
                "let total = 1\nlet a = \"a \\{total\\}\"\nlet b = \"b \\{x\\} {total}\"\nio.write_standard_output_line(\"{total}{a}{b}\")\n",
                File.ReadAllText(script));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_escapes_only_the_failing_double_brace_segment()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "mixed.scs",
                "let a = 1\nlet n = \"{{a: 1}.length()} {{y}}\"\nio.write_standard_output_line(\"{n}\")\n");
            var result = ScriptsProcess.Run("check", "--fix", script);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal($"applied 1 fix(es) to {script}\n", result.StandardOutput);
            Assert.Equal(
                "let a = 1\nlet n = \"{{a: 1}.length()} \\{y\\}\"\nio.write_standard_output_line(\"{n}\")\n",
                File.ReadAllText(script));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_turns_a_stray_single_brace_into_text()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "stray.scs",
                "let a = 1\nlet s = \"{{{a} x}}\"\nio.write_standard_output_line(\"{a} {s}\")\n");
            var result = ScriptsProcess.Run("check", "--fix", script);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal($"applied 1 fix(es) to {script}\n", result.StandardOutput);
            Assert.Equal(
                "let a = 1\nlet s = \"\\{\\{a\\} x\\}\"\nio.write_standard_output_line(\"{a} {s}\")\n",
                File.ReadAllText(script));
            Assert.Equal("1 {{a} x}\n", ScriptsProcess.Run("run", script).StandardOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_resolves_a_name_without_extension_and_names_the_file_it_wrote()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "fix.scs", "let total = 1\nio.write_standard_output_line(\"{totl}\")\n");
            var result = ScriptsProcess.RunInDirectoryWithStandardInput(
                directory, string.Empty, "check", "--fix", "fix");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("applied 1 fix(es) to fix.scs\n", result.StandardOutput);
            Assert.Equal("let total = 1\nio.write_standard_output_line(\"{total}\")\n", File.ReadAllText(script));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_preserves_a_byte_order_mark()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = Path.Combine(directory, "bom.scs");
            var original = "let total = 1\nio.write_standard_output_line(\"{totl}\")\n";
            var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(original)).ToArray();
            File.WriteAllBytes(script, withBom);

            var result = ScriptsProcess.Run("check", "--fix", script);

            Assert.Equal(0, result.ExitCode);
            var bytes = File.ReadAllBytes(script);
            Assert.True(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
            Assert.Equal(
                "\uFEFFlet total = 1\nio.write_standard_output_line(\"{total}\")\n",
                Encoding.UTF8.GetString(bytes));

            var check = ScriptsProcess.Run("check", script);
            Assert.Equal(0, check.ExitCode);

            var format = ScriptsProcess.Run("format", "--check", script);
            Assert.Equal(251, format.ExitCode);
            Assert.Contains("UTF-8 byte-order mark is forbidden", format.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_leaves_a_bom_only_file_unchanged()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = Path.Combine(directory, "bom-only.scs");
            byte[] bomOnly = [0xEF, 0xBB, 0xBF];
            File.WriteAllBytes(script, bomOnly);

            var result = ScriptsProcess.Run("check", "--fix", script);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Equal(bomOnly, File.ReadAllBytes(script));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_does_not_add_a_byte_order_mark()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory, "nobom.scs", "let total = 1\nio.write_standard_output_line(\"{totl}\")\n");

            var result = ScriptsProcess.Run("check", "--fix", script);

            Assert.Equal(0, result.ExitCode);
            var bytes = File.ReadAllBytes(script);
            Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
            Assert.Equal(
                "let total = 1\nio.write_standard_output_line(\"{total}\")\n",
                Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_refuses_a_clean_script_reached_through_a_final_symbolic_link()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            const string body = "io.write_standard_output_line(\"clean\")\n";
            var victim = ScriptsProcess.WriteScript(directory, "clean.scs", body);
            var unchangedTimestamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(victim, unchangedTimestamp);
            var link = Path.Combine(directory, "link.scs");
            File.CreateSymbolicLink(link, victim);

            var result = ScriptsProcess.Run("check", "--fix", link);

            Assert.Equal(252, result.ExitCode);
            Assert.True(
                result.StandardError.Contains("SCRIPTS-CLI-0014", StringComparison.Ordinal),
                result.StandardError);
            Assert.True(
                result.StandardError.Contains("the path is a symbolic link", StringComparison.Ordinal),
                result.StandardError);
            Assert.Equal(body, File.ReadAllText(victim));
            Assert.Equal(unchangedTimestamp, File.GetLastWriteTimeUtc(victim));
            Assert.Equal(victim, new FileInfo(link).LinkTarget);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_refuses_a_final_symbolic_link()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            const string body = "let total = 1\nio.write_standard_output_line(\"{totl}\")\n";
            var victim = ScriptsProcess.WriteScript(directory, "victim.scs", body);
            var unchangedTimestamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(victim, unchangedTimestamp);
            var link = Path.Combine(directory, "link.scs");
            File.CreateSymbolicLink(link, victim);

            var result = ScriptsProcess.Run("check", "--fix", link);

            Assert.Equal(252, result.ExitCode);
            Assert.Contains("SCRIPTS-CLI-0014", result.StandardError, StringComparison.Ordinal);
            Assert.Contains("the path is a symbolic link", result.StandardError, StringComparison.Ordinal);
            Assert.Equal(body, File.ReadAllText(victim));
            Assert.Equal(unchangedTimestamp, File.GetLastWriteTimeUtc(victim));
            Assert.Equal(victim, new FileInfo(link).LinkTarget);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_refuses_a_dangling_final_symbolic_link_without_creating_its_target()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var missing = Path.Combine(directory, "missing.scs");
            var link = Path.Combine(directory, "link.scs");
            File.CreateSymbolicLink(link, missing);

            var result = ScriptsProcess.Run("check", "--fix", link);

            Assert.Equal(252, result.ExitCode);
            Assert.True(
                result.StandardError.Contains("SCRIPTS-CLI-0014", StringComparison.Ordinal),
                result.StandardError);
            Assert.True(
                result.StandardError.Contains("the path is a symbolic link", StringComparison.Ordinal),
                result.StandardError);
            Assert.False(File.Exists(missing));
            Assert.Equal(missing, new FileInfo(link).LinkTarget);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_refuses_a_final_symbolic_link_to_a_directory()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var target = Path.Combine(directory, "target");
            Directory.CreateDirectory(target);
            var link = Path.Combine(directory, "link.scs");
            File.CreateSymbolicLink(link, target);

            var result = ScriptsProcess.Run("check", "--fix", link);

            Assert.Equal(252, result.ExitCode);
            Assert.Contains("SCRIPTS-CLI-0014", result.StandardError, StringComparison.Ordinal);
            Assert.Contains("the path is a symbolic link", result.StandardError, StringComparison.Ordinal);
            Assert.Equal(target, new FileInfo(link).LinkTarget);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_refuses_a_final_symbolic_link_with_a_trailing_slash()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            const string body = "io.write_standard_output_line(\"clean\")\n";
            var victim = ScriptsProcess.WriteScript(directory, "clean.scs", body);
            var unchangedTimestamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(victim, unchangedTimestamp);
            var link = Path.Combine(directory, "link.scs");
            File.CreateSymbolicLink(link, victim);

            var result = ScriptsProcess.Run("check", "--fix", link + "/");

            Assert.Equal(252, result.ExitCode);
            Assert.True(
                result.StandardError.Contains("SCRIPTS-CLI-0014", StringComparison.Ordinal),
                result.StandardError);
            Assert.True(
                result.StandardError.Contains("the path is a symbolic link", StringComparison.Ordinal),
                result.StandardError);
            Assert.Equal(body, File.ReadAllText(victim));
            Assert.Equal(unchangedTimestamp, File.GetLastWriteTimeUtc(victim));
            Assert.Equal(victim, new FileInfo(link).LinkTarget);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_refuses_a_final_symbolic_link_to_a_directory_with_a_trailing_slash()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var target = Path.Combine(directory, "target");
            Directory.CreateDirectory(target);
            var link = Path.Combine(directory, "link.scs");
            File.CreateSymbolicLink(link, target);

            var result = ScriptsProcess.Run("check", "--fix", link + "/");

            Assert.Equal(252, result.ExitCode);
            Assert.True(
                result.StandardError.Contains("SCRIPTS-CLI-0014", StringComparison.Ordinal),
                result.StandardError);
            Assert.True(
                result.StandardError.Contains("the path is a symbolic link", StringComparison.Ordinal),
                result.StandardError);
            Assert.Equal(target, new FileInfo(link).LinkTarget);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_a_regular_clean_script_exits_zero_without_writing()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            const string body = "io.write_standard_output_line(\"clean\")\n";
            var script = ScriptsProcess.WriteScript(directory, "clean.scs", body);
            var unchangedTimestamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(script, unchangedTimestamp);

            var result = ScriptsProcess.Run("check", "--fix", script);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Equal(string.Empty, result.StandardError);
            Assert.Equal(body, File.ReadAllText(script));
            Assert.Equal(unchangedTimestamp, File.GetLastWriteTimeUtc(script));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_keeps_the_loader_error_for_a_regular_script_with_a_trailing_slash()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            const string body = "io.write_standard_output_line(\"clean\")\n";
            var script = ScriptsProcess.WriteScript(directory, "clean.scs", body);
            var unchangedTimestamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(script, unchangedTimestamp);

            // The pre-check trims the slash to classify the path, but the loader still receives the raw path.
            var result = ScriptsProcess.Run("check", "--fix", script + "/");

            Assert.Equal(252, result.ExitCode);
            Assert.True(
                result.StandardError.Contains("SCRIPTS-CLI-0009", StringComparison.Ordinal),
                result.StandardError);
            Assert.Equal(body, File.ReadAllText(script));
            Assert.Equal(unchangedTimestamp, File.GetLastWriteTimeUtc(script));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_preserves_the_mode_of_a_regular_file()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "mode.scs", "let total = 1\nio.write_standard_output_line(\"{totl}\")\n");
            var mode = (UnixFileMode)Convert.ToInt32("640", 8);
            File.SetUnixFileMode(script, mode);

            var result = ScriptsProcess.Run("check", "--fix", script);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("let total = 1\nio.write_standard_output_line(\"{total}\")\n", File.ReadAllText(script));
            Assert.Equal(mode, File.GetUnixFileMode(script));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_migrates_the_v03_standard_library_renames()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "legacy.scs",
                "let xs = [1]\nlet first = xs.get(0).value_or_fail()\nlet info = uname()?\nio.write_standard_output_line(\"{first} {info.host_name}\")\n");
            var result = ScriptsProcess.Run("check", "--fix", script);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("applied 2 fix(es) to " + script + "\n", result.StandardOutput);
            Assert.Equal(
                "let xs = [1]\nlet first = xs.get_at_index(0).value_or_fail()\nlet info = system.get_information()?\nio.write_standard_output_line(\"{first} {info.host_name}\")\n",
                File.ReadAllText(script));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_reports_a_fix_in_an_imported_module_without_applying_it()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var module = ScriptsProcess.WriteScript(
                directory,
                "lib.scs",
                "public function f() -> integer {\n    let text = \"a\"\n    return text.lenth()\n}\n");
            var script = ScriptsProcess.WriteScript(directory, "main.scs", "import \"lib.scs\" as lib\nio.write_standard_output_line(\"{lib.f()}\")\n");
            var before = File.ReadAllText(module);

            var result = ScriptsProcess.Run("check", "--fix", script);

            Assert.Equal(251, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Contains("1 fix(es) in imported modules were not applied", result.StandardError, StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllText(module));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_leaves_a_script_without_a_safe_fix_untouched()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "no-fix.scs", "let n: integer = \"x\"\n");
            var before = File.ReadAllText(script);

            var result = ScriptsProcess.Run("check", "--fix", script);

            Assert.Equal(251, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Equal(before, File.ReadAllText(script));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_applies_several_fixes_on_one_line_in_reverse_order()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "multi.scs",
                "let text = \"ab\"\nlet other = \"cd\"\nio.write_standard_output_line(\"{text.lenth()} {other.lenth()}\")\n");

            var result = ScriptsProcess.Run("check", "--fix", script);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal($"applied 2 fix(es) to {script}\n", result.StandardOutput);
            Assert.Equal(
                "let text = \"ab\"\nlet other = \"cd\"\nio.write_standard_output_line(\"{text.length()} {other.length()}\")\n",
                File.ReadAllText(script));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_reports_a_fix_that_leaves_another_error()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "fix-then-error.scs",
                "let total = 1\nio.write_standard_output_line(\"{totl}\")\nlet bad: integer = \"x\"\n");

            var result = ScriptsProcess.Run("check", "--fix", script);

            Assert.Equal(251, result.ExitCode);
            Assert.Equal($"applied 1 fix(es) to {script}\n", result.StandardOutput);
            Assert.Contains("SCRIPTS-TYPE-0012", result.StandardError, StringComparison.Ordinal);
            Assert.Equal("let total = 1\nio.write_standard_output_line(\"{total}\")\nlet bad: integer = \"x\"\n", File.ReadAllText(script));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_with_errors_json_writes_only_the_document()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "json-fix.scs", "let total = 1\nio.write_standard_output_line(\"{totl}\")\n");

            var result = ScriptsProcess.Run("check", "--errors-json", "--fix", script);

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("applied", result.StandardOutput, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.True(document.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("let total = 1\nio.write_standard_output_line(\"{total}\")\n", File.ReadAllText(script));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_rejects_the_standard_input()
    {
        var result = ScriptsProcess.RunWithStandardInput("io.write_standard_output_line(\"x\")\n", "check", "--fix", "-");

        Assert.Equal(250, result.ExitCode);
        Assert.Contains("SCRIPTS-CLI-0012", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void An_assertion_in_a_lambda_is_positioned_in_the_lambda_s_file()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var moduleDirectory = Path.Combine(directory, "mod");
            Directory.CreateDirectory(moduleDirectory);
            ScriptsProcess.WriteScript(
                moduleDirectory,
                "runner.scs",
                "public function apply(callback: function(integer) -> unit, value: integer) -> unit {\n    callback(value)\n}\n");
            var script = ScriptsProcess.WriteScript(
                directory,
                "main.scs",
                "import \"mod/runner.scs\" as runner\n"
                + "let guard = (x: integer) => assert(x > 0, \"guard failed for {x}\")\n"
                + "runner.apply(guard, -1)\n");
            var result = ScriptsProcess.Run("run", script);

            Assert.Equal(253, result.ExitCode);
            Assert.Contains($"{script}:2:", result.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain("runner.scs:", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_deferred_assertion_is_positioned_in_its_own_file()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "defer.scs",
                "process.defer(() => assert(false, \"cleanup invariant\"))\nio.write_standard_output_line(\"one\")\n");
            var result = ScriptsProcess.Run("run", "--json-out", script);

            Assert.Equal(253, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var error = document.RootElement.GetProperty("errors")[0];
            Assert.Equal(script, error.GetProperty("file").GetString());
            Assert.Equal(1, error.GetProperty("line").GetInt32());
            Assert.Equal("assertion failed: cleanup invariant", error.GetProperty("message").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_non_utf8_inherited_variable_reaches_the_child_byte_for_byte()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "raw-environment.scs",
                """
                let r = process.run(process.request("/bin/sh", ["-c", "printf '%s' \"$SCRIPTS_A8_BAD\" | od -An -tx1 | tr -d ' \\n'"]))?
                io.write_standard_output_line("[{r.standard_output}]")
                """);

            // Only a native parent can place a non-UTF-8 value in scripts's initial environment.
            var startInfo = new ProcessStartInfo("/bin/sh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(
                $"SCRIPTS_A8_BAD=$(printf 'a\\377b') exec '{ScriptsProcess.Binary}' run '{script}'");
            using var process = Process.Start(startInfo)!;
            var standardOutput = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("[61ff62]\n", standardOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_non_utf8_inherited_name_cannot_be_removed_by_its_lenient_spelling()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "raw-name-remove.scs",
                """
                match environment.remove_variable("BAD\u{FFFD}NAME") {
                    ok(_) => io.write_standard_output_line("ok")
                    error(failure) => io.write_standard_output_line(failure.kind)
                }
                """);
            var startInfo = new ProcessStartInfo("/bin/sh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(
                $"exec /usr/bin/env \"$(printf 'BAD\\377NAME')\"=xvalue '{ScriptsProcess.Binary}' run '{script}'");
            using var process = Process.Start(startInfo)!;
            var standardOutput = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("invalid_argument\n", standardOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_non_utf8_inherited_name_reaches_the_child_as_raw_bytes()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "raw-name.scs",
                """
                match process.run(process.request("/usr/bin/env", [])) {
                    ok(result) => io.write_standard_output_line("ok")
                    error(failure) => io.write_standard_output_line(failure.kind)
                }
                """);
            var startInfo = new ProcessStartInfo("/bin/sh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(
                $"exec /usr/bin/env \"$(printf 'BAD\\377NAME')\"=xvalue '{ScriptsProcess.Binary}' run '{script}'");
            using var process = Process.Start(startInfo)!;
            var standardOutput = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("invalid_encoding\n", standardOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_non_utf8_inherited_name_stays_raw_and_refused_after_environment_all()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "raw-name-after-all.scs",
                """
                let all = environment.all_variables()
                io.write_standard_output_line("{all.length() > 0}")
                match environment.set_variable("BAD\u{FFFD}NAME", "y") {
                    ok(_) => io.write_standard_output_line("set=ok")
                    error(failure) => io.write_standard_output_line("set={failure.kind}")
                }
                match environment.remove_variable("BAD\u{FFFD}NAME") {
                    ok(_) => io.write_standard_output_line("remove=ok")
                    error(failure) => io.write_standard_output_line("remove={failure.kind}")
                }
                io.write_standard_output_line(environment.get_variable("BAD\u{FFFD}NAME").value_or("missing"))
                match process.run(process.request("/usr/bin/env", [])) {
                    ok(_) => io.write_standard_output_line("env=ok")
                    error(failure) => io.write_standard_output_line("env={failure.kind}")
                }
                """);
            var startInfo = new ProcessStartInfo("/bin/sh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(
                $"exec /usr/bin/env \"$(printf 'BAD\\377NAME')\"=xvalue '{ScriptsProcess.Binary}' run '{script}'");
            using var process = Process.Start(startInfo)!;
            var standardOutput = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("true\nset=invalid_argument\nremove=invalid_argument\nxvalue\nenv=invalid_encoding\n", standardOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_failed_stdout_write_reports_runtime_0019()
    {
        if (!File.Exists("/dev/full"))
        {
            return;
        }

        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exec \"$0\" describe --format json > /dev/full");
        startInfo.ArgumentList.Add(ScriptsProcess.Binary);
        using var process = Process.Start(startInfo)!;
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(253, process.ExitCode);
        Assert.Contains("SCRIPTS-RUNTIME-0019", standardError, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_fix_on_a_read_only_file_reports_a_typed_error()
    {
        if (Environment.UserName == "root")
        {
            return;
        }

        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "ro.scs", "let text = \"abc\"\nio.write_standard_output_line(\"{text.lenth()}\")\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead);
            try
            {
                var result = ScriptsProcess.Run("check", "--fix", script);

                Assert.Equal(252, result.ExitCode);
                Assert.Contains("SCRIPTS-CLI-0014", result.StandardError, StringComparison.Ordinal);
                Assert.DoesNotContain("internal error", result.StandardError, StringComparison.Ordinal);
                Assert.Equal("let text = \"abc\"\nio.write_standard_output_line(\"{text.lenth()}\")\n", File.ReadAllText(script));
            }
            finally
            {
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_fix_errors_json_rejects_stdin_with_250()
    {
        var result = ScriptsProcess.RunWithStandardInput("io.write_standard_output_line(\"x\")\n", "check", "--errors-json", "--fix", "-");

        Assert.Equal(250, result.ExitCode);
        Assert.Contains("\"code\":\"SCRIPTS-CLI-0012\"", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_errors_json_is_the_last_line_after_an_unterminated_child_stderr()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "s.scs",
                "let outcome = process.run_inherit(process.request(\"sh\", [\"-c\", \"printf nonl >&2\"]))\n"
                + "match outcome {\n    ok(code) => io.write_standard_output_line(\"code {code}\")\n    error(e) => io.write_standard_output_line(e.kind)\n}\n");
            var result = ScriptsProcess.Run("run", "--errors-json", script);

            var lastLine = result.StandardError.TrimEnd('\n').Split('\n').Last();
            using var document = JsonDocument.Parse(lastLine);
            Assert.True(document.RootElement.TryGetProperty("schema_version", out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_on_a_valid_script_exits_zero_and_is_silent()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "ok.scs", "let n = 1\n");
            var result = ScriptsProcess.Run("check", script);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Equal(string.Empty, result.StandardError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Missing_script_reports_cli_0009_with_code_252()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var missing = Path.Combine(directory, "absent.scs");
            var result = ScriptsProcess.Run("run", missing);

            Assert.Equal(252, result.ExitCode);
            Assert.Contains("SCRIPTS-CLI-0009", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_on_a_missing_script_also_reports_cli_0009_with_code_252()
    {
        // CLI-0009: exit code 252 (not 250) for run *and* check.
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var missing = Path.Combine(directory, "absent.scs");
            var result = ScriptsProcess.Run("check", missing);

            Assert.Equal(252, result.ExitCode);
            Assert.Contains("SCRIPTS-CLI-0009", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Unhandled_top_level_error_exits_with_code_253()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "err.scs", "let value = \"abc\".parse_integer()?\n");
            var result = ScriptsProcess.Run("run", script);

            Assert.Equal(253, result.ExitCode);
            Assert.Contains("SCRIPTS-RUNTIME-0017", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void User_code_can_raise_and_catch_its_own_error()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "own_error.scs",
                "function read_number(text: string) -> result<integer, error> {\n"
                + "    if text.starts_with(\"E\") {\n"
                + "        return error(\"invalid_format\", \"E prefix\", text)\n"
                + "    }\n"
                + "    return text.parse_integer()\n"
                + "}\n"
                + "match read_number(\"E42\") {\n"
                + "    ok(n) => io.write_standard_output_line(\"ok\")\n"
                + "    error(failure) => io.write_standard_output_line(failure.kind + \"|\" + failure.subject)\n"
                + "}\n");
            var result = ScriptsProcess.Run("run", script);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("invalid_format|E42\n", result.StandardOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void User_code_cannot_use_a_kind_outside_the_closed_set()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "bad_kind.scs", "let failure = error(\"not_fund\", \"/x\", \"absent\")\nio.write_standard_output_line(failure.kind)\n");
            var result = ScriptsProcess.Run("check", script);

            Assert.Equal(251, result.ExitCode);
            Assert.Contains("SCRIPTS-TYPE-0062", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Exit_script_returns_the_requested_code()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "exit.scs", "io.write_standard_output_line(\"before\")\nprocess.exit_with_code(7)\n");
            var result = ScriptsProcess.Run("run", script);

            Assert.Equal(7, result.ExitCode);
            Assert.Equal("before\n", result.StandardOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Exit_script_accepts_the_whole_0_to_255_range_and_rejects_256_with_code_253()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            foreach (var (code, expected) in new[] { (0, 0), (249, 249), (250, 250), (251, 251), (252, 252), (253, 253), (254, 254), (255, 255) })
            {
                var script = ScriptsProcess.WriteScript(directory, $"exit-{code}.scs", $"process.exit_with_code({code})\n");
                Assert.Equal(expected, ScriptsProcess.Run("run", script).ExitCode);
            }

            var rejected = ScriptsProcess.WriteScript(directory, "exit-256.scs", "process.exit_with_code(256)\n");
            var result = ScriptsProcess.Run("run", rejected);
            Assert.Equal(253, result.ExitCode);
            Assert.Contains("SCRIPTS-RUNTIME-0016", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Json_out_reports_exit_script_253_as_an_exit_not_a_failure()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "exit-253.scs", "process.exit_with_code(253)\n");
            var result = ScriptsProcess.Run("run", "--json-out", script);

            Assert.Equal(253, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal(253, document.RootElement.GetProperty("exit_code").GetInt32());
            Assert.Equal("exit_script", document.RootElement.GetProperty("termination").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The process status and the document's exit_code agree for the highest script code.</summary>
    [Fact]
    public void Json_out_reports_exit_script_255()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "exit-255.scs", "io.write_standard_output_line(\"before\")\nprocess.exit_with_code(255)\n");
            var result = ScriptsProcess.Run("run", "--json-out", script);

            Assert.Equal(255, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal(255, document.RootElement.GetProperty("exit_code").GetInt32());
            Assert.Equal("exit_script", document.RootElement.GetProperty("termination").GetString());
            Assert.Equal("before\n", document.RootElement.GetProperty("stdout").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// `assert(false, message)` stops the run with the fatal SCRIPTS-RUNTIME-0021 (code 253) and the
    /// script's own message on stderr; the statements after the call never run.
    /// </summary>
    [Fact]
    public void Assert_false_stops_with_code_253_and_the_message_on_standard_error()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "assert.scs",
                "io.write_standard_output_line(\"before\")\nassert(1 == 2, \"one is not two\")\nio.write_standard_output_line(\"never\")\n");
            var result = ScriptsProcess.Run("run", script);

            Assert.Equal(253, result.ExitCode);
            Assert.Equal("before\n", result.StandardOutput);
            Assert.Contains("error[SCRIPTS-RUNTIME-0021]: assertion failed: one is not two", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// DESIGN §6: `process.exit_with_code(n)` accepts 0..255; 256 and above are rejected, whatever the
    /// expression produces the number and whatever the command form. A script that refuses an input with
    /// `process.exit_with_code(256)` must never look like a success, so this is checked on every path: a literal, a variable,
    /// arithmetic, an argument parsed at run time, a `match` arm, a loop, a deferred action, and under
    /// `--json-out`/`--errors-json` where the process status must equal the document's `exit_code`.
    /// </summary>
    [Theory]
    [InlineData("process.exit_with_code(256)\n", "run")]
    [InlineData("process.exit_with_code(257)\n", "run")]
    [InlineData("process.exit_with_code(259)\n", "run")]
    [InlineData("process.exit_with_code(300)\n", "run")]
    [InlineData("let code = 256\nprocess.exit_with_code(code)\n", "run")]
    [InlineData("process.exit_with_code(255 + 1)\n", "run")]
    [InlineData("match 1 {\n  1 => process.exit_with_code(256),\n  _ => process.exit_with_code(0)\n}\n", "run")]
    [InlineData("let mutable i = 0\nwhile i < 3 {\n  i += 1\n  process.exit_with_code(256)\n}\n", "run")]
    [InlineData("process.defer(() => process.exit_with_code(256))\nio.write_standard_output_line(\"done\")\n", "run")]
    [InlineData("process.exit_with_code(256)\n", "run --json-out")]
    [InlineData("process.exit_with_code(256)\n", "run --errors-json")]
    public void Exit_script_above_255_is_a_runtime_failure_on_every_path(string source, string commandLine)
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "bounds.scs", source);
            var arguments = commandLine.Split(' ');
            var result = ScriptsProcess.Run([.. arguments, script]);

            Assert.Equal(253, result.ExitCode);
            Assert.Contains("SCRIPTS-RUNTIME-0016", result.StandardError + result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The argument comes from the script's own arguments: the bound holds for a value known only at run time.</summary>
    [Fact]
    public void Exit_script_rejects_a_code_parsed_from_a_script_argument()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "arg.scs",
                "let values = process.arguments()\nprocess.exit_with_code(values.get_at_index(0)?.parse_integer()?)\n");
            var result = ScriptsProcess.Run("run", script, "256");

            Assert.Equal(253, result.ExitCode);
            Assert.Contains("SCRIPTS-RUNTIME-0016", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Json_out_reports_standard_output_result_and_termination()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "ok.scs", "io.write_standard_output_line(\"hello\")\nreturn 42\n");
            var result = ScriptsProcess.Run("run", "--json-out", script);

            Assert.Equal(0, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal(0, document.RootElement.GetProperty("exit_code").GetInt32());
            Assert.Empty(document.RootElement.GetProperty("errors").EnumerateArray());
            Assert.Equal("normal", document.RootElement.GetProperty("termination").GetString());
            Assert.Equal("hello\n", document.RootElement.GetProperty("stdout").GetString());
            Assert.Equal(42, document.RootElement.GetProperty("result").GetInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Return_of_a_function_container_is_a_static_error_under_json_out()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "function-container.scs", "let f = () => 1\nreturn [f]\n");

            var check = ScriptsProcess.Run("check", script);
            Assert.Equal(251, check.ExitCode);
            Assert.Contains("SCRIPTS-TYPE-0054", check.StandardError, StringComparison.Ordinal);

            var run = ScriptsProcess.Run("run", "--json-out", script);
            Assert.Equal(251, run.ExitCode);
            Assert.DoesNotContain("internal error", run.StandardError, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(run.StandardOutput);
            Assert.Equal(251, document.RootElement.GetProperty("exit_code").GetInt32());
            Assert.Equal("static_error", document.RootElement.GetProperty("termination").GetString());
            Assert.Equal("SCRIPTS-TYPE-0054", document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("return [1, 2]\n", "[1,2]")]
    [InlineData("return {\"a\": 1}\n", "{\"a\":1}")]
    [InlineData("let value: result<integer, error> = ok(1)\nreturn value\n", "{\"ok\":1}")]
    public void Return_of_a_serializable_value_still_renders_under_json_out(string source, string expectedResult)
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "serializable-return.scs", source);
            var result = ScriptsProcess.Run("run", "--json-out", script);

            Assert.Equal(0, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal(expectedResult, document.RootElement.GetProperty("result").GetRawText());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Json_out_on_a_static_error_reports_static_error_termination()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "bad.scs", "let n = \"abc\".size()\n");
            var result = ScriptsProcess.Run("run", "--json-out", script);

            Assert.Equal(251, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal("static_error", document.RootElement.GetProperty("termination").GetString());
            Assert.False(document.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("SCRIPTS-TYPE-0008", document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Errors_json_on_check_writes_one_line_on_standard_output()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "bad.scs", "let n = \"abc\".size()\n");
            var result = ScriptsProcess.Run("check", "--errors-json", script);

            Assert.Equal(251, result.ExitCode);
            var line = result.StandardOutput.TrimEnd('\n');
            Assert.DoesNotContain('\n', line);
            using var document = JsonDocument.Parse(line);
            Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_deny_warnings_accepts_a_read_prefixed_binding_without_a_warning()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            // A read `_value` is accepted by SCRIPTS-TYPE-0070 and must not emit the retired SCRIPTS-LINT-0008 warning.
            var script = ScriptsProcess.WriteScript(
                directory,
                "read-prefixed.scs",
                "let _value = filesystem.read_file(p\"/tmp/scripts-absent-0070\")\nio.write_standard_output_line(_value.value_or(\"\"))");

            var result = ScriptsProcess.Run("check", "--deny-warnings", script);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_deny_warnings_fails_on_a_warning_and_keeps_it_in_warnings()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            // `process.request("bash", …)` triggers SCRIPTS-LINT-0001; without the flag the check succeeds.
            var script = ScriptsProcess.WriteScript(directory, "warn.scs", "let outcome = process.run(process.request(\"bash\", [\"-c\", \"echo\"]))?\nio.write_standard_output_line(outcome.standard_output)");

            Assert.Equal(0, ScriptsProcess.Run("check", script).ExitCode);

            var denied = ScriptsProcess.Run("check", "--deny-warnings", script);
            Assert.Equal(251, denied.ExitCode);
            Assert.Contains("warning[SCRIPTS-LINT-0001]", denied.StandardError, StringComparison.Ordinal);

            var json = ScriptsProcess.Run("check", "--errors-json", "--deny-warnings", script);
            Assert.Equal(251, json.ExitCode);
            using var document = JsonDocument.Parse(json.StandardOutput);
            Assert.False(document.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("SCRIPTS-LINT-0001", document.RootElement.GetProperty("warnings")[0].GetProperty("code").GetString());
            Assert.Empty(document.RootElement.GetProperty("errors").EnumerateArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Check_deny_warnings_fails_on_the_quadratic_concatenation_warning()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            // The shape is what the lint keys on, not the item count: two items are enough to prove the diagnostic.
            var script = ScriptsProcess.WriteScript(
                directory,
                "quadratic.scs",
                "let mutable s = \"\"\nfor item in [\"a\", \"b\"] {\n    s = s + item\n}\nio.write_standard_output_line(s)\n");

            Assert.Equal(0, ScriptsProcess.Run("check", script).ExitCode);

            var denied = ScriptsProcess.Run("check", "--deny-warnings", script);
            Assert.Equal(251, denied.ExitCode);
            Assert.Contains("warning[SCRIPTS-LINT-0012]", denied.StandardError, StringComparison.Ordinal);

            var json = ScriptsProcess.Run("check", "--errors-json", "--deny-warnings", script);
            Assert.Equal(251, json.ExitCode);
            using var document = JsonDocument.Parse(json.StandardOutput);
            Assert.Equal("SCRIPTS-LINT-0012", document.RootElement.GetProperty("warnings")[0].GetProperty("code").GetString());
            Assert.Empty(document.RootElement.GetProperty("errors").EnumerateArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Errors_json_on_check_covers_a_load_error_too()
    {
        // A parse error fails before checking; the JSON document must still be written on stdout (DESIGN §8.3).
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "bad.scs", "let x =");
            var result = ScriptsProcess.Run("check", "--errors-json", script);

            Assert.Equal(251, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal("SCRIPTS-PARSE-0023", document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Script_arguments_are_available_to_the_script()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "args.scs",
                "for argument in process.arguments() {\n    io.write_standard_output_line(argument)\n}\n");
            var result = ScriptsProcess.Run("run", script, "one", "-two", "--three");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("one\n-two\n--three\n", result.StandardOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_end_of_options_separator_protects_a_scripts_option_name()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "args.scs",
                "for argument in process.arguments() {\n    io.write_standard_output_line(argument)\n}\n");
            var result = ScriptsProcess.Run("run", script, "--", "--json-out");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("--\n--json-out\n", result.StandardOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Explain_json_out_emits_one_document_with_positions()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "explain-json.scs", "let n = 1\nio.write_standard_output_line(\"{n}\")\n");
            var result = ScriptsProcess.Run("explain", "--json-out", script);

            Assert.Equal(0, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal("explain", document.RootElement.GetProperty("command").GetString());
            Assert.Equal(script, document.RootElement.GetProperty("script").GetString());
            var steps = document.RootElement.GetProperty("steps");
            Assert.Equal(2, steps.GetArrayLength());
            Assert.Equal("let", steps[0].GetProperty("kind").GetString());
            Assert.Equal(1, steps[0].GetProperty("line").GetInt32());
            Assert.Equal(1, steps[0].GetProperty("column").GetInt32());
            Assert.Equal("expression", steps[1].GetProperty("kind").GetString());
            Assert.Equal(2, steps[1].GetProperty("line").GetInt32());
            Assert.Empty(document.RootElement.GetProperty("errors").EnumerateArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Explain_json_out_reports_a_static_error_with_251()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "explain-bad.scs", "let n: integer = \"x\"\n");
            var result = ScriptsProcess.Run("explain", "--json-out", script);

            Assert.Equal(251, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.False(document.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("SCRIPTS-TYPE-0012", document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Explain_describes_a_script_without_running_it()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "expl.scs", "let n = 3\nio.write_standard_output_line(\"value {n}\")\n");
            var result = ScriptsProcess.Run("explain", script);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("declares the constant `n` = 3", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("displays", result.StandardOutput, StringComparison.Ordinal);
            // explain never executes: no script output.
            Assert.DoesNotContain("value 3", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Explain_reports_static_errors_like_check()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "bad.scs", "let n = \"abc\".size()\n");
            var result = ScriptsProcess.Run("explain", script);

            Assert.Equal(251, result.ExitCode);
            Assert.Contains("SCRIPTS-TYPE-0008", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Explain_is_deterministic_across_runs()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "det.scs",
                "function square(x: integer) -> integer {\n    return x * x\n}\nlet result = square(5)\nio.write_standard_output_line(\"{result}\")\n");
            var first = ScriptsProcess.Run("explain", script);
            var second = ScriptsProcess.Run("explain", script);

            Assert.Equal(0, first.ExitCode);
            Assert.Equal(first.StandardOutput, second.StandardOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_failed_standard_output_write_is_not_silent()
    {
        // DESIGN §0.4: no silent failure. Writing to a full /dev/full must report SCRIPTS-RUNTIME-0019 and exit 253,
        // not succeed silently (red-team finding R2-1). Skipped when /dev/full is unavailable.
        if (!File.Exists("/dev/full"))
        {
            return;
        }

        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "p.scs", "io.write_standard_output_line(\"hi\")\n");
            var result = ScriptsProcess.RunWithStandardOutputRedirect(script, "/dev/full");

            Assert.Equal(253, result.ExitCode);
            Assert.Contains("SCRIPTS-RUNTIME-0019", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Explain_describes_a_call_into_an_imported_module()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            ScriptsProcess.WriteScript(
                directory,
                "lib.scs",
                "public function greet(name: string) -> string {\n    return \"hello {name}\"\n}\n");
            var script = ScriptsProcess.WriteScript(
                directory,
                "main.scs",
                "import \"lib.scs\" as lib\nio.write_standard_output_line(lib.greet(\"Ada\"))\n");
            var result = ScriptsProcess.Run("explain", script);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("lib.greet", result.StandardOutput, StringComparison.Ordinal);
            // explain never crashes: no stack trace, no code 134.
            Assert.DoesNotContain("Exception", result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("Exception", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Standard_io_reads_standard_input()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "stdin.scs",
                "match io.read_standard_input() {\n    ok(text) => io.write_standard_output_line(text.trim_whitespace()),\n    error(e) => io.write_standard_output_line(\"error {e.kind}\")\n}\n");
            var result = ScriptsProcess.RunWithStandardInput("abc\n", "run", script);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("abc\n", result.StandardOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Standard_io_reports_invalid_encoding_on_standard_input()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "stdin.scs",
                "match io.read_standard_input() {\n    ok(text) => io.write_standard_output_line(\"ok\"),\n    error(e) => io.write_standard_output_line(e.kind)\n}\n");
            // Invalid UTF-8 cannot go through ScriptsProcess.RunWithStandardInput (a string), so use a shell redirection.
            var inputPath = Path.Combine(directory, "bytes.bin");
            File.WriteAllBytes(inputPath, [0xFF, 0xFE]);
            var result = RunShell($"exec \"$0\" run \"$1\" < \"$2\"", script, inputPath);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("invalid_encoding\n", result.StandardOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Standard_io_read_line_reads_lines_and_shares_the_stream_with_read_standard_input()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "readline.scs",
                "let first = io.read_standard_input_line()?\n" +
                "let second = io.read_standard_input_line()?\n" +
                "let rest = io.read_standard_input()?\n" +
                "io.write_standard_output_line(\"[{first}][{second}][{rest.trim_whitespace()}]\")\n");
            var result = ScriptsProcess.RunWithStandardInput("one\r\ntwo\nthree\n", "run", script);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("[one][two][three]\n", result.StandardOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Standard_io_write_adds_no_newline_and_orders_with_io_write_standard_output_line()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "write.scs",
                "io.write_standard_output(\"a\")\nio.write_standard_output_line(\"b\")\nio.write_standard_output(\"c\")\n");
            var result = ScriptsProcess.Run("run", script);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("ab\nc\n", result.StandardOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("io.write_standard_output(\"x\")\n")]
    [InlineData("io.write_standard_output_line(\"x\")\n")]
    public void Buffered_stdout_write_on_a_closed_pipe_exits_141_silently(string body)
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "closed-stdout.scs", body);

            var result = ScriptsProcess.RunWithClosedStandardOutput(script);

            Assert.Equal(141, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Deferred_action_epipe_exits_141_silently_and_keeps_running_the_lifo_chain()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var marker = Path.Combine(directory, "ran-marker");
            var script = ScriptsProcess.WriteScript(
                directory,
                "deferred-epipe.scs",
                $$"""
                function flush_stdout() -> unit {
                    io.write_standard_output("deferred")
                    match io.read_standard_input_line() {
                        ok(_) => io.write_standard_output("")
                        error(_) => io.write_standard_output("")
                    }
                }
                function write_marker() -> unit {
                    match filesystem.write_file_overwrite("ran", p"{{marker}}") {
                        ok(_) => io.write_standard_output("")
                        error(_) => io.write_standard_output("")
                    }
                }
                process.defer(write_marker)
                process.defer(flush_stdout)
                """);

            var result = ScriptsProcess.RunWithClosedStandardOutput(script);

            Assert.Equal(141, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardError);
            Assert.True(File.Exists(marker), "the other deferred action did not run after the EPIPE");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("io.write_standard_output(\"x\")\nprocess.exit_with_code(0)\n", 141)]
    [InlineData("io.write_standard_output(\"x\")\nprocess.exit_with_code(5)\n", 5)]
    public void Exit_script_with_a_closed_stdout_maps_zero_to_141_but_keeps_a_nonzero_code(string body, int expected)
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "exit-closed-stdout.scs", body);

            var result = ScriptsProcess.RunWithClosedStandardOutput(script);

            Assert.Equal(expected, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Process_script_path_is_the_canonical_entry_path_for_absolute_relative_and_symlink_invocations()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "script.scs",
                "io.write_standard_output_line(process.script_path().to_string())\n");
            var canonical = Path.GetFullPath(script);

            var absolute = ScriptsProcess.Run("run", script);
            Assert.Equal(0, absolute.ExitCode);
            Assert.Equal(canonical + "\n", absolute.StandardOutput);

            var relative = RunShell("cd \"$1\" && exec \"$0\" run ./script.scs", directory);
            Assert.Equal(0, relative.ExitCode);
            Assert.Equal(canonical + "\n", relative.StandardOutput);

            var link = Path.Combine(directory, "link.scs");
            File.CreateSymbolicLink(link, script);
            var throughLink = ScriptsProcess.Run("run", link);
            Assert.Equal(0, throughLink.ExitCode);
            Assert.Equal(canonical + "\n", throughLink.StandardOutput);

            var properties = ScriptsProcess.WriteScript(
                directory,
                "properties.scs",
                "let p = process.script_path()\nio.write_standard_output_line(\"{p.is_absolute()} {p.base_name()}\")\n");
            var propertyRun = ScriptsProcess.Run("run", properties);
            Assert.Equal(0, propertyRun.ExitCode);
            Assert.Equal("true properties.scs\n", propertyRun.StandardOutput);

            // check only parses/analyses: the entry script is never executed (no path is printed).
            var check = ScriptsProcess.Run("check", script);
            Assert.Equal(0, check.ExitCode);
            Assert.Equal(string.Empty, check.StandardOutput);
            Assert.Equal(string.Empty, check.StandardError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Standard_process_defer_cleans_up_on_every_termination_path()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            const string body =
                "" +
                "function cleanup(workdir: path) -> unit {\n" +
                "    io.write_standard_output_line(\"cleanup\")\n" +
                "    match filesystem.remove_directory_tree(workdir) {\n" +
                "        ok(_) => io.write_standard_output_line(\"nettoye\"),\n" +
                "        error(e) => io.write_standard_output_line(\"echec {e.kind}\")\n" +
                "    }\n" +
                "}\n" +
                "let result = filesystem.make_temporary_directory(\"scripts-e2e-defer\")\n" +
                "match result {\n" +
                "    ok(w) => {\n" +
                "        io.write_standard_output_line(\"temps={w}\")\n" +
                "        process.defer(() => cleanup(w))\n" +
                "    },\n" +
                "    error(e) => io.write_standard_output_line(\"echec creation\")\n" +
                "}\n";
            var end = ScriptsProcess.WriteScript(directory, "end.scs", body);
            var returned = ScriptsProcess.WriteScript(directory, "return.scs", body + "return 0\n");
            var exited = ScriptsProcess.WriteScript(directory, "exit.scs", body + "process.exit_with_code(3)\n");
            var fatal = ScriptsProcess.WriteScript(
                directory,
                "fatal.scs",
                body + "let mutable zero = 0\nio.write_standard_output_line(\"{1 / zero}\")\n");

            var endRun = ScriptsProcess.Run("run", end);
            Assert.Equal(0, endRun.ExitCode);
            Assert.Contains("cleanup", endRun.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("nettoye", endRun.StandardOutput, StringComparison.Ordinal);

            var returnRun = ScriptsProcess.Run("run", returned);
            Assert.Equal(0, returnRun.ExitCode);
            Assert.Contains("nettoye", returnRun.StandardOutput, StringComparison.Ordinal);

            var exitRun = ScriptsProcess.Run("run", exited);
            Assert.Equal(3, exitRun.ExitCode);
            Assert.Contains("nettoye", exitRun.StandardOutput, StringComparison.Ordinal);

            var fatalRun = ScriptsProcess.Run("run", fatal);
            Assert.Equal(253, fatalRun.ExitCode);
            Assert.Contains("nettoye", fatalRun.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("SCRIPTS-RUNTIME-0002", fatalRun.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void An_unknown_standard_module_is_bcs_module_0005()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "bad.scs", "import \"std:yaml\" as x\n");
            var result = ScriptsProcess.Run("run", script);

            Assert.Equal(251, result.ExitCode);
            Assert.Contains("SCRIPTS-MODULE-0005", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ScriptsProcessResult RunShell(params string[] commandAndArguments)
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(commandAndArguments[0]);
        startInfo.ArgumentList.Add(Binary);
        foreach (var argument in commandAndArguments[1..])
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ScriptsProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static string Binary => ScriptsProcess.Binary;
}
