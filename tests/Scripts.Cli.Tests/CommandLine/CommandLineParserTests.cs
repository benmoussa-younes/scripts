using Scripts.Core;

namespace Scripts.Cli.Tests.CommandLine;

public sealed class CommandLineParserTests
{
    private static ParsedCommand Accepted(params string[] commandLineArguments)
    {
        var result = CommandLineParser.Parse(commandLineArguments);
        var accepted = Assert.IsType<CommandLineAccepted>(result);
        return accepted.Command;
    }

    private static Diagnostic Rejected(params string[] commandLineArguments)
    {
        var result = CommandLineParser.Parse(commandLineArguments);
        var rejected = Assert.IsType<CommandLineRejected>(result);
        Assert.Null(rejected.Diagnostic.File);
        Assert.Equal(Severity.Error, rejected.Diagnostic.Severity);
        return rejected.Diagnostic;
    }

    [Fact]
    public void No_arguments_is_cli_0001()
    {
        var diagnostic = Rejected();

        Assert.Equal("SCRIPTS-CLI-0001", diagnostic.Code);
        Assert.Equal("no command provided", diagnostic.Message);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("--foo")]
    [InlineData("-x")]
    [InlineData("script.scs")]
    [InlineData("RUN")]
    [InlineData("")]
    public void Unknown_first_argument_is_cli_0002_with_the_command_placeholder(string firstArgument)
    {
        var diagnostic = Rejected(firstArgument, "script.scs");

        Assert.Equal("SCRIPTS-CLI-0002", diagnostic.Code);
        Assert.Equal($"unknown command: `{firstArgument}`", diagnostic.Message);
        Assert.Equal("available commands: run, check, explain, fmt, describe, help", diagnostic.Suggestion);
    }

    [Fact]
    public void Describe_name_without_a_value_is_a_usage_error()
    {
        Assert.Equal("SCRIPTS-CLI-0013", Rejected("describe", "--format", "json", "--name").Code);
        Assert.Equal("SCRIPTS-CLI-0013", Rejected("describe", "--format", "json", "--name", "--help").Code);
    }

    [Fact]
    public void Repl_is_no_longer_a_command()
    {
        Assert.Equal("SCRIPTS-CLI-0002", Rejected("repl").Code);
    }

    [Fact]
    public void Unknown_option_for_run_is_cli_0003_with_command_and_accepted_options()
    {
        var diagnostic = Rejected("run", "--verbose", "script.scs");

        Assert.Equal("SCRIPTS-CLI-0003", diagnostic.Code);
        Assert.Equal("option `--verbose` unknown or not accepted by `scripts run`", diagnostic.Message);
        Assert.Equal(
            "options accepted by `scripts run`: --errors-json, --json-out; for a script starting with `-`, write `./-x.scs`",
            diagnostic.Suggestion);
    }

