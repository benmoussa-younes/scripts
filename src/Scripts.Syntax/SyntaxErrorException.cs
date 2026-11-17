using Scripts.Core;

namespace Scripts.Syntax;

/// <summary>Carries the first diagnostic out of the recursive-descent parser; it never crosses the public API.</summary>
internal sealed class SyntaxErrorException : Exception
{
    public SyntaxErrorException(Diagnostic diagnostic)
        : base(diagnostic.Message)
    {
        Diagnostic = diagnostic;
    }

    public Diagnostic Diagnostic { get; }
}
