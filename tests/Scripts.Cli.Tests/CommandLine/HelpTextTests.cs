using System.Globalization;
using Scripts.Core;

namespace Scripts.Cli.Tests.CommandLine;

public sealed class HelpTextTests
{
    private static readonly string[] Lines = HelpText.Full.Split('\n');

    [Fact]
    public void Version_line_is_the_language_name_and_the_generated_version()
    {
        Assert.Equal($"scripts {LanguageReference.Version}", HelpText.VersionLine);
    }

    [Fact]
    public void Help_starts_with_the_version_and_ends_with_a_single_newline_and_uses_lf_only()
    {
        Assert.StartsWith($"scripts {LanguageReference.Version} ", HelpText.Full, StringComparison.Ordinal);
        Assert.EndsWith(".\n", HelpText.Full, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', HelpText.Full);
        Assert.False(HelpText.Full.EndsWith("\n\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_line_fits_in_one_hundred_columns()
    {
        foreach (var line in Lines)
        {
            Assert.True(line.EnumerateRunes().Count() <= 100, $"line too long ({line.EnumerateRunes().Count()} code points): {line}");
        }
    }

    [Fact]
    public void Help_is_deterministic()
    {
        Assert.Equal(HelpText.Full, HelpText.Full);
        Assert.Equal(HelpText.Full, string.Join('\n', Lines));
    }

    [Theory]
    [InlineData("scripts run [--errors-json] [--json-out] [--no-warnings] [--] <script.scs>|- [argument…]")]
    [InlineData("scripts check [--errors-json] [--deny-warnings] [--fix] [--module] <script.scs>|-")]
    [InlineData("scripts explain [--json-out] <script.scs>")]
    [InlineData("scripts format [--check] <script.scs>")]
    [InlineData("scripts describe --format json [--name NAME]")]
    [InlineData("scripts --version")]
    [InlineData("scripts --help")]
    [InlineData("scripts help")]
    public void Usage_lists_every_form_of_the_command_line(string usageLine)
    {
        Assert.Contains("\n  " + usageLine + "\n", HelpText.Full, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("  run ")]
    [InlineData("  check ")]
    [InlineData("  explain ")]
    [InlineData("  describe ")]
    [InlineData("  help ")]
    [InlineData("  --errors-json ")]
    [InlineData("  --json-out ")]
    [InlineData("  --no-warnings ")]
    [InlineData("  --deny-warnings ")]
    [InlineData("  --module ")]
    [InlineData("  -- ")]
    [InlineData("  --check ")]
    [InlineData("  --format json ")]
    [InlineData("  --version ")]
    [InlineData("  --help ")]
    public void Commands_and_options_are_documented_in_their_sections(string documentedItem)
    {
        Assert.Contains("\n" + documentedItem, HelpText.Full, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_exit_code_of_the_language_is_listed()
    {
        var section = HelpText.Full[HelpText.Full.IndexOf("EXIT CODES", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("EXAMPLES", StringComparison.Ordinal)];

        foreach (var exitCode in LanguageReference.ExitCodeTable)
        {
            var label = exitCode.Code == exitCode.LastCode
                ? exitCode.Code.ToString(CultureInfo.InvariantCulture)
                : $"{exitCode.Code}-{exitCode.LastCode}";
            Assert.Contains($"\n  {label,-6}  {exitCode.Meaning}\n", section, StringComparison.Ordinal);
        }

        foreach (var code in new[] { 0, 250, 251, 252, 253, 129, 130, 141, 143 })
        {
            Assert.Contains($"\n  {code,-6}  ", section, StringComparison.Ordinal);
        }

        Assert.Contains("\n  1-255 ", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_has_examples_and_documentation_pointers_and_the_option_placement_rule()
    {
        Assert.Contains("\nEXAMPLES\n", HelpText.Full, StringComparison.Ordinal);
        Assert.Contains("  scripts run --json-out script.scs ", HelpText.Full, StringComparison.Ordinal);
        Assert.Contains("\nREFERENCE\n", HelpText.Full, StringComparison.Ordinal);
        Assert.Contains("scripts describe --format json provides the machine-readable reference.", HelpText.Full, StringComparison.Ordinal);

        Assert.Contains("Options precede the script.", HelpText.Full, StringComparison.Ordinal);
        Assert.Contains("process.arguments()", HelpText.Full, StringComparison.Ordinal);
        Assert.Contains("#!/usr/bin/scripts run", HelpText.Full, StringComparison.Ordinal);
    }

    [Fact]
    public void Fix_refuses_a_final_symbolic_link_and_the_record_option_is_gone()
    {
        Assert.Contains(
            "file (never to the standard input), then checks it again. A final\n"
            + "                  symbolic link is refused.\n",
            HelpText.Full,
            StringComparison.Ordinal);
        Assert.DoesNotContain("--record", HelpText.Full, StringComparison.Ordinal);
        Assert.DoesNotContain("scripts repl", HelpText.Full, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_documents_the_script_read_from_the_standard_input()
    {
        Assert.Contains("\n  -               (run, check, format --check) Reads the script from the standard input", HelpText.Full, StringComparison.Ordinal);
        Assert.Contains("\n  scripts run - < script.scs ", HelpText.Full, StringComparison.Ordinal);
        Assert.Contains("\n  scripts format script.scs ", HelpText.Full, StringComparison.Ordinal);
    }
}
