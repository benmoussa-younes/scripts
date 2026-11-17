using Scripts.Core;

namespace Scripts.Cli;

/// <summary>Texts printed by <c>--version</c>, <c>--help</c>, <c>help</c> and <c>scripts</c> without arguments.</summary>
public static class HelpText
{
    public static string VersionLine => $"{LanguageReference.LanguageName} {LanguageReference.Version}";

    /// <summary>Complete help, every line at most 100 columns, terminated by a newline.</summary>
    public static string Full { get; } = BuildFullText();

    private static string BuildFullText()
    {
        var lines = new List<string>();
        lines.Add($"{VersionLine} ({LanguageReference.ProductName})");
        lines.Add("Statically typed system scripting language for Linux, interpreted by one native binary.");
        lines.Add(string.Empty);
        lines.Add("USAGE");
        lines.Add("  scripts run [--errors-json] [--json-out] [--] <script.scs>|- [argument…]");
        lines.Add("  scripts check [--errors-json] [--deny-warnings] [--fix] <script.scs>|-");
        lines.Add("  scripts explain [--json-out] <script.scs>");
        lines.Add("  scripts fmt --check <script.scs>|-");
        lines.Add("  scripts describe --format json [--name NAME]");
        lines.Add("  scripts --version");
        lines.Add("  scripts --help");
        lines.Add("  scripts help");
        lines.Add(string.Empty);
        lines.Add("COMMANDS");
        lines.Add("  run       Checks then runs the script; nothing runs if a static error exists (code");
        lines.Add("            251). Warnings are printed even on success.");
        lines.Add("  check     Analyzes the script without running it: syntax, types, calls, imports,");
        lines.Add("            visibility, mutability, returns, exhaustiveness and warnings. Code 0 if");
        lines.Add("            valid, 251 otherwise.");
        lines.Add("  fmt       Checks the canonical layout without rewriting a token: LF, final newline,");
        lines.Add("            no BOM/tab/trailing space, four spaces per open brace. 0 if canonical, 251");
        lines.Add("            otherwise.");
        lines.Add("  explain   Checks the script (same diagnostics as check), then describes it in");
        lines.Add("            English, one sentence per statement; flags destructive operations and");
        lines.Add("            process.run_shell. --json-out writes the same steps as one JSON document.");
        lines.Add("  describe  Writes the complete language description as JSON to stdout: keywords,");
        lines.Add("            types, functions, error codes, exit codes, limits, grammar.");
        lines.Add("  help      Prints this help (like --help).");
        lines.Add(string.Empty);
        lines.Add("OPTIONS");
        lines.Add("  --errors-json   (run, check) Diagnostics as JSON on one line: check writes them to");
        lines.Add("                  stdout, run to stderr (last line, only if there is a diagnostic).");
        lines.Add("  --json-out      (run, explain) No human output: a single JSON document on stdout.");
        lines.Add("                  run:   schema_version, exit_code, success, termination, stdout,");
        lines.Add("                  stderr, result, errors, warnings.");
        lines.Add("                  explain: schema_version, command, script, steps (depth, kind,");
        lines.Add("                  text, destructive, dangerous, line, column), errors, warnings.");
        lines.Add("  --deny-warnings (check) Fail with code 251 as soon as the script has a warning, as if");
        lines.Add("                  the warning were a static error; the warning rendering is unchanged.");
        lines.Add("  --fix           (check) Applies the safe fixes carried by the diagnostics to the script");
        lines.Add("                  file (never to the standard input), then checks it again. A final");
        lines.Add("                  symbolic link is refused.");
        lines.Add("  --              (run) End of options; useful if the script is named -x.scs.");
        lines.Add("  -               (run, check) Reads the script from the standard input, up to the end of");
        lines.Add("                  the stream: scripts run - < script.scs.");
        lines.Add("  --format json   (describe) Only format available; mandatory.");
        lines.Add("  --check         (fmt) Required; check the layout, never rewrite the file.");
        lines.Add("  --name NAME     (describe) Keeps the entries of a namespace, a module, a receiver type");
        lines.Add("                  or a fully qualified name: --name process, --name list,");
        lines.Add("                  --name filesystem.read_file.");
        lines.Add("  --version       Prints the version (scripts " + LanguageReference.Version + ") and exits.");
        lines.Add("  --help          Prints this help and exits.");
        lines.Add(string.Empty);
        lines.Add("The entry script must be a regular file (or a character device such as /dev/null); a");
        lines.Add("pipe or process substitution is passed as -.");
        lines.Add(string.Empty);
        lines.Add("Options precede the script. Everything after the script path is an argument of the");
        lines.Add("script, including words starting with - (read with process.arguments()). A scripts option");
        lines.Add("written after the script and before a possible -- is rejected; to pass an option name to the");
        lines.Add("script, write: scripts run script.scs -- --json-out (the -- is itself an argument). A shebang is");
        lines.Add("\"#!/usr/bin/env -S scripts run\", or \"#!/usr/bin/scripts run\" for the packaged binary.");
        lines.Add(string.Empty);
        lines.Add("EXIT CODES");
        foreach (var exitCode in LanguageReference.ExitCodeTable)
        {
            var label = exitCode.Code == exitCode.LastCode ? $"{exitCode.Code}" : $"{exitCode.Code}-{exitCode.LastCode}";
            lines.Add($"  {label,-6}  {exitCode.Meaning}");
        }

        lines.Add(string.Empty);
        lines.Add("EXAMPLES");
        lines.Add("  scripts run backup.scs /home /srv        Runs the script with two arguments");
        lines.Add("  scripts run - < script.scs               Runs the script read from the standard input");
        lines.Add("  scripts check --errors-json script.scs   Checks and writes diagnostics as JSON on stdout");
        lines.Add("  scripts run --json-out script.scs        Runs and writes the full result as JSON");
        lines.Add("  scripts explain script.scs               Describes in English what the script does");
        lines.Add("  scripts fmt --check script.scs           Exit 251 if the layout is not canonical");
        lines.Add(string.Empty);
        lines.Add("REFERENCE");
        lines.Add("  scripts describe --format json provides the machine-readable reference.");
        return string.Join('\n', lines) + '\n';
    }
}
