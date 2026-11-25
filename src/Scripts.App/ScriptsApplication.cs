using System.Linq;
using System.Text;
using Scripts.Checker;
using Scripts.Cli;
using Scripts.Core;
using Scripts.Core.Reporting;
using Scripts.Runtime;
using Scripts.Runtime.Interpreter;
using Scripts.Runtime.Output;
using Scripts.Runtime.Platform;
using Scripts.Syntax;

namespace Scripts.App;

/// <summary>
/// Composition root of the scripts binary (DESIGN §9, R2): command line -> load -> check -> interpret, with the
/// file-descriptor output discipline (A5: no <c>Console.*</c>, deterministic behaviour on a closed stream).
/// Returns the process exit code; <see cref="Run"/> is the single place that knows every code of DESIGN §6.
/// </summary>
public static class ScriptsApplication
{
    /// <summary>Entry point used by <c>src/Scripts.App/Program.cs</c> and by in-process tests.</summary>
    public static int Run(IReadOnlyList<string> arguments)
    {
        standardOutputFailureReason = null;
        StandardOutputWasBrokenPipe = false;
        try
        {
            var exitCode = Dispatch(arguments);
            // A closed stdout must not be a silent 253 (language principle 4): EPIPE keeps its silent 141,
            // every other write failure is the catalogued SCRIPTS-RUNTIME-0019 on stderr.
            if (standardOutputFailureReason is { } reason)
            {
                WriteStandardError(DiagnosticReporter.RenderHuman(
                    [Diagnostics.Create(ErrorCodes.Runtime0019, null, default, ("stream", "stdout"), ("reason", reason))],
                    []));
            }

            return exitCode;
        }
        catch (Exception)
        {
            // Internal-error policy (DESIGN §8, R2/S9): an unexpected exception (a scripts bug) becomes a fixed
            // message on stderr and exit code 254 — never a stack trace, never a crash (code 134).
            WriteStandardError("scripts internal error: command processing failed\n");
            return ExitCodes.InternalError;
        }
    }

    private static int Dispatch(IReadOnlyList<string> arguments)
    {
        // The argument vector is decoded to UTF-16 by the runtime; a scripts option is always ASCII, so this is safe.
        var parseResult = CommandLineParser.Parse(arguments);
        if (parseResult is CommandLineRejected rejected)
        {
            // DESIGN §9: `scripts` without arguments prints the full help on stderr, then the diagnostic, exit code 250.
            if (arguments.Count == 0)
            {
                WriteStandardError(HelpText.Full);
            }

            // `check --errors-json` writes its document to stdout even when the command line is rejected
            // (DESIGN §8.3; audit M2), unlike run whose line goes to stderr.
            if (arguments.Count > 0
                && arguments[0] == "check"
                && arguments.Contains("--errors-json", StringComparer.Ordinal))
            {
                return WriteCheckJson([rejected.Diagnostic], [], false);
            }

            return ReportCommandLineError(rejected.Diagnostic, arguments);
        }

        var command = ((CommandLineAccepted)parseResult).Command;
        return command switch
        {
            VersionCommand => WriteTextAndReturn(HelpText.VersionLine + "\n", ExitCodes.Success),
            HelpCommand => WriteTextAndReturn(HelpText.Full, ExitCodes.Success),
            DescribeCommand describe => WriteTextAndReturn(DescribeDocument.Render(describe.Name), ExitCodes.Success),
            FormatCommand format => RunFormat(format),
            CheckCommand check => RunCheck(check),
            RunCommand run => RunScript(run),
            ExplainCommand explain => RunExplain(explain),
            _ => InternalError(),
        };
    }

    /// <summary>Writes to stdout then returns <paramref name="successCode"/>, or 141 when the pipe was closed.</summary>
    private static int WriteTextAndReturn(string text, int successCode) =>
        WriteStandardOutput(text) ? successCode : FailedOutputExitCode;

    // ----------------------------------------------------------------- source loading