    [Theory]
    [InlineData("check", "--json-out", "--errors-json, --deny-warnings, --fix")]
    [InlineData("explain", "--errors-json", "--json-out")]
    [InlineData("run", "-x.scs", "--errors-json, --json-out")]
    [InlineData("check", "-x", "--errors-json, --deny-warnings, --fix")]
    [InlineData("run", "--deny-warnings", "--errors-json, --json-out")]
    [InlineData("explain", "--deny-warnings", "--json-out")]
    [InlineData("describe", "--verbose", "--format, --name")]
    [InlineData("describe", "--json-out", "--format, --name")]
    [InlineData("describe", "--format=json", "--format, --name")]
    public void Option_not_accepted_by_the_command_is_cli_0003(string command, string option, string expectedAccepted)
    {
        var diagnostic = Rejected(command, option, "script.scs");

        Assert.Equal("SCRIPTS-CLI-0003", diagnostic.Code);
        Assert.Equal($"option `{option}` unknown or not accepted by `scripts {command}`", diagnostic.Message);
        Assert.Contains($"options accepted by `scripts {command}`: {expectedAccepted};", diagnostic.Suggestion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("run", "--errors-json")]
    [InlineData("run", "--json-out")]
    public void Scripts_option_after_the_script_of_run_is_cli_0004(string command, string option)
    {
        var diagnostic = Rejected(command, "script.scs", option);

        Assert.Equal("SCRIPTS-CLI-0004", diagnostic.Code);
        Assert.Equal($"option `{option}` placed after the script: options precede the script", diagnostic.Message);
        Assert.Equal($"write `scripts run {option} script.scs`", diagnostic.Suggestion);
    }

    [Fact]
    public void Scripts_option_among_process_arguments_before_a_double_dash_is_cli_0004()
    {
        var diagnostic = Rejected("run", "--json-out", "script.scs", "a", "b", "--errors-json");

        Assert.Equal("SCRIPTS-CLI-0004", diagnostic.Code);
        Assert.Equal("option `--errors-json` placed after the script: options precede the script", diagnostic.Message);
    }

    [Fact]
    public void Accepted_option_after_the_script_of_check_is_cli_0004()
    {
        var diagnostic = Rejected("check", "a.scs", "--errors-json");

        Assert.Equal("SCRIPTS-CLI-0004", diagnostic.Code);
        Assert.Equal("write `scripts check --errors-json a.scs`", diagnostic.Suggestion);
    }

    [Fact]
    public void Missing_script_is_cli_0005_for_run_check_and_explain()
    {
        foreach (var command in new[] { "run", "check", "explain" })
        {
            var diagnostic = Rejected(command);
            Assert.Equal("SCRIPTS-CLI-0005", diagnostic.Code);
            Assert.Equal($"missing script for `scripts {command}`", diagnostic.Message);
            Assert.Equal($"`scripts {command} <script.scs>`", diagnostic.Suggestion);
        }

        Assert.Equal("SCRIPTS-CLI-0005", Rejected("run", "--json-out").Code);
        Assert.Equal("SCRIPTS-CLI-0005", Rejected("run", "--errors-json", "--").Code);
        Assert.Equal("SCRIPTS-CLI-0005", Rejected("run", "--json-out", "--").Code);
        Assert.Equal("SCRIPTS-CLI-0005", Rejected("run", "--").Code);
        Assert.Equal("SCRIPTS-CLI-0005", Rejected("check", "--errors-json").Code);
    }

    [Fact]
    public void Describe_without_format_is_cli_0006_with_the_missing_value_text()
    {
        string[][] argumentSets = [["describe"], ["describe", "--format"]];
        foreach (var arguments in argumentSets)
        {
            var diagnostic = Rejected(arguments);
            Assert.Equal("SCRIPTS-CLI-0006", diagnostic.Code);
            Assert.Equal("`scripts describe` requires `--format json` (received value: `<missing>`)", diagnostic.Message);
            Assert.Equal("`scripts describe --format json`", diagnostic.Suggestion);
        }
    }

    [Theory]
    [InlineData("yaml")]
    [InlineData("JSON")]
    [InlineData("")]
    [InlineData("--format")]
    public void Describe_with_another_format_value_is_cli_0006_with_that_value(string value)
    {
        var diagnostic = Rejected("describe", "--format", value);

        Assert.Equal("SCRIPTS-CLI-0006", diagnostic.Code);
        Assert.Equal($"`scripts describe` requires `--format json` (received value: `{value}`)", diagnostic.Message);
    }

    [Theory]
    [InlineData("check", "a.scs", "b.scs")]
    [InlineData("explain", "a.scs", "b.scs")]
    [InlineData("check", "-", "extra")]
    public void Second_script_for_check_and_explain_is_cli_0007(string command, string firstScript, string extra)
    {
        var diagnostic = Rejected(command, firstScript, extra);

        Assert.Equal("SCRIPTS-CLI-0007", diagnostic.Code);
        Assert.Equal($"unexpected extra argument: `{extra}`", diagnostic.Message);
        Assert.Contains($"remove `{extra}`", diagnostic.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Double_dash_is_an_extra_argument_outside_run()
    {
        const string expectedMessage = "unexpected extra argument: `--`";
        Assert.Equal(expectedMessage, Rejected("check", "--", "a.scs").Message);
        Assert.Equal(expectedMessage, Rejected("check", "a.scs", "--").Message);
        Assert.Equal(expectedMessage, Rejected("explain", "--", "a.scs").Message);
        Assert.Equal(expectedMessage, Rejected("describe", "--").Message);
        Assert.Equal("SCRIPTS-CLI-0007", Rejected("check", "--", "a.scs").Code);
    }

    [Theory]
    [InlineData("--version", "x")]
    [InlineData("--help", "x")]
    [InlineData("help", "run")]
    [InlineData("describe", "json")]
    public void Standalone_commands_and_describe_reject_extra_arguments_with_cli_0007(string command, string extra)
    {
        var diagnostic = Rejected(command, extra);

        Assert.Equal("SCRIPTS-CLI-0007", diagnostic.Code);
        Assert.Equal($"unexpected extra argument: `{extra}`", diagnostic.Message);
    }

    [Fact]
    public void Describe_with_a_valid_format_and_an_extra_argument_is_cli_0007()
    {
        var diagnostic = Rejected("describe", "--format", "json", "extra");

        Assert.Equal("SCRIPTS-CLI-0007", diagnostic.Code);
        Assert.Contains("`extra`", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Explain_after_the_script_with_a_bcs_option_it_does_not_accept_is_cli_0003()
    {
        var diagnostic = Rejected("explain", "a.scs", "--errors-json");

        Assert.Equal("SCRIPTS-CLI-0003", diagnostic.Code);
        Assert.Equal("option `--errors-json` unknown or not accepted by `scripts explain`", diagnostic.Message);
    }

    public static TheoryData<string[]> RepeatedOptionCases => new()
    {
        new[] { "run", "--json-out", "--json-out", "script.scs" },
        new[] { "run", "--errors-json", "--json-out", "--errors-json", "script.scs" },
        new[] { "check", "--errors-json", "--errors-json", "script.scs" },
        new[] { "describe", "--format", "json", "--format", "json" },
        new[] { "describe", "--format", "yaml", "--format", "json" },
    };

    [Theory]
    [MemberData(nameof(RepeatedOptionCases))]
    public void Repeated_option_is_cli_0008_with_the_option_placeholder(string[] commandLineArguments)
    {
        var diagnostic = Rejected(commandLineArguments);
        var repeated = commandLineArguments.Skip(1).GroupBy(token => token).First(group => group.Count() > 1 && group.Key.StartsWith('-')).Key;

        Assert.Equal("SCRIPTS-CLI-0008", diagnostic.Code);
        Assert.Equal($"repeated option: `{repeated}`", diagnostic.Message);
    }

    [Fact]
    public void First_violation_in_reading_order_wins()
    {
        Assert.Equal("SCRIPTS-CLI-0002", Rejected("frobnicate", "--bogus").Code);
        Assert.Equal("SCRIPTS-CLI-0003", Rejected("run", "--bogus", "--bogus").Code);
        Assert.Equal("SCRIPTS-CLI-0008", Rejected("run", "--json-out", "--json-out", "--bogus").Code);
        Assert.Equal("SCRIPTS-CLI-0003", Rejected("run", "--bogus", "--json-out", "--json-out").Code);
        Assert.Equal("SCRIPTS-CLI-0003", Rejected("check", "--json-out", "--errors-json", "--errors-json").Code);
        Assert.Equal("SCRIPTS-CLI-0003", Rejected("describe", "--format", "yaml", "--bogus").Code);
        Assert.Equal("SCRIPTS-CLI-0007", Rejected("describe", "extra").Code);
        Assert.Equal("SCRIPTS-CLI-0007", Rejected("check", "a.scs", "b.scs", "--errors-json").Code);
    }

    [Theory]
    [InlineData(false, false, "script.scs", new[] { "run", "script.scs" })]
    [InlineData(true, false, "script.scs", new[] { "run", "--errors-json", "script.scs" })]
    [InlineData(false, true, "script.scs", new[] { "run", "--json-out", "script.scs" })]
    [InlineData(false, false, "script.scs", new[] { "run", "--", "script.scs" })]
    [InlineData(false, false, "-x.scs", new[] { "run", "--", "-x.scs" })]
    [InlineData(false, false, "--", new[] { "run", "--", "--" })]
    [InlineData(false, false, "", new[] { "run", "" })]
    public void Valid_run_forms_are_accepted(bool errorsJson, bool jsonOut, string expectedScript, string[] commandLineArguments)
    {
        var command = Assert.IsType<RunCommand>(Accepted(commandLineArguments));

        Assert.Equal(errorsJson, command.ErrorsJson);
        Assert.Equal(jsonOut, command.JsonOut);
        Assert.Equal(expectedScript, command.ScriptPath);
        Assert.False(command.StandardInput);
        Assert.Empty(command.ScriptArguments);
    }

    public static TheoryData<string[]> BothOutputFlagsCases => new()
    {
        new[] { "run", "--errors-json", "--json-out", "script.scs" },
        new[] { "run", "--json-out", "--errors-json", "script.scs" },
        new[] { "run", "--errors-json", "--json-out", "--", "script.scs" },
        new[] { "run", "--errors-json", "--json-out" },
        new[] { "run", "--errors-json", "--json-out", "-" },
    };

    [Theory]
    [MemberData(nameof(BothOutputFlagsCases))]
    public void Both_output_flags_on_run_are_cli_0011(string[] commandLineArguments)
    {
        var diagnostic = Rejected(commandLineArguments);

        Assert.Equal("SCRIPTS-CLI-0011", diagnostic.Code);
        Assert.Equal("`--errors-json` and `--json-out` cannot be combined", diagnostic.Message);
        Assert.Equal("keep one of the two flags", diagnostic.Suggestion);
    }

    [Fact]
    public void A_bare_dash_is_the_standard_input_for_run()
    {
        var command = Assert.IsType<RunCommand>(Accepted("run", "-"));

        Assert.True(command.StandardInput);
        Assert.Equal("-", command.ScriptPath);
        Assert.False(command.ErrorsJson);
        Assert.False(command.JsonOut);
        Assert.Empty(command.ScriptArguments);
    }

    [Fact]
    public void A_bare_dash_is_the_standard_input_after_the_options_and_after_the_double_dash()
    {
        Assert.True(Assert.IsType<RunCommand>(Accepted("run", "--errors-json", "-")).ErrorsJson);
        Assert.True(Assert.IsType<RunCommand>(Accepted("run", "--json-out", "-")).JsonOut);
        Assert.True(Assert.IsType<RunCommand>(Accepted("run", "--", "-")).StandardInput);
        Assert.True(Assert.IsType<RunCommand>(Accepted("run", "--json-out", "--", "-")).JsonOut);
        Assert.True(Assert.IsType<RunCommand>(Accepted("run", "--", "-")).StandardInput);
    }

    [Fact]
    public void Script_arguments_after_a_standard_input_script_are_verbatim()
    {
        var command = Assert.IsType<RunCommand>(Accepted("run", "-", "a", "b"));

        Assert.True(command.StandardInput);
        Assert.Equal(["a", "b"], command.ScriptArguments);

        Assert.Equal(["-"], Assert.IsType<RunCommand>(Accepted("run", "-", "-")).ScriptArguments);
        Assert.Equal(["--", "--json-out"], Assert.IsType<RunCommand>(Accepted("run", "-", "--", "--json-out")).ScriptArguments);
    }

    [Fact]
    public void A_dash_after_the_options_double_dash_is_the_standard_input_and_the_rest_are_arguments()
    {
        // `--` ends the options for run, so a `-` there is the standard input too, never the file named `-`.
        var command = Assert.IsType<RunCommand>(Accepted("run", "--", "-", "-x.scs"));

        Assert.True(command.StandardInput);
        Assert.Equal(["-x.scs"], command.ScriptArguments);
    }

    [Fact]
    public void A_dash_for_explain_stays_cli_0003()
    {
        var diagnostic = Rejected("explain", "-");

        Assert.Equal("SCRIPTS-CLI-0003", diagnostic.Code);
        Assert.Equal("option `-` unknown or not accepted by `scripts explain`", diagnostic.Message);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("check")]
    public void A_script_name_starting_with_a_dash_and_not_alone_is_still_cli_0003(string command)
    {
        var diagnostic = Rejected(command, "-x.scs");

        Assert.Equal("SCRIPTS-CLI-0003", diagnostic.Code);
        Assert.Equal($"option `-x.scs` unknown or not accepted by `scripts {command}`", diagnostic.Message);
    }

    [Fact]
    public void Script_arguments_are_passed_verbatim_including_options_double_dash_empty_and_unicode()
    {
        string[] scriptArguments = ["-x", "--", "", "🙂 ☃ 😀", "--json-outx", "-", "--verbose", "--errors-jsonx", "a b", "run"];

        var command = Assert.IsType<RunCommand>(Accepted(["run", "--json-out", "script.scs", .. scriptArguments]));

        Assert.Equal("script.scs", command.ScriptPath);
        Assert.True(command.JsonOut);
        Assert.Equal(scriptArguments, command.ScriptArguments);
    }

    [Fact]
    public void Script_named_like_an_option_is_reachable_after_the_double_dash_and_its_arguments_are_verbatim()
    {
        var command = Assert.IsType<RunCommand>(Accepted("run", "--", "-x.scs", "--", "--json-out"));

        Assert.Equal("-x.scs", command.ScriptPath);
        Assert.Equal(["--", "--json-out"], command.ScriptArguments);
    }

    [Fact]
    public void Double_dash_after_the_script_is_a_script_argument_and_protects_scripts_option_names()
    {
        var command = Assert.IsType<RunCommand>(Accepted("run", "script.scs", "--", "--json-out"));

        Assert.Equal(["--", "--json-out"], command.ScriptArguments);
        Assert.Equal(["--"], Assert.IsType<RunCommand>(Accepted("run", "script.scs", "--")).ScriptArguments);
    }

    [Fact]
    public void Valid_check_explain_describe_version_and_help_forms_are_accepted()
    {
        Assert.Equal(new CheckCommand(false, false, "s.scs", false), Accepted("check", "s.scs"));
        Assert.Equal(new CheckCommand(true, false, "s.scs", false), Accepted("check", "--errors-json", "s.scs"));
        Assert.Equal(new CheckCommand(false, true, "s.scs", false), Accepted("check", "--deny-warnings", "s.scs"));
        Assert.Equal(new CheckCommand(true, true, "s.scs", false), Accepted("check", "--deny-warnings", "--errors-json", "s.scs"));
        Assert.Equal(new CheckCommand(false, false, "s.scs", false, true), Accepted("check", "--fix", "s.scs"));
        Assert.Equal(new CheckCommand(false, false, "-", true, true), Accepted("check", "--fix", "-"));
        Assert.Equal(new ExplainCommand("s.scs", false), Accepted("explain", "s.scs"));
        Assert.Equal(new ExplainCommand("s.scs", true), Accepted("explain", "--json-out", "s.scs"));
        Assert.Equal(new DescribeCommand(), Accepted("describe", "--format", "json"));
        Assert.Equal(new DescribeCommand("process"), Accepted("describe", "--format", "json", "--name", "process"));
        Assert.Equal("SCRIPTS-CLI-0008", Rejected("describe", "--format", "json", "--name", "a", "--name", "b").Code);
        Assert.Equal(new VersionCommand(), Accepted("--version"));
        Assert.Equal(new HelpCommand(), Accepted("--help"));
        Assert.Equal(new HelpCommand(), Accepted("help"));
    }

    [Fact]
    public void Fmt_requires_check_and_accepts_one_script_or_the_standard_input()
    {
        Assert.Equal(new FmtCommand("s.scs", false), Accepted("fmt", "--check", "s.scs"));
        Assert.Equal(new FmtCommand("-", true), Accepted("fmt", "--check", "-"));
        Assert.Equal(new FmtCommand("s.scs", false), Accepted("fmt", "s.scs", "--check"));
        Assert.Equal("SCRIPTS-CLI-0015", Rejected("fmt", "s.scs").Code);
        Assert.Equal("SCRIPTS-CLI-0005", Rejected("fmt", "--check").Code);
        Assert.Equal("SCRIPTS-CLI-0008", Rejected("fmt", "--check", "--check", "s.scs").Code);
        Assert.Equal("SCRIPTS-CLI-0003", Rejected("fmt", "--json-out", "--check", "s.scs").Code);
        Assert.Equal("SCRIPTS-CLI-0007", Rejected("fmt", "--check", "a", "b").Code);
    }

    [Fact]
    public void A_bare_dash_is_the_standard_input_for_check()
    {
        Assert.Equal(new CheckCommand(false, false, "-", true), Accepted("check", "-"));
        Assert.Equal(new CheckCommand(true, false, "-", true), Accepted("check", "--errors-json", "-"));
        Assert.Equal(new CheckCommand(false, true, "-", true), Accepted("check", "--deny-warnings", "-"));
    }
}
