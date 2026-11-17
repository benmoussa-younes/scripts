using Scripts.Core;

namespace Scripts.Syntax;

internal enum TokenKind
{
    EndOfFile,
    NewLine,
    Error,
    Identifier,
    Integer,
    Float,
    String,
    Let,
    Mutable,
    Function,
    Return,
    If,
    Else,
    While,
    For,
    In,
    Match,
    Break,
    Continue,
    Import,
    As,
    Public,
    Private,
    True,
    False,
    And,
    Or,
    Not,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    EqualEqual,
    NotEqual,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
    PipeGreater,
    Equal,
    PlusEqual,
    MinusEqual,
    StarEqual,
    SlashEqual,
    PercentEqual,
    Arrow,
    FatArrow,
    Question,
    Dot,
    Comma,
    Colon,
    Semicolon,
    Pipe,
    LeftParenthesis,
    RightParenthesis,
    LeftBracket,
    RightBracket,
    LeftBrace,
    RightBrace,
}

internal enum StringStyle
{
    Normal,
    Raw,
    Path,
}

internal static class TokenKinds
{
    public static bool IsKeyword(TokenKind kind) => kind >= TokenKind.Let && kind <= TokenKind.Not;
}

internal abstract record StringSegment(int Start, int End);

internal sealed record TextSegment(int Start, int End, string Text) : StringSegment(Start, End);

/// <summary>Tokens of one interpolation, without the surrounding braces; a faulty token can only be the last one.</summary>
internal sealed record InterpolationSegment(int Start, int End, IReadOnlyList<Token> Tokens) : StringSegment(Start, End);

internal class Token
{
    public Token(TokenKind kind, int start, int end, string text = "")
    {
        Kind = kind;
        Start = start;
        End = end;
        Text = text;
    }

    public TokenKind Kind { get; }

    public int Start { get; }

    public int End { get; }

    /// <summary>Name of an identifier or keyword; empty for every other token.</summary>
    public string Text { get; }

    public bool IsPrecededByBlank { get; set; }

    /// <summary>True for a `&gt;` that closes type arguments (a line break after it is significant).</summary>
    public bool ClosesTypeArguments { get; set; }

    /// <summary>True when consuming this token must raise <see cref="Failure"/> (possibly after its own earlier parts).</summary>
    public virtual bool IsFaulty => false;

    public virtual Diagnostic? Failure => null;
}

internal sealed class ErrorToken : Token
{
    private readonly Diagnostic diagnostic;

    public ErrorToken(Diagnostic diagnostic, int start, int end)
        : base(TokenKind.Error, start, end)
    {
        this.diagnostic = diagnostic;
    }

    public override bool IsFaulty => true;

    public override Diagnostic? Failure => diagnostic;
}

internal sealed class IntegerToken : Token
{
    public IntegerToken(int start, int end, long value)
        : base(TokenKind.Integer, start, end)
    {
        Value = value;
    }

    public long Value { get; }
}

internal sealed class FloatToken : Token
{
    public FloatToken(int start, int end, double value)
        : base(TokenKind.Float, start, end)
    {
        Value = value;
    }

    public double Value { get; }
}

internal sealed class StringToken : Token
{
    public StringToken(int start, int end, StringStyle style, IReadOnlyList<StringSegment> segments, Diagnostic? failure)
        : base(TokenKind.String, start, end)
    {
        Style = style;
        Segments = segments;
        Failure = failure;
        IsFaulty = failure is not null || segments.Any(
            segment => segment is InterpolationSegment { Tokens.Count: > 0 } interpolation && interpolation.Tokens[^1].IsFaulty);
    }

    public StringStyle Style { get; }

    public IReadOnlyList<StringSegment> Segments { get; }

    public override bool IsFaulty { get; }

    public override Diagnostic? Failure { get; }

    public bool HasInterpolation => Segments.Any(segment => segment is InterpolationSegment);
}
