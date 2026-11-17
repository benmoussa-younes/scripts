using Scripts.Core;

namespace Scripts.Syntax;

public sealed record ParseResult(ProgramNode? Program, Diagnostic? Error);

public static class Parser
{
    /// <summary>Parses one source file; <paramref name="isModule"/> enables the module-only rules (visibility required on declarations).</summary>
    public static ParseResult Parse(SourceFile source, bool isModule)
    {
        var parser = new SyntaxParser(source, isModule);
        if (!parser.TryParseImports(out var imports, out var error))
        {
            return new ParseResult(null, error);
        }

        return parser.TryParseBody(imports, out var program, out error)
            ? new ParseResult(program, null)
            : new ParseResult(null, error);
    }
}
