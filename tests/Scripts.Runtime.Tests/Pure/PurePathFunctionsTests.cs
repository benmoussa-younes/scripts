using Scripts.Runtime.Platform;
using static Scripts.Runtime.Tests.Pure.PureTestSupport;

namespace Scripts.Runtime.Tests.Pure;

public class PurePathFunctionsTests
{
    [Theory]
    [InlineData("/tmp", "a.txt", "/tmp/a.txt")]
    [InlineData("/tmp/", "a.txt", "/tmp/a.txt")]
    [InlineData("", "b", "b")]
    [InlineData("a//", "b", "a//b")]
    [InlineData(".", "..", "./..")]
    [InlineData("/", "x", "/x")]
    public void DivisionJoinsPathComponents(string basePath, string child, string expected)
    {
        Assert.Equal(expected, ((PathValue)ArithmeticOperations.Divide(Path(basePath), Text(child))).Text);
        Assert.Equal(expected, ((PathValue)ArithmeticOperations.Divide(Path(basePath), Path(child))).Text);
    }

    [Theory]
    [InlineData("", "x", "x")]
    [InlineData("", "", "")]
    [InlineData("a", "", "a")]
    [InlineData("a", "/b", "/b")]
    public void InternalPathTextJoinKeepsTheEmptyWorkingDirectorySentinelUnchanged(string basePath, string child, string expected) =>
        Assert.Equal(expected, PathText.Join(basePath, child));

    [Theory]
    [InlineData("/tmp/a.txt", "a.txt")]
    [InlineData("/tmp/dir/", "dir")]
    [InlineData("a//", "a")]
    [InlineData(".", ".")]
    [InlineData("..", "..")]
    [InlineData("/", "/")]
    [InlineData("//", "/")]
    [InlineData("", "")]
    [InlineData("a", "a")]
    [InlineData("a/b/c", "c")]
    [InlineData("/a", "a")]
    public void BaseNameReturnsLastComponent(string path, string expected) =>
        Assert.Equal(expected, ((StringValue)Call("path.base_name", Path(path))).Text);

    [Theory]
    [InlineData("a.tar.gz", "gz")]
    [InlineData("a.txt", "txt")]
    [InlineData(".hidden.txt", "txt")]
    [InlineData(".bashrc", "")]
    [InlineData("a.", "")]
    [InlineData(".", "")]
    [InlineData("..", "")]
    [InlineData("/", "")]
    [InlineData("", "")]
    [InlineData("/x.d/file", "")]
    [InlineData("noext", "")]
    [InlineData("/tmp/a.txt/", "txt")]
    [InlineData("a..b", "b")]
    public void ExtensionUsesLastDotOfLastComponent(string path, string expected) =>
        Assert.Equal(expected, ((StringValue)Call("path.extension", Path(path))).Text);

    [Theory]
    [InlineData("/", "/")]
    [InlineData("a", ".")]
    [InlineData("a/b", "a")]
    [InlineData("/a", "/")]
    [InlineData("/a/b", "/a")]
    [InlineData("a/b/", "a")]
    [InlineData("a//b", "a")]
    [InlineData(".", ".")]
    [InlineData("..", ".")]
    [InlineData("", ".")]
    [InlineData("//", "/")]
    [InlineData("//a", "/")]
    [InlineData("a/b/c", "a/b")]
    public void ParentRemovesLastComponent(string path, string expected) =>
        Assert.Equal(expected, ((PathValue)Call("path.parent", Path(path))).Text);

    [Theory]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("é/\U0001F600")]
    public void ToTextReturnsExactText(string path) =>
        Assert.Equal(path, ((StringValue)Call("path.to_string", Path(path))).Text);

    [Theory]
    [InlineData("/a", true)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    [InlineData("/", true)]
    [InlineData("./a", false)]
    public void IsAbsoluteChecksLeadingSlash(string path, bool expected) =>
        Assert.Equal(Boolean(expected), Call("path.is_absolute", Path(path)));
}