    private static LoadResult LoadProgram(string scriptPath, bool standardInput)
    {
        if (!standardInput)
        {
            scriptPath = ResolveScriptPath(scriptPath);
            if (RefuseNonRegularEntry(scriptPath) is { } refusal)
            {
                return new LoadResult(null, refusal);
            }

            return ProgramLoader.Load(scriptPath, new FileSystemSourceProvider());
        }

        // DESIGN §9: `-` reads the entry script from the standard input, up to the source size limit plus one byte so
        // that the loader can report an oversized source; a closed stream is an empty (valid) script.
        var reading = ReadScriptFromStandardInput();
        return reading.Succeeded
            ? ProgramLoader.LoadStandardInput(reading.Bytes, new FileSystemSourceProvider())
            : new LoadResult(null, Diagnostics.Create(ErrorCodes.Cli0010, null, default, ("reason", reading.FailureReason)));
    }

    /// <summary>
    /// <c>scripts run foo</c> resolves to <c>foo.scs</c> when <c>foo</c> does not exist and has no extension: an exact
    /// path always wins, and a missing path keeps the name the user typed so the error cites it.
    /// </summary>
    private static string ResolveScriptPath(string scriptPath)
    {
        if (Path.HasExtension(scriptPath) || File.Exists(scriptPath) || Directory.Exists(scriptPath))
        {
            return scriptPath;
        }

        var candidate = scriptPath + ".scs";
        return File.Exists(candidate) ? candidate : scriptPath;
    }

    /// <summary>
    /// BUG-020: a FIFO or a socket would block or fail in a read/open. The best-effort pre-check (TOCTOU with the
    /// later open) reuses the loader's own <c>SCRIPTS-CLI-0009</c> family; a missing path falls through to the loader.
    /// </summary>
    private static Diagnostic? RefuseNonRegularEntry(string scriptPath) =>
        EntryFileProbe.IsLoadable(EntryFileProbe.Classify(scriptPath))
            ? null
            : Diagnostics.Create(
                ErrorCodes.Cli0009,
                null,
                default,
                ("path", scriptPath),
                ("reason", EntryFileProbe.NotRegularReason));

    /// <summary>
    /// Reads standard input for <c>scripts run -</c> / <c>scripts check -</c>: at most <see cref="Limits.MaximumSourceBytes"/> + 1
    /// bytes, reported as a failure when it cannot be read (SCRIPTS-CLI-0010). A closed standard input is an empty script
    /// (DESIGN §9); a directory is SCRIPTS-CLI-0010.
    /// </summary>
    private static StandardInputReadResult ReadScriptFromStandardInput() =>
        StandardInputSource.Read(Limits.MaximumSourceBytes + 1);

    private static int ReportLoadError(Diagnostic error) =>
        IsUnreadableScript(error) ? ReportScriptUnreadable(error) : ReportStaticErrors([error], []);

    /// <summary>DESIGN §9: a missing script (CLI-0009) and an unreadable standard input (CLI-0010) both exit with code 252.</summary>
    private static bool IsUnreadableScript(Diagnostic error) =>
        error.Code is ErrorCodes.Cli0009 or ErrorCodes.Cli0010;

    /// <summary>A script-file I/O failure (unreadable, or not rewritable by --fix) keeps the 252 family.</summary>
    private static bool IsScriptIoError(Diagnostic error) =>
        IsUnreadableScript(error) || error.Code == ErrorCodes.Cli0014;

    /// <summary>Exit code of a load failure, whatever its rendering: 252 for an unreadable script, 251 for any other load error.</summary>
    private static int LoadErrorExitCode(Diagnostic error) =>
        IsScriptIoError(error) ? ExitCodes.ScriptUnreadable : ExitCodes.StaticError;

    /// <summary>Unreadable script errors are reported like static errors (human text on stderr), but with code 252.</summary>
    private static int ReportScriptUnreadable(Diagnostic error)
    {
        var text = DiagnosticReporter.RenderHuman([error], []);
        return WriteStandardError(text) ? ExitCodes.ScriptUnreadable : ExitCodes.RuntimeFailure;
    }

