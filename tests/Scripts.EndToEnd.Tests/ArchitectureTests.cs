using System.Xml.Linq;

namespace Scripts.EndToEnd.Tests;

/// <summary>
/// ARCHITECTURE.md, "Code map": the project references form a star, not a chain. Core references
/// nothing; Syntax, Checker, Cli and Runtime reference Core (Runtime also the four core
/// projects); App is the composition root that references all five. The reference sets are read from
/// the production .csproj files so an upward reference fails the build's test gate.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly IReadOnlyDictionary<string, string[]> ExpectedReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Scripts.Core"] = [],
            ["Scripts.Syntax"] = ["Scripts.Core"],
            ["Scripts.Checker"] = ["Scripts.Core"],
            ["Scripts.Cli"] = ["Scripts.Core"],
            ["Scripts.Runtime"] =
            [
                "Scripts.Core",
            ],
            ["Scripts.App"] =
            [
                "Scripts.Core",
                "Scripts.Syntax",
                "Scripts.Checker",
                "Scripts.Runtime",
                "Scripts.Cli",
            ],
        };

    [Fact]
    public void Production_projects_reference_exactly_the_star_graph()
    {
        var productionRoot = Path.Combine(FindRepositoryRoot(), "src");
        var projectFiles = Directory
            .EnumerateFiles(productionRoot, "*.csproj", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ExpectedReferences.Keys.OrderBy(name => name, StringComparer.Ordinal),
            projectFiles
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(name => name, StringComparer.Ordinal));

        foreach (var projectFile in projectFiles)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectFile);
            var actual = ReadProjectReferences(projectFile);
            var expected = ExpectedReferences[projectName].ToHashSet(StringComparer.Ordinal);
            var mismatch = CompareReferences(expected, actual);

            Assert.True(
                mismatch.Missing.Count == 0 && mismatch.Unexpected.Count == 0,
                DescribeMismatch(projectName, projectFile, mismatch));
        }
    }

    [Fact]
    public void The_comparison_reports_a_forbidden_reference_with_its_project()
    {
        var expected = new HashSet<string>(["Scripts.Core"], StringComparer.Ordinal);
        var actual = ParseProjectReferences(
            XDocument.Parse(
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <ProjectReference Include="..\Scripts.Syntax\Scripts.Syntax.csproj" />
                    <ProjectReference Include="..\Scripts.Core\Scripts.Core.csproj" />
                  </ItemGroup>
                </Project>
                """));

        var mismatch = CompareReferences(expected, actual);
        var message = DescribeMismatch(
            "Scripts.Runtime",
            "/repo/src/Scripts.Runtime/Scripts.Runtime.csproj",
            mismatch);

        Assert.Equal(["Scripts.Syntax"], mismatch.Unexpected);
        Assert.Empty(mismatch.Missing);
        Assert.Contains("Scripts.Runtime", message);
        Assert.Contains("Scripts.Syntax", message);
    }

    [Fact]
    public void The_comparison_reports_a_missing_reference_with_its_project()
    {
        var expected = new HashSet<string>(
            ["Scripts.Core", "Scripts.Runtime"],
            StringComparer.Ordinal);
        var actual = new HashSet<string>(["Scripts.Core"], StringComparer.Ordinal);

        var mismatch = CompareReferences(expected, actual);
        var message = DescribeMismatch(
            "Scripts.App",
            "/repo/src/Scripts.App/Scripts.App.csproj",
            mismatch);

        Assert.Empty(mismatch.Unexpected);
        Assert.Equal(["Scripts.Runtime"], mismatch.Missing);
        Assert.Contains("Scripts.App", message);
        Assert.Contains("Scripts.Runtime", message);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Scripts.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }

    private static HashSet<string> ReadProjectReferences(string projectFile) =>
        ParseProjectReferences(XDocument.Load(projectFile));

    private static HashSet<string> ParseProjectReferences(XDocument project) =>
        project
            .Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    private static ReferenceMismatch CompareReferences(
        IReadOnlySet<string> expected,
        IReadOnlySet<string> actual) =>
        new(
            expected.Except(actual).OrderBy(name => name, StringComparer.Ordinal).ToList(),
            actual.Except(expected).OrderBy(name => name, StringComparer.Ordinal).ToList());

    private static string DescribeMismatch(string projectName, string projectFile, ReferenceMismatch mismatch)
    {
        var message = new List<string>
        {
            $"{projectName} ({projectFile}) has the wrong ProjectReference set.",
        };
        if (mismatch.Missing.Count > 0)
        {
            message.Add("missing: " + string.Join(", ", mismatch.Missing));
        }

        if (mismatch.Unexpected.Count > 0)
        {
            message.Add("unexpected: " + string.Join(", ", mismatch.Unexpected));
        }

        return string.Join(Environment.NewLine, message);
    }

    private sealed record ReferenceMismatch(IReadOnlyList<string> Missing, IReadOnlyList<string> Unexpected);
}
