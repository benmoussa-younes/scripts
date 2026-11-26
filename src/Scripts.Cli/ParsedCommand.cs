using Scripts.Core;

namespace Scripts.Cli;

/// <summary>A successfully parsed command line (DESIGN 9).</summary>
public abstract record ParsedCommand;

/// <summary>
/// <c>scripts run [--errors-json] [--json-out] [--no-warnings] [--] script|- [arguments…]</c>; <c>ScriptArguments</c> is
/// everything after the script, verbatim. A bare <c>-</c> (or <c>--</c> followed by <c>-</c>) reads the script from the
/// standard input (DESIGN §9): <c>StandardInput</c> is then true and <c>ScriptPath</c> stays <c>"-"</c> only as the token
/// that was read. <c>NoWarnings</c> hides the human warning block only: errors always print, and the JSON documents keep
/// the warnings.
/// </summary>
public sealed record RunCommand(
    bool ErrorsJson,
    bool JsonOut,
    string ScriptPath,
    bool StandardInput,
    IReadOnlyList<string> ScriptArguments,
    bool NoWarnings = false) : ParsedCommand;

/// <summary><c>scripts check [--errors-json] [--deny-warnings] [--fix] script|-</c>; a bare <c>-</c> reads the script from the standard input, which <c>--fix</c> cannot rewrite.</summary>
public sealed record CheckCommand(bool ErrorsJson, bool DenyWarnings, string ScriptPath, bool StandardInput, bool Fix = false, bool Module = false) : ParsedCommand;

/// <summary><c>scripts explain [--json-out] script</c>; <c>--json-out</c> asks for the machine document (DESIGN §9).</summary>
public sealed record ExplainCommand(string ScriptPath, bool JsonOut = false) : ParsedCommand;

public sealed record DescribeCommand(string? Name = null) : ParsedCommand;

/// <summary><c>scripts format [--check] script|-</c> (P9c): rewrite the canonical layout of one source file, or with <c>--check</c> report the violations without writing; the standard input can only be checked.</summary>
public sealed record FormatCommand(string ScriptPath, bool StandardInput, bool Check) : ParsedCommand;

public sealed record VersionCommand : ParsedCommand;

public sealed record HelpCommand : ParsedCommand;

/// <summary>Outcome of <see cref="CommandLineParser.Parse"/>: either a command or one SCRIPTS-CLI diagnostic (exit code 250).</summary>
public abstract record CommandLineParseResult;

public sealed record CommandLineAccepted(ParsedCommand Command) : CommandLineParseResult;

public sealed record CommandLineRejected(Diagnostic Diagnostic) : CommandLineParseResult;