    // ----------------------------------------------------------------- check
    /// <summary>
    /// <c>scripts format --check</c> (P9c): layout only, no code rewriting. Exit 0 when canonical, 251 otherwise, with
    /// one <c>path:line:column: not canonical: …</c> line per violation on stderr. A BOM is refused before decoding
    /// (SourceReading strips it silently); lexical and source-size errors use the normal 251/252 rendering.
    /// </summary>
    private static int RunFormat(FormatCommand command)
    {
        if (command.LegacyAlias)
        {
            WriteStandardError("warning: `scripts fmt` is deprecated; use `scripts format`\n");
        }

        byte[] bytes;
        string path;
        if (command.StandardInput)
        {
            var reading = ReadScriptFromStandardInput();
            if (!reading.Succeeded)
            {
                return ReportLoadError(Diagnostics.Create(ErrorCodes.Cli0010, null, default, ("reason", reading.FailureReason)));
            }

            bytes = reading.Bytes;
            path = ProgramLoader.StandardInputSourceName;
        }
        else
        {
            var scriptPath = ResolveScriptPath(command.ScriptPath);
            // BUG-020: `format` reads the entry file directly; a FIFO or a socket must fail fast like run/check/explain.
            if (RefuseNonRegularEntry(scriptPath) is { } refusal)
            {
                return ReportLoadError(refusal);
            }

            var provider = new FileSystemSourceProvider();
            if (!provider.TryReadBytes(scriptPath, out bytes, out var reason))
            {
                return ReportLoadError(Diagnostics.Create(
                    ErrorCodes.Cli0009,
                    null,
                    default,
                    ("path", scriptPath),
                    ("reason", reason)));
            }

            path = scriptPath;
        }

        var source = SourceReading.Read(path, bytes);
        if (source.Error is not null)
        {
            return ReportLoadError(source.Error);
        }

        var issues = LayoutCheck.Check(source.Source!.Text).ToList();
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            issues.Insert(0, new LayoutIssue(1, 1, "UTF-8 byte-order mark is forbidden"));
        }

        if (issues.Count == 0)
        {
            return ExitCodes.Success;
        }

        foreach (var issue in issues)
        {
            if (!WriteStandardError($"{path}:{issue.Line}:{issue.Column}: not canonical: {issue.Message}\n"))
            {
                return ExitCodes.RuntimeFailure;
            }
        }

