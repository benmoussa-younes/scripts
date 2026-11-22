using System.Diagnostics;

namespace Scripts.EndToEnd.Tests;

/// <summary>The packaging-metadata checker accepts one version and names every mismatch.</summary>
public sealed class PackagingMetadataEndToEndTests
{
    private const string Changelog =
        "scripts (0.2.0) unstable; urgency=medium\n\n  * A change.\n\n -- Test <test@example.com>  Sat, 21 Nov 2026 00:00:00 +0000\n";

    private const string Control = "Package: scripts\nVersion: 0.2.0\nArchitecture: amd64\n";
    private const string ChangelogMarkdown = "# Changelog\n\n## [0.2.0] - 2026-11-21\n\n- A change.\n";
    private const string Readme =
        "Scripts 0.2.0 is the current release.\n\nsudo apt install ./scripts_0.2.0_amd64.deb\ntar -xzf scripts_0.2.0_linux-x64.tar.gz\nsudo install -m 755 scripts-0.2.0/scripts /usr/local/bin/scripts\ngit verify-tag v0.2.0\ngh attestation verify scripts_0.2.0_amd64.deb --repo example/scripts\n";

    private const string ReleaseWorkflow =
        "jobs:\n  build:\n    steps:\n      - name: Verify\n        run: |\n          gpg --batch --import packaging/maintainer-key.asc\n          bash packaging/verify-release.sh --require-tag\n      - name: Attest\n        uses: actions/attest-build-provenance@4d101475d8b2 # v4\n";

    private const string ReleaseChecker = "usage: verify-release.sh [--write] [--require-tag] [--directory DIR]\n";

