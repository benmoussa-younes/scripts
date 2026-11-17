using System.Text;

namespace Scripts.Syntax;

/// <summary>Suggests an ASCII spelling for an identifier that contains non-ASCII letters.</summary>
internal static class AsciiNames
{
    private static readonly (string Accented, char Plain)[] FoldingTable =
    [
        ("àáâãäåāăą", 'a'),
        ("ÀÁÂÃÄÅĀĂĄ", 'A'),
        ("çćĉċč", 'c'),
        ("ÇĆĈĊČ", 'C'),
        ("èéêëēĕėęě", 'e'),
        ("ÈÉÊËĒĔĖĘĚ", 'E'),
        ("ìíîïĩīĭįı", 'i'),
        ("ÌÍÎÏĨĪĬĮİ", 'I'),
        ("ñńņň", 'n'),
        ("ÑŃŅŇ", 'N'),
        ("òóôõöøōŏő", 'o'),
        ("ÒÓÔÕÖØŌŎŐ", 'O'),
        ("ùúûüũūŭůűų", 'u'),
        ("ÙÚÛÜŨŪŬŮŰŲ", 'U'),
        ("ýÿ", 'y'),
        ("ÝŸ", 'Y'),
    ];

    /// <summary>Returns the word around <paramref name="offset"/> with each non-ASCII letter replaced by its plain letter (or `_`).</summary>
    public static string Suggest(string text, int offset)
    {
        var start = offset;
        while (start > 0 && IsWordCharacter(text[start - 1]))
        {
            start--;
        }

        var end = offset;
        while (end < text.Length && IsWordCharacter(text[end]))
        {
            end++;
        }

        var folded = new StringBuilder();
        for (var index = start; index < end; index++)
        {
            folded.Append(Fold(text[index]));
            if (char.IsHighSurrogate(text[index]) && index + 1 < end && char.IsLowSurrogate(text[index + 1]))
            {
                index++;
            }
        }

        return folded.ToString();
    }

    private static bool IsWordCharacter(char character) => character == '_' || char.IsLetterOrDigit(character) || char.IsSurrogate(character);

    private static char Fold(char character)
    {
        if (character < 0x80)
        {
            return character;
        }

        foreach (var (accented, plain) in FoldingTable)
        {
            if (accented.Contains(character, StringComparison.Ordinal))
            {
                return plain;
            }
        }

        return '_';
    }
}