        return ExitCodes.StaticError;
    }

    private static int RunCheck(CheckCommand command)
    {
        if (!command.StandardInput)
        {
            // Resolve `scripts check foo` to `foo.scs` once: the symbolic-link guard, the
            // applied-fixes message and the reload must all name the file that was read.
            command = command with { ScriptPath = ResolveScriptPath(command.ScriptPath) };
        }

        if (command.Fix && command.StandardInput)
        {
            var diagnostic = Diagnostics.Create(ErrorCodes.Cli0012, null, new Span(0, 0));
            if (command.ErrorsJson)
            {
                return WriteCheckJson([diagnostic], [], command.DenyWarnings);
            }

            return WriteStandardError(DiagnosticReporter.RenderHuman([diagnostic], []))
                ? ExitCodes.Usage
                : ExitCodes.RuntimeFailure;
        }

        // `--fix` refuses a final symbolic link before loading, so the help sentence holds even when the
        // script is clean and the writer would never run. The O_NOFOLLOW open remains the write-time check.
        if (command.Fix && NoFollowFileWriter.IsFinalSymbolicLink(command.ScriptPath))
        {
            var failure = Diagnostics.Create(
                ErrorCodes.Cli0014,
                null,
                new Span(0, 0),
                ("reason", NoFollowFileWriter.SymbolicLinkReason));
            if (command.ErrorsJson)
            {
                return WriteCheckJson([failure], [], command.DenyWarnings);
            }

            return WriteStandardError(DiagnosticReporter.RenderHuman([failure], []))
                ? ExitCodes.ScriptUnreadable
                : ExitCodes.RuntimeFailure;
        }

        var loaded = LoadProgram(command.ScriptPath, command.StandardInput);
        var applied = 0;

        // A syntax error may carry a safe fix too (L6: `{{` in a string). The parser stops at the first error, so the
        // entry is rewritten and reloaded until it parses or its error has no fix; each rewrite removes a `{{` or `}}`.
        while (command.Fix && loaded.Error?.Fix is { File: { } fixedFile } && fixedFile.Path == command.ScriptPath)
        {
            var textBefore = fixedFile.Text;
            int appliedToSyntax;
            try
            {
                appliedToSyntax = ApplySafeFixes(fixedFile, [loaded.Error]);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return ReportFixWriteFailure(command, exception);
            }

            if (appliedToSyntax == 0)
            {
                break;
            }

            applied += appliedToSyntax;
            loaded = LoadProgram(command.ScriptPath, false);
            // A fix that rewrites the same text would loop forever: stop as soon as the entry did not change.
            if (loaded.Error?.Fix?.File?.Text == textBefore)
            {
                break;
            }
        }

        if (loaded.Error is not null)
        {
            if (applied > 0 && !command.ErrorsJson &&
                !WriteStandardOutput($"applied {applied} fix(es) to {command.ScriptPath}\n"))
            {
                return ExitCodes.RuntimeFailure;
            }

            // DESIGN §8.3: under --errors-json, check always writes the JSON document on stdout, including load errors.
            if (command.ErrorsJson)
            {
                return WriteCheckJson([loaded.Error], [], command.DenyWarnings);
            }

            return ReportLoadError(loaded.Error);
        }

        var result = ProgramChecker.Check(loaded.Program!);
        if (command.Fix)
        {
            // A warning's fix (L5: rename an unread binding) is applied only to a program without errors: an error fix
            // such as a typo rename may make that binding read, and the two edits together would break the script.
            IReadOnlyList<Diagnostic> fixable = result.Errors.Count > 0 ? result.Errors : result.Warnings;
            var moduleFixes = fixable.Count(diagnostic => diagnostic.Fix is { File: not null }
                && diagnostic.Fix.File.Path != loaded.Program!.Entry.Source.Path);
            int appliedToProgram;
            try
            {
                appliedToProgram = ApplySafeFixes(loaded.Program!.Entry.Source, fixable);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return ReportFixWriteFailure(command, exception);
            }

            if (moduleFixes > 0 && !command.ErrorsJson)
            {
                WriteStandardError(
                    $"{moduleFixes} fix(es) in imported modules were not applied: --fix rewrites the entry script only\n");
            }

            if (appliedToProgram > 0)
            {
                var reloaded = LoadProgram(command.ScriptPath, false);
                if (reloaded.Error is not null)
                {
                    if (command.ErrorsJson)
                    {
                        return WriteCheckJson([reloaded.Error], [], command.DenyWarnings);
                    }

                    return ReportLoadError(reloaded.Error);
                }

                result = ProgramChecker.Check(reloaded.Program!);
            }

            applied += appliedToProgram;
            if (applied > 0 && !command.ErrorsJson &&
                !WriteStandardOutput($"applied {applied} fix(es) to {command.ScriptPath}\n"))
            {
                return ExitCodes.RuntimeFailure;
            }
        }

        if (command.ErrorsJson)
        {
            return WriteCheckJson(result.Errors, result.Warnings, command.DenyWarnings);
        }

        if (result.Errors.Count > 0 || result.Warnings.Count > 0)
        {
            var text = DiagnosticReporter.RenderHuman(result.Errors, result.Warnings);
            if (!WriteStandardError(text))
            {
                return ExitCodes.RuntimeFailure;
            }
        }

        // `--deny-warnings` (DESIGN §8.5): a warning makes the check fail (251) like a static error, without
        // changing how it is rendered.
        var deniedWarning = command.DenyWarnings && result.Warnings.Count > 0;
        return result.Errors.Count == 0 && !deniedWarning ? ExitCodes.Success : ExitCodes.StaticError;
    }

    /// <summary>
    /// A read-only file or a full disk is an environment condition, not a scripts bug (audit B1): report a typed CLI
    /// error (252) instead of the internal-error path (254).
    /// </summary>
    private static int ReportFixWriteFailure(CheckCommand command, Exception exception)
    {
        var failure = Diagnostics.Create(ErrorCodes.Cli0014, null, new Span(0, 0), ("reason", exception.Message));
        if (command.ErrorsJson)
        {
            return WriteCheckJson([failure], [], command.DenyWarnings);
        }

        return WriteStandardError(DiagnosticReporter.RenderHuman([failure], []))
            ? ExitCodes.ScriptUnreadable
            : ExitCodes.RuntimeFailure;
    }

    /// <summary>
    /// Applies the safe fixes carried by the diagnostics to the entry file, in reverse span order, and returns how many
    /// edits were applied. A module's file is left untouched: <c>check</c> names the entry script.
    /// </summary>
    private static int ApplySafeFixes(SourceFile entry, IReadOnlyList<Diagnostic> errors)
    {
        var entryPath = entry.Path;
        var fixes = errors
            .Select(error => error.Fix)
            .Where(fix => fix is { File: not null } && fix.File.Path == entryPath)
            .Select(fix => fix!)
            .OrderByDescending(fix => fix.Span.Start)
            .ToList();
        if (fixes.Count == 0)
        {
            return 0;
        }

        var text = entry.Text;
        var applied = 0;
        var editedFrom = text.Length;
        foreach (var fix in fixes)
        {
            // Fixes are applied from the end of the file; one overlapping an edit already made is skipped.
            if (fix.Span.Start < 0 || fix.Span.End > editedFrom || fix.Span.Start > fix.Span.End)
            {
                continue;
            }

            text = string.Concat(text.AsSpan(0, fix.Span.Start), fix.Replacement, text.AsSpan(fix.Span.End));
            editedFrom = fix.Span.Start;
            applied++;
        }

        if (applied > 0)
        {
            // BUG-021: SourceReading strips the BOM before decoding, so a rewrite would drop it. Read the
            // original bytes and re-encode exactly one U+FEFF, which UTF8Encoding(false) writes as EF BB BF;
            // a file without a BOM gains none. The BUG-017 no-follow pre-check already refused a symlink.
            var originalText = entry.Text;
            if (StartsWithUtf8ByteOrderMark(entryPath))
            {
                originalText = "\uFEFF" + originalText;
                text = "\uFEFF" + text;
            }

            // In-place write (mode and owner preserved). A failed write is rolled back to the original
            // text: the CLI contract says a failed `--fix` leaves the script as it was, and a disk-full
            // error is exactly the case where the truncated file must not be lost.
            using var stream = NoFollowFileWriter.OpenAndTruncate(entryPath);
            try
            {
                stream.Write(new UTF8Encoding(false).GetBytes(text));
                stream.Flush(flushToDisk: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                TryRestore(stream, originalText);
                throw;
            }
        }

        return applied;
    }

    /// <summary>Best-effort rollback of a failed in-place rewrite; a second failure is swallowed for the caller to report.</summary>
    private static void TryRestore(FileStream stream, string originalText)
    {
        try
        {
            stream.Position = 0;
            stream.SetLength(0);
            stream.Write(new UTF8Encoding(false).GetBytes(originalText));
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The original text could not be restored; the write failure is still reported by the caller.
        }
    }

    /// <summary>True when the entry file starts with the UTF-8 byte-order mark <c>EF BB BF</c>.</summary>
    private static bool StartsWithUtf8ByteOrderMark(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> head = stackalloc byte[3];
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return read == head.Length && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
    }

    /// <summary>Writes the <c>--errors-json</c> document of <c>check</c> on stdout and returns its exit code.</summary>
    private static int WriteCheckJson(IReadOnlyList<Diagnostic> errors, IReadOnlyList<Diagnostic> warnings, bool denyWarnings)
    {
        var document = DiagnosticReporter.RenderErrorsJsonDocument(errors, warnings, denyWarnings);
        var written = WriteStandardOutput(document + "\n");
        var deniedWarning = denyWarnings && warnings.Count > 0;
        // DESIGN §9: an unreadable script (CLI-0009, CLI-0010) keeps exit code 252 under --errors-json, and every
        // other CLI usage error keeps 250 (audit F3); only a static error is 251.
        var exitCode = errors.Count > 0 && errors.All(IsScriptIoError)
            ? LoadErrorExitCode(errors[0])
            : errors.Count > 0 && errors.All(error => error.Code.StartsWith("SCRIPTS-CLI-", StringComparison.Ordinal))
                ? ExitCodes.Usage
                : errors.Count == 0 && !deniedWarning ? ExitCodes.Success : ExitCodes.StaticError;
        return written ? exitCode : FailedOutputExitCode;
    }

    // ----------------------------------------------------------------- run

    private static int RunScript(RunCommand command) =>
        command.JsonOut ? RunScriptJson(command) : RunScriptHuman(command);

    private static int RunScriptHuman(RunCommand command)
    {
        var loaded = LoadProgram(command.ScriptPath, command.StandardInput);
        if (loaded.Error is not null)
        {
            var exitCode = ReportLoadError(loaded.Error);
            if (command.ErrorsJson)
            {
                WriteErrorsJsonLine([loaded.Error], []);
            }

            return exitCode;
        }

        var check = ProgramChecker.Check(loaded.Program!);
        if (check.Errors.Count > 0)
        {
            var exitCode = ReportStaticErrors(check.Errors, check.Warnings);
            if (command.ErrorsJson)
            {
                WriteErrorsJsonLine(check.Errors, check.Warnings);
            }

            return exitCode;
        }

        if (check.Warnings.Count > 0)
        {
            WriteStandardError(DiagnosticReporter.RenderHuman([], check.Warnings));
        }

        var result = RunOnLargeStack(() =>
        {
            var context = RuntimeContext.CreateForConsole(command.ScriptArguments);
            try
            {
                var outcome = ProgramInterpreter.Run(loaded.Program!, context);
                var shutdown = ShutdownAndCaptureFailure(context);
                return (
                    Outcome: outcome,
                    FlushFailure: shutdown.Failure,
                    OutputBrokenPipe: shutdown.BrokenPipe || context.DeferredBrokenPipe);
            }
            finally
            {
                context.Dispose();
            }
        });
        return ReportExecutionOutcome(
            result.Outcome, result.FlushFailure, result.OutputBrokenPipe, command.ErrorsJson, check.Warnings);
    }

    /// <summary>
    /// Human-mode wrapper: a RuntimeFailure raised before the interpreter starts (e.g. the large-stack thread cannot be
    /// created under a low virtual-memory limit, red-team R2-2) becomes a runtime outcome (diagnostic + code 253)
    /// instead of escaping as an unhandled exception.
    /// </summary>
    private static (ExecutionOutcome Outcome, Diagnostic? FlushFailure, bool OutputBrokenPipe) RunOnLargeStack(
        Func<(ExecutionOutcome Outcome, Diagnostic? FlushFailure, bool OutputBrokenPipe)> work)
    {
        try
        {
            return LargeStackThread.Run(work);
        }
        catch (RuntimeFailure failure)
        {
            var diagnostic = Diagnostics.Create(failure.Code, null, default, failure.Arguments);
            return (new ExecutionOutcome(ExitCodes.RuntimeFailure, RunTermination.RuntimeError, diagnostic, null), null, false);
        }
    }

    private static int RunScriptJson(RunCommand command)
    {
        var loaded = LoadProgram(command.ScriptPath, command.StandardInput);
        if (loaded.Error is not null)
        {
            return ReportLoadErrorJson(loaded.Error);
        }

        var check = ProgramChecker.Check(loaded.Program!);
        if (check.Errors.Count > 0)
        {
            var staticDocument = DiagnosticReporter.RenderRunJsonDocument(
                ExitCodes.StaticError, RunTermination.StaticError, string.Empty, string.Empty, null, check.Errors, check.Warnings);
            WriteStandardOutput(staticDocument + "\n");
            return ExitCodes.StaticError;
        }

        (ExecutionOutcome Outcome, CapturedOutput Captured) result;
        try
        {
            result = LargeStackThread.Run(() =>
            {
                var context = RuntimeContext.CreateForCapture(command.ScriptArguments, out var captured);
                try
                {
                    var outcome = ProgramInterpreter.Run(loaded.Program!, context);
                    return (Outcome: outcome, Captured: captured);
                }
                finally
                {
                    ShutdownAndCaptureFailure(context);
                    context.Dispose();
                }
            });
        }
        catch (RuntimeFailure failure)
        {
            // Pre-interpreter failure (e.g. the large-stack thread cannot start, R2-2): report a runtime-error document.
            var diagnostic = Diagnostics.Create(failure.Code, null, default, failure.Arguments);
            var failureDocument = DiagnosticReporter.RenderRunJsonDocument(
                ExitCodes.RuntimeFailure, RunTermination.RuntimeError, string.Empty, string.Empty, null, [diagnostic], check.Warnings);
            return WriteStandardOutput(failureDocument + "\n") ? ExitCodes.RuntimeFailure : FailedOutputExitCode;
        }

        var runtimeErrors = result.Outcome.Failure is null ? new List<Diagnostic>() : [result.Outcome.Failure];
        var resultJson = result.Outcome.Result is { } value ? ResultJson.Render(value) : null;
        var exitCode = result.Outcome.ExitCode;
        var document = DiagnosticReporter.RenderRunJsonDocument(
            exitCode,
            result.Outcome.Termination,
            result.Captured.ReadStandardOutput(),
            result.Captured.ReadStandardError(),
            resultJson,
            runtimeErrors,
            check.Warnings);
        return WriteStandardOutput(document + "\n") ? exitCode : FailedOutputExitCode;
    }

    /// <summary>A load failure under <c>--json-out</c> is a static error document with termination <c>static_error</c> and code 252 for an unreadable script.</summary>
    private static int ReportLoadErrorJson(Diagnostic error)
    {
        var exitCode = LoadErrorExitCode(error);
        var document = DiagnosticReporter.RenderRunJsonDocument(
            exitCode, RunTermination.StaticError, string.Empty, string.Empty, null, [error], []);
        WriteStandardOutput(document + "\n");
        return exitCode;
    }

    // ----------------------------------------------------------------- explain (R3)

    private static int RunExplain(ExplainCommand command)
    {
        // explain never receives `-` (DESIGN §9: the parser keeps it an option, SCRIPTS-CLI-0003).
        var loaded = LoadProgram(command.ScriptPath, standardInput: false);
        if (loaded.Error is not null)
        {
            return command.JsonOut ? WriteCheckJson([loaded.Error], [], denyWarnings: false) : ReportLoadError(loaded.Error);
        }

        var check = ProgramChecker.Check(loaded.Program!);
        if (!command.JsonOut && (check.Errors.Count > 0 || check.Warnings.Count > 0))
        {
            WriteStandardError(DiagnosticReporter.RenderHuman(check.Errors, check.Warnings));
        }

        if (check.Errors.Count > 0)
        {
            // DESIGN §9: the machine form reports the same static error as `check --errors-json` (exit 251).
            return command.JsonOut ? WriteCheckJson(check.Errors, check.Warnings, denyWarnings: false) : ExitCodes.StaticError;
        }

        if (command.JsonOut)
        {
            var document = ProgramExplainer.ExplainJson(loaded.Program!, check.Warnings);
            return WriteStandardOutput(document + "\n") ? ExitCodes.Success : FailedOutputExitCode;
        }

        var explanation = ProgramExplainer.Explain(loaded.Program!, check.Warnings);
        return WriteTextAndReturn(explanation, ExitCodes.Success);
    }

    // ----------------------------------------------------------------- outcomes

    /// <summary>Human run termination: failure text on stderr, optional trailing JSON line, exit code of DESIGN §6.</summary>
    private static int ReportExecutionOutcome(
        ExecutionOutcome outcome,
        Diagnostic? flushFailure,
        bool outputBrokenPipe,
        bool errorsJson,
        IReadOnlyList<Diagnostic> warnings)
    {
        // A failed final flush (SCRIPTS-RUNTIME-0019, e.g. a full stdout) is a real execution failure: it must not be
        // swallowed (DESIGN §0.4: no silent failure). EPIPE during the final flush or a deferred action is not a
        // failure, but it must still surface as the documented silent 141 when the run was a success termination
        // (normal end or an explicit process.exit_with_code(0)). A non-zero process.exit_with_code(n), a runtime failure and a signal
        // keep their own code.
        var failure = outcome.Failure ?? flushFailure;
        var exitCode = outcome.ExitCode;
        if (outputBrokenPipe && outcome.Failure is null && outcome.ExitCode == ExitCodes.Success)
        {
            exitCode = ExitCodes.BrokenPipe;
        }
        else if (flushFailure is not null && outcome.Failure is null)
        {
            exitCode = ExitCodes.RuntimeFailure;
        }

        if (failure is not null)
        {
            WriteStandardError(DiagnosticReporter.RenderHuman([failure], []));
        }

        // DESIGN §8.3: run writes the JSON line on stderr only when there is at least one diagnostic.
        if (errorsJson && (failure is not null || warnings.Count > 0))
        {
            WriteErrorsJsonLine(failure is null ? [] : [failure], warnings);
        }

        return exitCode;
    }

    /// <summary>
    /// Ends the run: terminates unwaited children and flushes both streams. Returns the write failure of the final
    /// flush (SCRIPTS-RUNTIME-0019) when there is one, plus whether stdout was an EPIPE (silent 141 when the run was
    /// otherwise successful).
    /// </summary>
    private static (Diagnostic? Failure, bool BrokenPipe) ShutdownAndCaptureFailure(RuntimeContext context)
    {
        try
        {
            context.Shutdown();
            return (null, false);
        }
        catch (BrokenPipeTermination)
        {
            // stdout closed by the reader: silent; the caller maps this to 141 only for an otherwise successful run.
            return (null, true);
        }
        catch (RuntimeFailure failure)
        {
            return (Diagnostics.Create(failure.Code, null, default, failure.Arguments), false);
        }
    }

    // ----------------------------------------------------------------- static error paths

    private static int ReportCommandLineError(Diagnostic diagnostic, IReadOnlyList<string> arguments)
    {
        // DESIGN §8.4: a CLI error under --json-out is a static_error document; §8.3: --errors-json
        // appends its JSON line on stderr. The parser rejected the command line, so the flags are
        // read from the raw argument vector.
        if (arguments.Contains("--json-out", StringComparer.Ordinal))
        {
            var document = DiagnosticReporter.RenderRunJsonDocument(
                ExitCodes.Usage, RunTermination.StaticError, string.Empty, string.Empty, null, [diagnostic], []);
            return WriteStandardOutput(document + "\n") ? ExitCodes.Usage : FailedOutputExitCode;
        }

        WriteStandardError(DiagnosticReporter.RenderHuman([diagnostic], []));
        if (arguments.Contains("--errors-json", StringComparer.Ordinal))
        {
            WriteErrorsJsonLine([diagnostic], []);
        }

        return ExitCodes.Usage;
    }

    private static int ReportStaticErrors(IReadOnlyList<Diagnostic> errors, IReadOnlyList<Diagnostic> warnings)
    {
        var text = DiagnosticReporter.RenderHuman(errors, warnings);
        return WriteStandardError(text) ? ExitCodes.StaticError : ExitCodes.StaticError;
    }

    private static int InternalError()
    {
        WriteStandardError("internal error: unknown command\n");
        return ExitCodes.InternalError;
    }

    // ----------------------------------------------------------------- raw streams (A5: never Console.*)

    /// <summary>Writes the <c>--errors-json</c> document on stderr as the last line, preceded by a newline when needed.</summary>
    private static void WriteErrorsJsonLine(IReadOnlyList<Diagnostic> errors, IReadOnlyList<Diagnostic> warnings)
    {
        var document = DiagnosticReporter.RenderErrorsJsonDocument(errors, warnings);
        // A child that writes to inherited stderr without a trailing newline leaves the stream mid-line; scripts
        // cannot know its column, so the machine line is always introduced by a newline (audit M3). The JSON
        // document is then the last line, as DESIGN §8.3 promises.
        WriteStandardError("\n" + document + "\n");
    }

    /// <summary>UTF-8 (no BOM) write on fd 1; returns false when the stream is unusable. Shared with the CLI writers.</summary>
    internal static bool WriteStandardOutput(string text)
    {
        var channel = FileDescriptorOutputChannel.ForStandardOutput();
        try
        {
            channel.Write(text);
            channel.Flush();
            return true;
        }
        catch (BrokenPipeTermination)
        {
            StandardOutputWasBrokenPipe = true;
            return false;
        }
        catch (Exception exception) when (exception is RuntimeFailure or IOException)
        {
            // A RuntimeFailure carries the rendered reason in its arguments, not in Message (the code).
            standardOutputFailureReason ??= exception is RuntimeFailure failure
                ? failure.Arguments.FirstOrDefault(argument => argument.Name == "reason").Value ?? failure.Code
                : exception.Message;
            return false;
        }
    }

    /// <summary>True when the last stdout write failed because the reader closed the pipe (EPIPE, code 141).</summary>
    private static bool StandardOutputWasBrokenPipe { get; set; }

    /// <summary>The reason of the last non-EPIPE stdout failure, reported as SCRIPTS-RUNTIME-0019 by <see cref="Run"/>.</summary>
    private static string? standardOutputFailureReason;

    /// <summary>Exit code when stdout could not be written: 141 for EPIPE, 253 for any other write failure.</summary>
    internal static int FailedOutputExitCode => StandardOutputWasBrokenPipe ? ExitCodes.BrokenPipe : ExitCodes.RuntimeFailure;

    /// <summary>UTF-8 (no BOM) write on fd 2; never throws (A5: stderr unusable must not crash the process). Shared with the CLI writers.</summary>
    internal static bool WriteStandardError(string text)
    {
        var channel = FileDescriptorOutputChannel.ForStandardError();
        try
        {
            channel.Write(text);
            channel.Flush();
            return true;
        }
        catch (Exception exception) when (exception is RuntimeFailure or BrokenPipeTermination or IOException)
        {
            return false;
        }
    }
}
