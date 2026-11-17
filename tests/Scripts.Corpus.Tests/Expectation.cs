namespace Scripts.Corpus.Tests;

/// <summary>The directives of a corpus <c>.expect</c> file.</summary>
internal sealed record Expectation(int? ExitCode, string? StandardOutput, string? StandardError, string? Entry)
{
    public static Expectation Read(string path)
    {
        var lines = File.ReadAllLines(path);
        int? exitCode = null;
        string? entry = null;
        string? standardOutput = null;
        string? standardError = null;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.StartsWith("stdout:", StringComparison.Ordinal))
            {
                standardOutput = Block(lines, ref index, "stderr:", "exit:", "entry:");
            }
            else if (line.StartsWith("stderr:", StringComparison.Ordinal))
            {
                standardError = Block(lines, ref index, "stdout:", "exit:", "entry:");
            }
            else if (line.StartsWith("exit:", StringComparison.Ordinal))
            {
                exitCode = int.Parse(line["exit:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("entry:", StringComparison.Ordinal))
            {
                entry = line["entry:".Length..].Trim();
            }
        }

        return new Expectation(exitCode, standardOutput, standardError, entry);
    }

    private static string Block(string[] lines, ref int index, params string[] terminators)
    {
        var content = new List<string>();
        var cursor = index + 1;
        while (cursor < lines.Length && !terminators.Any(terminator => lines[cursor].StartsWith(terminator, StringComparison.Ordinal)))
        {
            content.Add(lines[cursor]);
            cursor++;
        }

        index = cursor - 1;
        return content.Count == 0 ? string.Empty : string.Join('\n', content) + "\n";
    }
}
