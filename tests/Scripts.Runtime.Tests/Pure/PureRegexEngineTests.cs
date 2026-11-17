using static Scripts.Runtime.Tests.Pure.PureTestSupport;

namespace Scripts.Runtime.Tests.Pure;

public class PureRegexEngineTests
{
    private static bool Find(string text, string pattern)
    {
        Assert.True(RegexEngine.TryFind(text, pattern, out var found, out var failureMessage), failureMessage);
        Assert.Null(failureMessage);
        return found;
    }

    private static string Reject(string pattern)
    {
        Assert.False(RegexEngine.TryFind("abc", pattern, out var found, out var failureMessage));
        Assert.False(found);
        Assert.False(string.IsNullOrEmpty(failureMessage));
        return failureMessage!;
    }

    [Fact]
    public void MatchesAnywhereWithoutImplicitAnchoring()
    {
        Assert.True(Find("xxabcxx", "abc"));
        Assert.False(Find("xxabcxx", "^abc"));
        Assert.True(Find("abc", "^abc$"));
        Assert.True(Find("abc", string.Empty));
        Assert.True(Find(string.Empty, string.Empty));
        Assert.False(Find(string.Empty, "a"));
    }

    [Fact]
    public void WorksOnUtf16UnitsNotScalars()
    {
        Assert.False(Find("\U0001F600", "^.$"));
        Assert.True(Find("\U0001F600", "^..$"));
        Assert.True(Find("\U0001F600", "\U0001F600"));
    }

    [Fact]
    public void UsesUnicodeClassesAndDotNetDollarSemantics()
    {
        Assert.True(Find("\uFF11\uFF12", "^\\d+$"));
        Assert.True(Find("é", "^\\w$"));
        Assert.True(Find("a\n", "^a$"));
        Assert.False(Find("a\nb", "^a$"));
        Assert.True(Find("\u00E9", "\\p{L}"));
    }

    [Fact]
    public void IsCaseSensitiveUnlessInlineOptionIsGiven()
    {
        Assert.False(Find("ABC", "abc"));
        Assert.True(Find("ABC", "(?i)abc"));
    }

    [Theory]
    [InlineData("(a)\\1")]
    [InlineData("(?=a)")]
    [InlineData("(?!a)")]
    [InlineData("(?<=a)b")]
    [InlineData("(?<!a)b")]
    [InlineData("(?>a)")]
    [InlineData("(?<n>a)\\k<n>")]
    [InlineData("(?(a)b|c)")]
    [InlineData("((a{100}){100}){100}")]
    public void RejectsUnsupportedConstructs(string pattern) => Reject(pattern);

    [Theory]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("[")]
    [InlineData("*")]
    [InlineData("a{2,1}")]
    [InlineData("\\")]
    [InlineData("[z-a]")]
    [InlineData("(?<")]
    public void RejectsSyntaxErrors(string pattern) => Reject(pattern);

    [Theory]
    [InlineData("[[:alpha:]]")]
    [InlineData("[[:digit:]]+")]
    [InlineData("[^[:space:]]")]
    [InlineData("[[:alpha:]-z]")]
    [InlineData("[a[:alpha:]b]")]
    [InlineData("[]x[:alpha:]]")]
    [InlineData("[^]x[:alpha:]]")]
    [InlineData("[[:alpha:][:digit:]]")]
    [InlineData("x[[:upper:]]y")]
    [InlineData("[a-z-[:alpha:]]")]
    public void RejectsPosixClassesBeforeCompilation(string pattern) =>
        Assert.Equal("POSIX classes [:…:] are not supported", Reject(pattern));

    [Theory]
    [InlineData("[:alpha:]", "a")]
    [InlineData("[:]", ":")]
    [InlineData("[[:]", "[")]
    [InlineData("\\[:alpha:\\]", "[:alpha:]")]
    [InlineData("[\\[:alpha:\\]]", "]")]
    [InlineData("[[]:alpha:]", "[:alpha:]")]
    [InlineData("[a-z]:[0-9]", "a:1")]
    public void AcceptsLookAlikesThatAreNotPosixClasses(string pattern, string text)
    {
        Assert.True(RegexEngine.TryFind(text, pattern, out var found, out _));
        Assert.True(found);
    }

    [Fact]
    public void StaysLinearOnCatastrophicPatterns()
    {
        var input = new string('a', 100_000) + "!";
        Assert.False(Find(input, "^(a+)+$"));
        Assert.False(Find(input, "^(a|aa)+$"));
    }

    [Fact]
    public void RepeatedUseOfTheSamePatternIsConsistent()
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            Assert.True(Find("abc" + attempt, "c\\d+$"));
            Assert.False(Find("abc" + attempt, "^c"));
        }
    }
}