    [Fact]
    public void The_packaging_checker_accepts_one_version_and_names_each_mismatch()
    {
        var root = ScriptsProcess.NewTemporaryDirectory();
        try
        {
            var checker = CopyChecker(root);
            WriteMetadata(root, Changelog, Control, ChangelogMarkdown, Readme);
            Assert.Equal(0, Run(checker).ExitCode);

            var mismatches = new (string Changelog, string Control, string Markdown, string Readme, string Expected)[]
            {
                (
                    Changelog,
                    "Package: scripts\nVersion: 0.1.2\nArchitecture: amd64\n",
                    ChangelogMarkdown,
                    Readme,
                    "packaging/debian/control: Version '0.1.2' != changelog '0.2.0'"
                ),
                (Changelog, Control, "# Changelog\n\n## [0.1.2] - 2026-11-21\n", Readme, "CHANGELOG.md: top release '0.1.2' != changelog '0.2.0'"),
                (Changelog, Control, ChangelogMarkdown, "sudo apt install ./scripts_0.1.2_amd64.deb\n", "README.md: no 'sudo apt install ./scripts_0.2.0_amd64.deb'"),
                (
                    Changelog.Replace("  * A change.", "  * " + new string('x', 90), StringComparison.Ordinal),
                    Control,
                    ChangelogMarkdown,
                    Readme,
                    "packaging/debian/changelog:3: 94 characters"
                ),
                (
                    Changelog,
                    "Package: scripts\nVersion: 0.2.0\nVersion: 0.2.0\nArchitecture: amd64\n",
                    ChangelogMarkdown,
                    Readme,
                    "packaging/debian/control: 2 Version lines, expected exactly one"
                ),
                (Changelog, Control, ChangelogMarkdown, Readme.Replace("is the current release", "is the previous release", StringComparison.Ordinal), "README.md: no 'Scripts 0.2.0 is the current release'"),
                (
                    Changelog,
                    Control,
                    ChangelogMarkdown,
                    Readme.Replace("gh attestation verify", "sha256sum", StringComparison.Ordinal),
                    "README.md: no 'gh attestation verify' command"
                ),
                (
                    Changelog,
                    Control,
                    ChangelogMarkdown,
                    Readme.Replace("git verify-tag", "git tag", StringComparison.Ordinal),
                    "README.md: no 'git verify-tag' command"
                ),
            };

            foreach (var mismatch in mismatches)
            {
                WriteMetadata(root, mismatch.Changelog, mismatch.Control, mismatch.Markdown, mismatch.Readme);
                var result = Run(checker);
                Assert.Equal(1, result.ExitCode);
                Assert.Contains(mismatch.Expected, result.StandardError, StringComparison.Ordinal);
                Assert.Contains("packaging: FAIL", result.StandardError, StringComparison.Ordinal);
            }

            var workflowMismatches = new (string Workflow, string Expected)[]
            {
                (
                    ReleaseWorkflow.Replace(
                        "bash packaging/verify-release.sh --require-tag",
                        "bash packaging/verify-release.sh --write",
                        StringComparison.Ordinal),
                    ".github/workflows/release.yml: has no 'bash packaging/verify-release.sh --require-tag' line"
                ),
                (
                    ReleaseWorkflow.Replace("maintainer-key.asc", "maintainer-key.pgp", StringComparison.Ordinal),
                    ".github/workflows/release.yml: has no 'gpg --batch --import packaging/maintainer-key.asc' line"
                ),
                (
                    ReleaseWorkflow.Replace(
                        "      - name: Attest\n        uses: actions/attest-build-provenance@4d101475d8b2 # v4\n",
                        "",
                        StringComparison.Ordinal),
                    ".github/workflows/release.yml: has no 'uses: actions/attest-build-provenance@' line"
                ),
                (
                    ReleaseWorkflow + "        run: cp packaging/SHA256SUMS.asc artifacts/\n",
                    ".github/workflows/release.yml: references SHA256SUMS.asc, but no release publishes a signature"
                ),
                (
                    ReleaseWorkflow + "        run: cp packaging/tarballs.sha256.asc artifacts/\n",
                    ".github/workflows/release.yml: references tarballs.sha256.asc, but no release publishes a signature"
                ),
                (
                    ReleaseWorkflow + "        run: cp packaging/*.asc artifacts/\n",
                    ".github/workflows/release.yml: references packaging/*.asc, but no release publishes a signature"
                ),
            };

            foreach (var workflowMismatch in workflowMismatches)
            {
                WriteMetadata(root, Changelog, Control, ChangelogMarkdown, Readme, workflowMismatch.Workflow);
                var result = Run(checker);
                Assert.Equal(1, result.ExitCode);
                Assert.Contains(workflowMismatch.Expected, result.StandardError, StringComparison.Ordinal);
                Assert.Contains("packaging: FAIL", result.StandardError, StringComparison.Ordinal);
            }

            WriteMetadata(
                root,
                Changelog,
                Control,
                ChangelogMarkdown,
                Readme,
                checker: "test -f \"${release_directory}/SHA256SUMS.asc\"\n");
            var checkerResult = Run(checker);
            Assert.Equal(1, checkerResult.ExitCode);
            Assert.Contains(
                "packaging/verify-release.sh: references SHA256SUMS.asc, but no release publishes a signature",
                checkerResult.StandardError,
                StringComparison.Ordinal);

            WriteMetadata(root, Changelog, Control, ChangelogMarkdown, Readme, ReleaseWorkflow + "# SHA256SUMS.asc\n");
            Assert.Equal(0, Run(checker).ExitCode);

            WriteMetadata(
                root,
                Changelog,
                Control,
                "# Changelog\n\n## [Unreleased]\n\n- Pending.\n\n## [0.2.0] - 2026-11-21\n\n- A change.\n",
                Readme);
            Assert.Equal(0, Run(checker).ExitCode);

            WriteMetadata(root, Changelog, Control, ChangelogMarkdown, Readme);
            RunGit(root, "init", "-q", "-b", "main");
            RunGit(root, "add", ".");
            RunGit(root, "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "fixture");
            RunGit(root, "-c", "tag.gpgsign=false", "tag", "v0.1.2");
            var tagged = Run(checker);
            Assert.Equal(1, tagged.ExitCode);
            Assert.Contains("git: HEAD is tagged 'v0.1.2', the metadata names '0.2.0'", tagged.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ScriptsProcessResult Run(string checker) => ScriptsProcess.Run("run", checker);

    private static void RunGit(string directory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {standardError}");
    }

    private static string CopyChecker(string root)
    {
        var binaries = Path.Combine(root, "tools", "scripts", "binaries");
        var modules = Path.Combine(root, "tools", "scripts", "modules");
        Directory.CreateDirectory(binaries);
        Directory.CreateDirectory(modules);
        var repository = RepositoryRoot();
        File.Copy(
            Path.Combine(repository, "tools", "scripts", "binaries", "check-packaging.scs"),
            Path.Combine(binaries, "check-packaging.scs"));
        File.Copy(
            Path.Combine(repository, "tools", "scripts", "modules", "versions.scs"),
            Path.Combine(modules, "versions.scs"));
        return Path.Combine(binaries, "check-packaging.scs");
    }

    private static void WriteMetadata(string root, string changelog, string control, string markdown, string readme, string? workflow = null, string? checker = null)
    {
        var debian = Path.Combine(root, "packaging", "debian");
        Directory.CreateDirectory(debian);
        File.WriteAllText(Path.Combine(debian, "changelog"), changelog);
        File.WriteAllText(Path.Combine(debian, "control"), control);
        File.WriteAllText(Path.Combine(root, "CHANGELOG.md"), markdown);
        File.WriteAllText(Path.Combine(root, "README.md"), readme);
        File.WriteAllText(Path.Combine(root, "packaging", "verify-release.sh"), checker ?? ReleaseChecker);
        var workflows = Path.Combine(root, ".github", "workflows");
        Directory.CreateDirectory(workflows);
        File.WriteAllText(Path.Combine(workflows, "release.yml"), workflow ?? ReleaseWorkflow);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Scripts.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("the repository root (Scripts.slnx) was not found above the test directory");
    }
}
