using System.Globalization;
using Scripts.Core;

namespace Scripts.Syntax.Tests;

/// <summary>Compact S-expression rendering of the AST, so tests can assert the exact shape with one string.</summary>
internal static class AstPrinter
{
    public static string Print(ProgramNode program) =>
        string.Join("\n", program.Imports.Select(Print).Concat(program.Statements.Select(Print)));

    public static string Print(Node node) =>
        node switch
        {
            ImportDeclaration import => $"(import \"{import.RelativePath}\" {import.Alias})",
            TypeSyntax type => PrintType(type),
            Expression expression => PrintExpression(expression),
            Pattern pattern => PrintPattern(pattern),
            Statement statement => PrintStatement(statement),
            _ => throw new InvalidOperationException(node.GetType().Name),
        };

    private static string PrintType(TypeSyntax type) =>
        type switch
        {
            NamedTypeSyntax named => named.TypeArguments.Count == 0 ? named.Name : $"{named.Name}<{string.Join(",", named.TypeArguments.Select(PrintType))}>",
            FunctionTypeSyntax function => $"function({string.Join(",", function.ParameterTypes.Select(PrintType))})->{PrintType(function.ReturnType)}",
            _ => throw new InvalidOperationException(type.GetType().Name),
        };

    private static string PrintExpression(Expression expression) =>
        expression switch
        {
            IntegerLiteral integer => integer.Value.ToString(CultureInfo.InvariantCulture),
            FloatLiteral number => PrintFloat(number.Value),
            BooleanLiteral boolean => boolean.Value ? "true" : "false",
            StringLiteral text => Quote(text.Value),
            InterpolatedString interpolated => $"(interp {string.Join(" ", interpolated.Parts.Select(PrintPart))})",
            PathLiteral path => $"(path {string.Join(" ", path.Parts.Select(PrintPart))})",
            IdentifierExpression identifier => identifier.Name,
            ListLiteral list => List("list", list.Elements.Select(PrintExpression)),
            MapLiteral map => List("map", map.Entries.Select(entry => $"({PrintExpression(entry.Key)} {PrintExpression(entry.Value)})")),
            UnaryExpression unary => $"({(unary.Operator == UnaryOperator.Negate ? "neg" : "not")} {PrintExpression(unary.Operand)})",
            BinaryExpression binary => $"({PrintOperator(binary.Operator)} {PrintExpression(binary.Left)} {PrintExpression(binary.Right)})",
            CallExpression call => List("call", call.Arguments.Select(PrintArgument).Prepend(PrintExpression(call.Callee))),
            MemberAccessExpression member => $"(. {PrintExpression(member.Target)} {member.MemberName})",
            IndexExpression index => $"(idx {PrintExpression(index.Target)} {PrintExpression(index.Index)})",
            PropagateExpression propagate => $"(? {PrintExpression(propagate.Operand)})",
            LambdaExpression lambda => $"(lambda ({string.Join(" ", lambda.Parameters.Select(parameter => parameter.Annotation is null ? parameter.Name : parameter.Name + ":" + PrintType(parameter.Annotation)))}) {PrintExpression(lambda.Body)})",
            OkExpression ok => ok.Value is null ? "(ok)" : $"(ok {PrintExpression(ok.Value)})",
            ErrorExpression error => $"(error {PrintExpression(error.Value)})",
            MatchExpression match => $"({(match.IsStatement ? "match-statement" : "match")} {PrintExpression(match.Scrutinee)} {string.Join(" ", match.Arms.Select(PrintArm))})",
            _ => throw new InvalidOperationException(expression.GetType().Name),
        };

    private static string PrintArgument(CallArgument argument) =>
        argument.Name is null ? PrintExpression(argument.Expression) : $"(= {argument.Name} {PrintExpression(argument.Expression)})";

