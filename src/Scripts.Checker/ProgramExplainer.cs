using System.Text;
using Scripts.Core;
using Scripts.Core.Json;
using CoreCallArgument = Scripts.Core.CallArgument;

namespace Scripts.Checker;

/// <summary>
/// <c>scripts explain</c> (DESIGN §9, R3): a deterministic English description of a checked program, one sentence per
/// statement, indented by nesting. Reads the annotated AST (call targets resolved by the checker). Never executes.
/// </summary>
public static class ProgramExplainer
{
    /// <summary>Renders the explanation of the entry script, followed by the checker's warnings when any.</summary>
    public static string Explain(LoadedProgram program, IReadOnlyList<Diagnostic> warnings)
    {
        var builder = new StringBuilder();
        builder.Append("Script `").Append(program.Entry.Source.Path).Append("`:\n");
        foreach (var step in Steps(program))
        {
            builder.Append(' ', step.Depth * 2).Append(step.Text).Append('\n');
        }

        if (warnings.Count > 0)
        {
            builder.Append('\n').Append("Warnings:\n");
            foreach (var warning in warnings)
            {
                builder.Append("  [").Append(warning.Code).Append("] ").Append(warning.Message).Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The machine form of <see cref="Explain"/> (DESIGN §9): one object per statement, in document order, whose text
    /// is the very sentence the human rendering prints. Both come from <see cref="Steps"/>, so they cannot diverge.
    /// </summary>
    public static string ExplainJson(LoadedProgram program, IReadOnlyList<Diagnostic> warnings)
    {
        var writer = new JsonWriter();
        writer.BeginObject();
        writer.WriteProperty("schema_version", LanguageReference.SchemaVersion);
        writer.WriteProperty("command", "explain");
        writer.WriteProperty("script", program.Entry.Source.Path);
        writer.WriteName("steps");
        writer.BeginArray();
        foreach (var step in Steps(program))
        {
            writer.BeginObject();
            writer.WriteProperty("depth", step.Depth);
            writer.WriteProperty("kind", step.Kind);
            writer.WriteProperty("text", step.Text);
            writer.WriteProperty("destructive", step.Destructive);
            writer.WriteProperty("dangerous", step.Dangerous);
            // Optional keys (DESIGN 9, stability policy): the 1-based position of the statement, so a tool can jump
            // to the source line without parsing English.
            writer.WriteProperty("line", step.Line);
            writer.WriteProperty("column", step.Column);
            writer.EndObject();
        }

        writer.EndArray();
        writer.WriteName("errors");
        writer.BeginArray();
        writer.EndArray();
        writer.WriteName("warnings");
        writer.BeginArray();
        foreach (var warning in warnings)
        {
            // The full §8.3 object (position, source line, cause, suggestion), like check/run (audit M4).
            Scripts.Core.Reporting.DiagnosticReporter.WriteDiagnosticJson(writer, warning);
        }

        writer.EndArray();
        writer.EndObject();
        return writer.ToString();
    }

    /// <summary>One explained statement: its nesting level, its closed-set kind, its sentence, its flags and its position.</summary>
    public readonly record struct ExplanationStep(
        int Depth,
        string Kind,
        string Text,
        bool Destructive,
        bool Dangerous,
        int Line = 0,
        int Column = 0);

    /// <summary>Every explained statement of the entry script, in document order (the single source of both renderings).</summary>
    public static IReadOnlyList<ExplanationStep> Steps(LoadedProgram program)
    {
        var steps = new List<ExplanationStep>();
        var explainer = new Explainer(steps, program.Entry.Source);
        foreach (var statement in program.Entry.Program.Statements)
        {
            explainer.Statement(statement, depth: 1);
        }

        return steps;
    }

    private sealed class Explainer(List<ExplanationStep> steps, SourceFile source)
    {
        public void Statement(Statement statement, int depth)
        {
            switch (statement)
            {
                case LetStatement let:
                    Emit(depth, "let",
                        "declares "
                        + (let.IsMutable ? "the mutable variable " : "the constant ")
                        + "`" + let.Name + "` = " + Describe(let.Initializer),
                        let.Span);
                    break;
                case AssignmentStatement assignment:
                    Emit(depth, "assignment", DescribeAssignment(assignment), assignment.Span);
                    break;
                case ExpressionStatement expression:
                    Emit(depth, StatementKind(expression.Expression), DescribeStatementExpression(expression.Expression), expression.Span);
                    break;
                case IfStatement conditional:
                    ExplainIf(conditional, depth);
                    break;
                case WhileStatement loop:
                    Emit(depth, "while", "repeats while " + Describe(loop.Condition) + ":", loop.Span);
                    Block(loop.Body, depth + 1);
                    break;
                case ForStatement forEach:
                    Emit(depth, "for", "for each `" + forEach.VariableName + "` of " + Describe(forEach.Iterable) + ":", forEach.Span);
                    Block(forEach.Body, depth + 1);
                    break;
                case ReturnStatement ret:
                    Emit(depth, "return", ret.Value is null ? "returns" : "returns " + Describe(ret.Value), ret.Span);
                    break;
                case BreakStatement:
                    Emit(depth, "break", "breaks the loop", statement.Span);
                    break;
                case ContinueStatement:
                    Emit(depth, "continue", "continues to the next iteration", statement.Span);
                    break;
                case FunctionDeclaration function:
                    ExplainFunction(function, depth);
                    break;
                default:
                    Emit(depth, "expression", "statement", statement.Span);
                    break;
            }
        }

        /// <summary>
        /// The sentence of an assignment. The operator is part of the meaning: `total += n` is not `total = n`, so
        /// each compound form is rendered in English instead of a bare "modifies `total` with `n`".
        /// </summary>
        private string DescribeAssignment(AssignmentStatement assignment)
        {
            var target = "`" + assignment.TargetName + "`";
            var value = Describe(assignment.Value);
            return assignment.Operator switch
            {
                AssignOperator.AddAssign => "adds " + value + " to " + target,
                AssignOperator.SubtractAssign => "subtracts " + value + " from " + target,
                AssignOperator.MultiplyAssign => "multiplies " + target + " by " + value,
                AssignOperator.DivideAssign => "divides " + target + " by " + value,
                AssignOperator.ModuloAssign => "sets " + target + " to " + target + " modulo " + value,
                _ => "modifies " + target + " with " + value,
            };
        }

        /// <summary>The closed-set kind of an expression statement: a `match` statement is `match`, a call is `expression`.</summary>
        private static string StatementKind(Expression expression) =>
            expression is MatchExpression ? "match" : "expression";

        /// <summary>The span of the `else` keyword that introduces a branch or the else block, when its AST span starts after it.</summary>
        private Span ElseKeywordSpan(Span anchor)
        {
            var index = source.Text.LastIndexOf("else", anchor.Start, StringComparison.Ordinal);
            return index >= 0 ? new Span(index, index + 4) : anchor;
        }

        private void ExplainIf(IfStatement conditional, int depth)
        {
            for (var index = 0; index < conditional.Branches.Count; index++)
            {
                var branch = conditional.Branches[index];
                // An `else if` is anchored on its `else` keyword, not on the `if` (the branch span starts there).
                Emit(depth, "if", (index == 0 ? "if " : "else if ") + Describe(branch.Condition) + ":",
                    index == 0 ? branch.Span : ElseKeywordSpan(branch.Span));
                Block(branch.Body, depth + 1);
            }

            if (conditional.ElseBlock is not null)
            {
                Emit(depth, "if", "else:", ElseKeywordSpan(conditional.ElseBlock.Span));
                Block(conditional.ElseBlock, depth + 1);
            }
        }

        private void ExplainFunction(FunctionDeclaration function, int depth)
        {
            var parameters = string.Join(", ", function.Parameters.Select(parameter => $"`{parameter.Name}`"));
            Emit(
                depth,
                "function",
                "declares the function `" + function.Name + "` (" + parameters
                + (function.ReturnType is null ? ", no return value" : ", returns a value") + "):",
                function.Span);
            Block(function.Body, depth + 1);
        }

        /// <summary>
        /// Records one statement. The flags come from the sentence itself, exactly as the human rendering prefixes it:
        /// `destructive operation:` and `dangerous operation:` are produced by the stdlib explanation templates of the
        /// call the statement contains, so a tool never has to parse English.
        /// </summary>
        private void Emit(int depth, string kind, string text, Span span)
        {
            var destructive = text.Contains("destructive operation:", StringComparison.Ordinal);
            var dangerous = text.Contains("dangerous operation:", StringComparison.Ordinal);
            var (line, column) = source.GetPosition(span.Start);
            steps.Add(new ExplanationStep(depth, kind, text, destructive, dangerous, line, column));
        }

        private void Block(BlockStatement block, int depth)
        {
            foreach (var statement in block.Statements)
            {
                Statement(statement, depth);
            }
        }

        private string DescribeStatementExpression(Expression expression) => expression switch
        {
            CallExpression call => DescribeCall(call),
            MatchExpression match => "evaluates a `match` on " + Describe(match.Scrutinee),
            _ => Describe(expression),
        };

        private string DescribeCall(CallExpression call)
        {
            var target = call.Target;
            // A named call keeps its names: rendering the values in source order without them tells a different
            // story from what the runtime executes (audit P8 #1).
            var describedArguments = call.Arguments.Select(argument => Describe(argument.Expression)).ToList();
            var labelledArguments = call.Arguments
                .Select(argument => argument.Name is { } name
                    ? $"{name} = {Describe(argument.Expression)}"
                    : Describe(argument.Expression))
                .ToList();
            var receiver = call.Callee is MemberAccessExpression member ? Describe(member.Target) : null;
            return target?.Kind switch
            {
                CallTargetKind.UserFunction => $"calls the function `{target.Declaration!.Name}`({string.Join(", ", labelledArguments)})",
                CallTargetKind.ModuleFunction => $"calls `{target.ModuleAlias}.{target.Declaration!.Name}`({string.Join(", ", labelledArguments)})",
                CallTargetKind.StdlibFunction or CallTargetKind.StdlibMethod when target.Stdlib is { } entry => DescribeStdlib(entry, call.Arguments, receiver),
                _ => $"calls `{Describe(call.Callee)}`({string.Join(", ", describedArguments)})",
            };
        }

        /// <summary>Instantiates the stdlib explanation template, replacing {name} placeholders with the argument descriptions.</summary>
        private string DescribeStdlib(StdlibEntry entry, IReadOnlyList<CoreCallArgument> arguments, string? receiverDescription = null)
        {
            var rendered = entry.ExplainTemplate;
            if (entry.Kind == StdlibKind.Method && receiverDescription is not null)
            {
                rendered = rendered.Replace("{receiver}", receiverDescription, StringComparison.Ordinal);
            }

            // The template is written in parameter order; a named call carries its values by name, so the
            // substitution must follow the parameter names, not the source positions (audit F1).
            var positional = arguments.All(argument => argument.Name is null);
            for (var index = 0; index < entry.Parameters.Count; index++)
            {
                var parameter = entry.Parameters[index];
                var argument = arguments.FirstOrDefault(candidate => candidate.Name == parameter.Name)
                    ?? (positional && index < arguments.Count ? arguments[index] : null);
                var value = argument is not null ? Describe(argument.Expression) : parameter.Name;
                rendered = rendered.Replace("{" + parameter.Name + "}", value, StringComparison.Ordinal);
            }

            var prefix = entry.Destructive
                ? "destructive operation: "
                : entry.Dangerous ? "dangerous operation: " : string.Empty;
            return prefix + rendered;
        }

        private string Describe(Expression expression) => expression switch
        {
            IntegerLiteral integer => integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FloatLiteral number => number.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            BooleanLiteral boolean => boolean.Value ? "true" : "false",
            StringLiteral text => $"the string \"{text.Value}\"",
            InterpolatedString text => $"the string \"{RenderInterpolated(text.Parts)}\"",
            PathLiteral path => $"the path \"{RenderInterpolated(path.Parts)}\"",
            IdentifierExpression identifier => $"`{identifier.Name}`",
            CallExpression call => DescribeCall(call),
            ListLiteral list => $"the list [{string.Join(", ", list.Elements.Select(Describe))}]",
            MapLiteral map => $"the table {{{string.Join(", ", map.Entries.Select(entry => Describe(entry.Key) + ": " + Describe(entry.Value)))}}}",
            UnaryExpression unary => unary.Operator == UnaryOperator.Not
                ? "not " + Describe(unary.Operand)
                : "-" + Describe(unary.Operand),
            BinaryExpression binary => Describe(binary.Left) + " " + OperatorSymbol(binary.Operator) + " " + Describe(binary.Right),
            IndexExpression index => Describe(index.Target) + "[" + Describe(index.Index) + "]",
            MemberAccessExpression member => Describe(member.Target) + "." + member.MemberName,
            PropagateExpression propagate => Describe(propagate.Operand) + " (propagation of `?`)",
            OkExpression { Value: null } => "an empty `ok`",
            OkExpression ok => "a success containing " + Describe(ok.Value!),
            ErrorExpression error => "an error containing " + Describe(error.Value),
            ErrorConstructorExpression errorConstructor => "a constructed error (" + Describe(errorConstructor.Kind) + ")",
            MatchExpression match => "a `match` on " + Describe(match.Scrutinee),
            LambdaExpression => "a lambda",
            _ => "an expression",
        };

        private static string OperatorSymbol(BinaryOperator op) => op switch
        {
            BinaryOperator.Add => "+",
            BinaryOperator.Subtract => "-",
            BinaryOperator.Multiply => "*",
            BinaryOperator.Divide => "/",
            BinaryOperator.Modulo => "%",
            BinaryOperator.Equal => "==",
            BinaryOperator.NotEqual => "!=",
            BinaryOperator.Less => "<",
            BinaryOperator.LessOrEqual => "<=",
            BinaryOperator.Greater => ">",
            BinaryOperator.GreaterOrEqual => ">=",
            BinaryOperator.And => "and",
            BinaryOperator.Or => "or",
            _ => "?",
        };

        /// <summary>Reconstructs the literal text of an interpolated string, with each hole described in braces.</summary>
        private string RenderInterpolated(IReadOnlyList<StringPart> parts)
        {
            var text = new StringBuilder();
            foreach (var part in parts)
            {
                switch (part)
                {
                    case TextPart literal:
                        text.Append(literal.Text);
                        break;
                    case InterpolationPart interpolation:
                        text.Append('{').Append(Describe(interpolation.Expression)).Append('}');
                        break;
                }
            }

            return text.ToString();
        }
    }
}
