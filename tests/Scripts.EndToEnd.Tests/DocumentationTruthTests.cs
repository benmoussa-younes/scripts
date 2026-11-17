namespace Scripts.EndToEnd.Tests;

/// <summary>
/// Cross-document truth: the top-level prose and the help text must not deny a shipped feature or cite a document
/// that is no longer part of the tracked tree.
/// </summary>
public sealed class DocumentationTruthTests
{
    [Fact]
    public void No_page_denies_a_shipped_feature_or_a_strict_capture()
    {
        var pages = new List<string>
        {
            RepoFile("README.md"),
        };

        foreach (var page in pages)
        {
            var text = File.ReadAllText(page);
            Assert.DoesNotContain("does not support named", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("silently damaged", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("can never be confused", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("no byte pipes", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("no byte pipelines", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("no pipe between two processes", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("no pipe between processes", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_help_text_cites_no_deleted_document()
    {
        var help = Scripts.Cli.HelpText.Full;
        foreach (var document in new[] { "DESIGN.md", "REFERENCE.md", "ERRORS.md", "ERROR-CODES.md", "MIGRATION.md" })
        {
            Assert.DoesNotContain(document, help, StringComparison.Ordinal);
        }
    }

    private static string RepoFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Scripts.slnx")))
            {
                return Path.Combine(directory.FullName, relativePath);
            }
        }

        throw new InvalidOperationException("repository root not found from " + AppContext.BaseDirectory);
    }
}
