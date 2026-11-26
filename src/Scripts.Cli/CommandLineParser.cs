using Scripts.Core;

namespace Scripts.Cli;

/// <summary>
/// Command-line parser of DESIGN 9. Reports the first violation in reading order; absences
/// (missing script, missing <c>--format json</c>) are reported after all arguments were read.
/// </summary>
public static class CommandLineParser
{
    private const string ErrorsJsonOption = "--errors-json";
    private const string JsonOutOption = "--json-out";
    private const string NoWarningsOption = "--no-warnings";
    private const string ModuleOption = "--module";
    private const string FixOption = "--fix";
    private const string NameOption = "--name";
    private const string DenyWarningsOption = "--deny-warnings";
    private const string FormatOption = "--format";
    private const string CheckOption = "--check";
    private const string EndOfOptions = "--";
    private const string StandardInputPath = "-";
    private const string MissingValueText = "<missing>";

    private static readonly string[] RunOptions = [ErrorsJsonOption, JsonOutOption, NoWarningsOption];
    private static readonly string[] CheckOptions = [ErrorsJsonOption, DenyWarningsOption, FixOption, ModuleOption];
    private static readonly string[] ExplainOptions = [JsonOutOption];

    public static CommandLineParseResult Parse(IReadOnlyList<string> commandLineArguments)
    {
        if (commandLineArguments.Count == 0)
        {
            return Reject(ErrorCodes.Cli0001);
        }

        var commandName = commandLineArguments[0];
        return commandName switch
        {
            "run" => ParseScriptCommand(commandLineArguments, "run", RunOptions),
            "check" => ParseScriptCommand(commandLineArguments, "check", CheckOptions),
            "explain" => ParseScriptCommand(commandLineArguments, "explain", ExplainOptions),
            "describe" => ParseDescribe(commandLineArguments),
            "format" => ParseFormat(commandLineArguments),
            "--version" => ParseStandalone(commandLineArguments, new VersionCommand()),
            "--help" or "help" => ParseStandalone(commandLineArguments, new HelpCommand()),
            _ => Reject(ErrorCodes.Cli0002, ("command", commandName)),
        };
    }

    private static CommandLineParseResult ParseScriptCommand(
        IReadOnlyList<string> commandLineArguments,
        string commandName,
        string[] acceptedOptions)
    {
        var seenOptions = new HashSet<string>(StringComparer.Ordinal);
        string? scriptPath = null;
        var nextPosition = 1;
        while (nextPosition < commandLineArguments.Count && scriptPath is null)
        {
            var token = commandLineArguments[nextPosition++];
            if (token == EndOfOptions)
            {
                if (commandName != "run")
                {
                    return Reject(ErrorCodes.Cli0007, ("argument", token));
                }

                if (nextPosition < commandLineArguments.Count)
                {
                    scriptPath = commandLineArguments[nextPosition++];
                }
            }
            else if (token == StandardInputPath && AcceptsStandardInput(commandName))
            {
                // DESIGN §9: `-` is the standard input for run and check only (explain keeps SCRIPTS-CLI-0003).
                scriptPath = token;
            }
            else if (token.StartsWith('-'))
            {
                if (!acceptedOptions.Contains(token))
                {
                    return RejectUnacceptedOption(token, commandName, acceptedOptions);
                }

                if (!seenOptions.Add(token))
                {
                    return Reject(ErrorCodes.Cli0008, ("option", token));
                }
            }
            else
            {
                scriptPath = token;
            }
        }

        if (seenOptions.Contains(ErrorsJsonOption) && seenOptions.Contains(JsonOutOption))
        {
            return Reject(ErrorCodes.Cli0011);
        }

        if (scriptPath is null)
        {
            return Reject(ErrorCodes.Cli0005, ("command", commandName));
        }

        var trailingArguments = new List<string>();
        for (var trailingPosition = nextPosition; trailingPosition < commandLineArguments.Count; trailingPosition++)
        {
            trailingArguments.Add(commandLineArguments[trailingPosition]);
        }

        var standardInput = scriptPath == StandardInputPath;

        if (commandName == "run")
        {
            return ParseRunTrailingArguments(seenOptions, scriptPath, standardInput, trailingArguments);
        }

        if (trailingArguments.Count > 0)
        {
            var firstTrailing = trailingArguments[0];
            if (firstTrailing == EndOfOptions || !firstTrailing.StartsWith('-'))
            {
                return Reject(ErrorCodes.Cli0007, ("argument", firstTrailing));
            }

            return acceptedOptions.Contains(firstTrailing)
                ? RejectOptionAfterScript(firstTrailing, commandName, scriptPath)
                : RejectUnacceptedOption(firstTrailing, commandName, acceptedOptions);
        }

        return commandName == "check"
            ? Accept(new CheckCommand(
                seenOptions.Contains(ErrorsJsonOption), seenOptions.Contains(DenyWarningsOption), scriptPath, standardInput,
                seenOptions.Contains(FixOption), seenOptions.Contains(ModuleOption)))
            : Accept(new ExplainCommand(scriptPath, seenOptions.Contains(JsonOutOption)));
    }

