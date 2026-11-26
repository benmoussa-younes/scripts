using System.Text;

namespace Scripts.EndToEnd.Tests;

/// <summary><c>scripts format --check</c> (P9c): canonical layout only, no rewriting, one line per violation.</summary>
public sealed class FormatEndToEndTests
{
    [Fact]
    public void The_fmt_alias_is_removed()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "canonical.scs",
                "io.write_standard_output_line(\"x\")\n");

            var result = ScriptsProcess.Run("fmt", "--check", script);

            Assert.Equal(250, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Contains("unknown command: `fmt`", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_canonical_script_exits_zero_with_no_output()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "canonical.scs",
                "function twice(n: integer) -> integer {\n    return n * 2\n}\n\nio.write_standard_output_line(\"{twice(2)}\")\n");

            var result = ScriptsProcess.Run("format", "--check", script);

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
    public void A_layout_violation_names_the_path_line_and_column_and_exits_251()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(directory, "bad.scs", "function f() -> unit {\n   io.write_standard_output_line(\"x\")\n}\n");

            var result = ScriptsProcess.Run("format", "--check", script);

            Assert.Equal(251, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Equal(
                $"{script}:2:1: not canonical: expected 4 spaces of indentation, found 3\n",
                result.StandardError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_tab_between_tokens_is_refused_but_check_accepts_it()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "tab.scs",
                "let x =\t1\nio.write_standard_output_line(\"{x}\")\n");

            var fmt = ScriptsProcess.Run("format", "--check", script);
            var check = ScriptsProcess.Run("check", script);

            Assert.Equal(251, fmt.ExitCode);
            Assert.Contains("1:8: not canonical: tab: use spaces", fmt.StandardError, StringComparison.Ordinal);
            Assert.Equal(0, check.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_raw_string_keeps_its_trailing_spaces_and_crlf()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "raw.scs",
                "let text = r\"\"\"\r\nline with spaces  \r\n\"\"\"\nio.write_standard_output_line(\"{text.length()}\")\n");

            var result = ScriptsProcess.Run("format", "--check", script);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_multi_line_raw_string_nested_in_an_interpolation_keeps_its_content()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var script = ScriptsProcess.WriteScript(
                directory,
                "nested-raw.scs",
                "function identity(value: string) -> string {\n    return value\n}\n\nlet text = \"{identity(r\"\"\"  \r\nline with spaces  \r\n\t\"\"\")}\"\nio.write_standard_output_line(\"{text.length()}\")\n");

            var fmt = ScriptsProcess.Run("format", "--check", script);
            var check = ScriptsProcess.Run("check", script);

            Assert.Equal(0, fmt.ExitCode);
            Assert.Equal(string.Empty, fmt.StandardError);
            Assert.Equal(0, check.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Standard_input_is_checked_under_the_stdin_name()
    {
        var result = ScriptsProcess.RunWithStandardInput("function f() -> unit {\n io.write_standard_output_line(\"x\")\n}\n", "format", "--check", "-");

        Assert.Equal(251, result.ExitCode);
        Assert.Equal("<stdin>:2:1: not canonical: expected 4 spaces of indentation, found 1\n", result.StandardError);
    }

    [Fact]
    public void A_byte_order_mark_and_crlf_are_refused()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "bom.scs");
            File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, (byte)'l', (byte)'e', (byte)'t', (byte)' ', (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'1', (byte)'\r', (byte)'\n']);

            var result = ScriptsProcess.Run("format", "--check", path);

            Assert.Equal(251, result.ExitCode);
            Assert.Contains("1:1: not canonical: UTF-8 byte-order mark is forbidden", result.StandardError, StringComparison.Ordinal);
            Assert.Contains("1:10: not canonical: CRLF line ending: use LF", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_missing_script_or_a_directory_is_252()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var missing = ScriptsProcess.Run("format", "--check", Path.Combine(directory, "absent.scs"));
            var asDirectory = ScriptsProcess.Run("format", "--check", directory);

            Assert.Equal(252, missing.ExitCode);
            Assert.Contains("SCRIPTS-CLI-0009", missing.StandardError, StringComparison.Ordinal);
            Assert.Equal(252, asDirectory.ExitCode);
            Assert.Contains("SCRIPTS-CLI-0009", asDirectory.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_layout_violation_is_rewritten_to_the_canonical_layout()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "messy.scs");
            File.WriteAllBytes(
                path,
                [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("function f() -> unit {\r\n   io.write_standard_output_line(\"x\")  \r\n}\r\nf()\r\n\r\n")]);

            var format = ScriptsProcess.Run("format", path);

            Assert.Equal(0, format.ExitCode);
            Assert.Equal(string.Empty, format.StandardOutput);
            Assert.Equal(string.Empty, format.StandardError);
            var expected = "function f() -> unit {\n    io.write_standard_output_line(\"x\")\n}\nf()\n";
            Assert.Equal(expected, File.ReadAllText(path));
            Assert.Equal(0, ScriptsProcess.Run("format", "--check", path).ExitCode);
            var run = ScriptsProcess.Run("run", path);
            Assert.Equal(0, run.ExitCode);
            Assert.Equal("x\n", run.StandardOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_second_rewrite_leaves_the_bytes_unchanged()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var path = ScriptsProcess.WriteScript(directory, "bad.scs", "function f() -> unit {\n   let x = 1  \n}\n");
            Assert.Equal(0, ScriptsProcess.Run("format", path).ExitCode);
            var once = File.ReadAllBytes(path);

            Assert.Equal(0, ScriptsProcess.Run("format", path).ExitCode);
            Assert.Equal(once, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_rewrite_keeps_raw_string_content()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "raw.scs");
            File.WriteAllText(
                path,
                "let text = r\"\"\"\r\nline with spaces  \r\n\"\"\"\r\n   io.write_standard_output_line(\"{text.length()}\")\r\n",
                new UTF8Encoding(false));

            Assert.Equal(0, ScriptsProcess.Run("format", path).ExitCode);
            var rewritten = File.ReadAllText(path);
            Assert.Contains("r\"\"\"\r\nline with spaces  \r\n\"\"\"\n", rewritten, StringComparison.Ordinal);
            Assert.Equal(0, ScriptsProcess.Run("format", "--check", path).ExitCode);
            Assert.Equal(0, ScriptsProcess.Run("check", path).ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_standard_input_cannot_be_rewritten()
    {
        var result = ScriptsProcess.RunWithStandardInput("let x = 1\n", "format", "-");

        Assert.Equal(250, result.ExitCode);
        Assert.Contains("SCRIPTS-CLI-0016", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void A_final_symbolic_link_is_refused_and_the_target_is_unchanged()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var target = ScriptsProcess.WriteScript(directory, "target.scs", "let x = 1\n");
            var link = Path.Combine(directory, "link.scs");
            File.CreateSymbolicLink(link, target);

            var result = ScriptsProcess.Run("format", link);

            Assert.Equal(252, result.ExitCode);
            Assert.Contains("SCRIPTS-CLI-0014", result.StandardError, StringComparison.Ordinal);
            Assert.Equal("let x = 1\n", File.ReadAllText(target));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_source_that_cannot_be_tokenized_is_not_rewritten()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var path = ScriptsProcess.WriteScript(directory, "unterminated.scs", "let text = \"unterminated\n");

            var result = ScriptsProcess.Run("format", path);

            Assert.Equal(251, result.ExitCode);
            Assert.Contains("not canonical: the source cannot be tokenized", result.StandardError, StringComparison.Ordinal);
            Assert.Equal("let text = \"unterminated\n", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_missing_script_is_252_for_a_rewrite_too()
    {
        var directory = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var result = ScriptsProcess.Run("format", Path.Combine(directory, "absent.scs"));

            Assert.Equal(252, result.ExitCode);
            Assert.Contains("SCRIPTS-CLI-0009", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