    private static string PrintArm(MatchArm arm) =>
        $"({PrintPattern(arm.Pattern)} => {(arm.BodyBlock is not null ? PrintStatement(arm.BodyBlock) : PrintExpression(arm.BodyExpression!))})";

    private static string PrintPart(StringPart part) =>
        part switch
        {
            TextPart text => Quote(text.Text),
            InterpolationPart interpolation => "{" + PrintExpression(interpolation.Expression) + "}",
            _ => throw new InvalidOperationException(part.GetType().Name),
        };

    private static string PrintPattern(Pattern pattern) =>
        pattern switch
        {
            WildcardPattern => "_",
            BindingPattern binding => binding.Name,
            LiteralPattern literal => PrintExpression(literal.Literal),
            AlternativePattern alternatives => List("alt", alternatives.Alternatives.Select(PrintPattern)),
            ResultPattern result => $"({(result.IsOk ? "ok" : "error")} {PrintPattern(result.Inner)})",
            _ => throw new InvalidOperationException(pattern.GetType().Name),
        };

    private static string PrintStatement(Statement statement) =>
        statement switch
        {
            BlockStatement block => List("block", block.Statements.Select(PrintStatement)),
            LetStatement let => $"(let{PrintVisibility(let.Visibility)}{(let.IsMutable ? " mutable" : string.Empty)} {let.Name}{(let.Annotation is null ? string.Empty : ":" + PrintType(let.Annotation))} {PrintExpression(let.Initializer)})",
            AssignmentStatement assignment => $"(assign {PrintAssignOperator(assignment.Operator)} {assignment.TargetName} [{string.Join(" ", assignment.Indexes.Select(PrintExpression))}] {PrintExpression(assignment.Value)})",
            IfStatement ifStatement => $"(if {string.Join(" ", ifStatement.Branches.Select(branch => $"({PrintExpression(branch.Condition)} {PrintStatement(branch.Body)})"))}{(ifStatement.ElseBlock is null ? string.Empty : " (else " + PrintStatement(ifStatement.ElseBlock) + ")")})",
            WhileStatement loop => $"(while {PrintExpression(loop.Condition)} {PrintStatement(loop.Body)})",
            ForStatement forLoop => $"(for {forLoop.VariableName} {PrintExpression(forLoop.Iterable)} {PrintStatement(forLoop.Body)})",
            ReturnStatement returnStatement => returnStatement.Value is null ? "(return)" : $"(return {PrintExpression(returnStatement.Value)})",
            BreakStatement => "break",
            ContinueStatement => "continue",
            FunctionDeclaration function => $"(function{PrintVisibility(function.Visibility)} {function.Name} ({string.Join(" ", function.Parameters.Select(parameter => parameter.Name + ":" + PrintType(parameter.Type)))}){(function.ReturnType is null ? string.Empty : " -> " + PrintType(function.ReturnType))} {PrintStatement(function.Body)})",
            ExpressionStatement expression => $"(expr {PrintExpression(expression.Expression)})",
            _ => throw new InvalidOperationException(statement.GetType().Name),
        };

    private static string PrintVisibility(Visibility visibility) =>
        visibility switch { Visibility.Public => " public", Visibility.Private => " private", _ => string.Empty };

    private static string PrintOperator(BinaryOperator binaryOperator) =>
        binaryOperator switch
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
            _ => "or",
        };

    private static string PrintAssignOperator(AssignOperator assignOperator) =>
        assignOperator switch
        {
            AssignOperator.Assign => "=",
            AssignOperator.AddAssign => "+=",
            AssignOperator.SubtractAssign => "-=",
            AssignOperator.MultiplyAssign => "*=",
            AssignOperator.DivideAssign => "/=",
            _ => "%=",
        };

    private static string List(string head, IEnumerable<string> items)
    {
        var parts = items.ToList();
        return parts.Count == 0 ? $"({head})" : $"({head} {string.Join(" ", parts)})";
    }

    private static string PrintFloat(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