    /// <summary>DESIGN §9: only <c>run</c> and <c>check</c> read the script from the standard input.</summary>
    private static bool AcceptsStandardInput(string commandName) => commandName is "run" or "check";

    // Script arguments are verbatim, except that a scripts option written before any `--` is a misplaced option, not a script argument.
    private static CommandLineParseResult ParseRunTrailingArguments(
        HashSet<string> seenOptions,
        string scriptPath,
        bool standardInput,
        List<string> trailingArguments)
    {
        foreach (var trailingArgument in trailingArguments)
        {
            if (trailingArgument == EndOfOptions)
            {
                break;
            }

            if (RunOptions.Contains(trailingArgument))
            {
                return RejectOptionAfterScript(trailingArgument, "run", scriptPath);
            }
        }

        return Accept(new RunCommand(
            seenOptions.Contains(ErrorsJsonOption),
            seenOptions.Contains(JsonOutOption),
            scriptPath,
            standardInput,
            trailingArguments,
            seenOptions.Contains(NoWarningsOption)));
    }

    /// <summary>
    /// <c>scripts format --check script|-</c> (P9c): the layout check takes one script or the standard input and the
    /// mandatory <c>--check</c>; it never rewrites the file.
    /// </summary>
    private static CommandLineParseResult ParseFormat(IReadOnlyList<string> commandLineArguments)
    {
        var checkSeen = false;
        string? scriptPath = null;
        for (var position = 1; position < commandLineArguments.Count; position++)
        {
            var token = commandLineArguments[position];
            if (token == CheckOption)
            {
                if (checkSeen)
                {
                    return Reject(ErrorCodes.Cli0008, ("option", token));
                }

                checkSeen = true;
            }
            else if (token == StandardInputPath && scriptPath is null)
            {
                scriptPath = token;
            }
            else if (token.StartsWith('-'))
            {
                return RejectUnacceptedOption(token, "format", [CheckOption]);
            }
            else if (scriptPath is null)
            {
                scriptPath = token;
            }
            else
            {
                return Reject(ErrorCodes.Cli0007, ("argument", token));
            }
        }

        if (!checkSeen)
        {
            return Reject(ErrorCodes.Cli0015);
        }

        return scriptPath is null
            ? Reject(ErrorCodes.Cli0005, ("command", "format"))
            : Accept(new FormatCommand(scriptPath, scriptPath == StandardInputPath));
    }

    /// <summary>
    /// <c>scripts describe --format json [--name NAME]</c>: a machine document, one format, at most one name.
    /// </summary>
    private static CommandLineParseResult ParseDescribe(IReadOnlyList<string> commandLineArguments)
    {
        var formatSeen = false;
        var nameSeen = false;
        string? formatValue = null;
        string? nameValue = null;
        var position = 1;
        while (position < commandLineArguments.Count)
        {
            var token = commandLineArguments[position++];
            if (token == FormatOption)
            {
                if (formatSeen)
                {
                    return Reject(ErrorCodes.Cli0008, ("option", token));
                }

                formatSeen = true;
                if (position < commandLineArguments.Count)
                {
                    formatValue = commandLineArguments[position++];
                }
            }
            else if (token == NameOption)
            {
                if (nameSeen)
                {
                    return Reject(ErrorCodes.Cli0008, ("option", token));
                }

                nameSeen = true;
                // A value option without a value is a usage error, not "the whole catalogue" (audit M5);
                // an option-like token is not swallowed as a query either.
                if (position == commandLineArguments.Count
                    || commandLineArguments[position].StartsWith('-'))
                {
                    return Reject(ErrorCodes.Cli0013, ("option", token));
                }

                nameValue = commandLineArguments[position++];
            }
            else if (token != EndOfOptions && token.StartsWith('-'))
            {
                return RejectUnacceptedOption(token, "describe", [FormatOption, NameOption]);
            }
            else
            {
                return Reject(ErrorCodes.Cli0007, ("argument", token));
            }
        }

        return formatValue == "json"
            ? Accept(new DescribeCommand(nameValue))
            : Reject(ErrorCodes.Cli0006, ("value", formatValue ?? MissingValueText));
    }

    private static CommandLineParseResult ParseStandalone(IReadOnlyList<string> commandLineArguments, ParsedCommand command) =>
        commandLineArguments.Count > 1
            ? Reject(ErrorCodes.Cli0007, ("argument", commandLineArguments[1]))
            : Accept(command);

    private static CommandLineRejected RejectUnacceptedOption(string option, string commandName, string[] acceptedOptions) =>
        Reject(
            ErrorCodes.Cli0003,
            ("option", option),
            ("command", commandName),
            ("accepted", acceptedOptions.Length == 0 ? "none" : string.Join(", ", acceptedOptions)));

    private static CommandLineRejected RejectOptionAfterScript(string option, string commandName, string scriptPath) =>
        Reject(ErrorCodes.Cli0004, ("option", option), ("command", commandName), ("script", scriptPath));

    private static CommandLineAccepted Accept(ParsedCommand command) => new(command);

    private static CommandLineRejected Reject(string code, params (string Name, string Value)[] placeholders) =>
        new(Diagnostics.Create(code, null, default, placeholders));
}
