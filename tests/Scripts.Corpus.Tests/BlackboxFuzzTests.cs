using System.Text;
using System.Text.RegularExpressions;

namespace Scripts.Corpus.Tests;

/// <summary>
/// xUnit port of the black-box fuzz axis: seeded mutants of valid scripts
/// (truncations, byte flips, insertions, block deletions, line duplications,
/// concatenations, random binary bytes, line repetitions) must never crash with
/// 134/136/137/139/254 and must never print a stack trace. A slow mutant is
/// tolerated, as in the shell oracle.
/// </summary>
[Collection("corpus")]
public sealed class BlackboxFuzzTests
{
    private const int MutationCount = 4000;
    private const int MutantTimeoutMilliseconds = 20_000;

    private static readonly int[] CrashCodes = [134, 136, 137, 139, 254];

    private static readonly Regex TracePattern = new(
        "Unhandled exception|at Scripts\\.|NullReference|Stack overflow|System\\.[A-Za-z]+Exception|scripts internal error",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] KindNames =
    [
        "truncation", "byte_flip", "insertion", "block_deletion",
        "line_duplication", "concatenation", "random_binary", "line_repetition",
    ];

    private static readonly Lazy<IReadOnlyList<Mutant>> Generated = new(
        Generate,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed record Mutant(int Index, int Kind, byte[] Data);

    public static TheoryData<int, string> Kinds()
    {
        var data = new TheoryData<int, string>();
        for (var kind = 0; kind < KindNames.Length; kind++)
        {
            data.Add(kind, KindNames[kind]);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void MutantKind(int kind, string name)
    {
        using var work = new BlackboxWorkspace("scripts-blackbox-fuzz-");
        var run = 0;
        var crashes = 0;
        var traces = 0;
        var slow = 0;
        var failures = new List<string>();
        foreach (var mutant in Generated.Value)
        {
            if (mutant.Kind != kind)
            {
                continue;
            }

            run++;
            var path = Path.Combine(work.Path, $"m{mutant.Index:00000}.scs");
            File.WriteAllBytes(path, mutant.Data);
            var result = BlackboxRunner.Run(
                BlackboxRunner.Binary,
                ["run", path],
                work.Path,
                MutantTimeoutMilliseconds);
            if (TracePattern.IsMatch(result.CombinedOutput))
            {
                traces++;
                if (failures.Count < 10)
                {
                    failures.Add($"TRACE on {path} (exit {result.ExitCode})\n{Head(result.CombinedOutput)}");
                }
            }

            if (result.TimedOut)
            {
                slow++;
                continue;
            }

            if (CrashCodes.Contains(result.ExitCode))
            {
                crashes++;
                if (failures.Count < 10)
                {
                    failures.Add($"UNEXPECTED CODE exit={result.ExitCode} on {path}");
                }
            }
        }

        Assert.True(run > 0, $"{name}: no mutant generated");
        Assert.True(
            crashes == 0 && traces == 0,
            $"{name} : run={run} unexpected_codes={crashes} traces={traces} timeouts={slow}\n{string.Join('\n', failures)}");
    }

    [Fact(DisplayName = "mutation_count")]
    public void MutationCountIsPreserved()
    {
        Assert.Equal(MutationCount, Generated.Value.Count);
    }

    private static IReadOnlyList<Mutant> Generate()
    {
        var seeds = Seeds();
        Assert.True(seeds.Count > 0, "fuzz: no seed");
        var random = new Random(20261014);
        var mutants = new List<Mutant>(MutationCount);
        for (var index = 0; index < MutationCount; index++)
        {
            var data = (byte[])seeds[random.Next(seeds.Count)].Clone();
            if (data.Length == 0)
            {
                continue;
            }

            var kind = random.Next(KindNames.Length);
            switch (kind)
            {
                case 0:
                    data = data[..random.Next(1, data.Length + 1)];
                    break;
                case 1:
                    for (var mutation = 0; mutation < random.Next(1, 6); mutation++)
                    {
                        data[random.Next(data.Length)] = (byte)random.Next(256);
                    }

                    break;
                case 2:
                    for (var mutation = 0; mutation < random.Next(1, 6); mutation++)
                    {
                        var position = random.Next(data.Length + 1);
                        var inserted = InsertionBytes[random.Next(InsertionBytes.Length)];
                        var extended = new byte[data.Length + 1];
                        data.AsSpan(0, position).CopyTo(extended);
                        extended[position] = inserted;
                        data.AsSpan(position).CopyTo(extended.AsSpan(position + 1));
                        data = extended;
                    }

                    break;
                case 3:
                    var start = random.Next(data.Length);
                    var end = Math.Min(data.Length, start + random.Next(1, 40));
                    data = [.. data.AsSpan(0, start), .. data.AsSpan(end)];
                    break;
                case 4:
                    var lines = SplitLines(data);
                    var line = random.Next(lines.Count);
                    lines.Insert(line, lines[line]);
                    data = JoinLines(lines);
                    break;
                case 5:
                    data = [.. data, .. seeds[random.Next(seeds.Count)]];
                    break;
                case 6:
                    data = new byte[random.Next(1, 400)];
                    random.NextBytes(data);
                    break;
                default:
                    var firstLine = FirstLine(data);
                    var repetitions = random.Next(1, 60);
                    var repeated = new byte[(firstLine.Length + 1) * repetitions];
                    for (var repetition = 0; repetition < repetitions; repetition++)
                    {
                        firstLine.CopyTo(repeated, repetition * (firstLine.Length + 1));
                        repeated[(repetition * (firstLine.Length + 1)) + firstLine.Length] = (byte)'\n';
                    }

                    data = repeated;
                    break;
            }

            mutants.Add(new Mutant(index, kind, data));
        }

        return mutants;
    }

    private static List<byte[]> Seeds()
    {
        var seeds = new List<byte[]>();
        var tests = Path.Combine(CorpusPaths.RepositoryRoot, "tests");
        if (Directory.Exists(tests))
        {
            foreach (var path in Directory.EnumerateFiles(tests, "*.scs", SearchOption.AllDirectories)
                         .Order(StringComparer.Ordinal)
                         .Take(400))
            {
                seeds.Add(File.ReadAllBytes(path));
            }
        }

        foreach (var source in BuiltInSeeds)
        {
            seeds.Add(Encoding.UTF8.GetBytes(source));
        }

        return seeds;
    }

    private static readonly string[] BuiltInSeeds =
    [
        """
        let mutable total = 0
        for item in [1, 2, 3, 4, 5] {
            total += item
        }
        io.write_standard_output_line("total={total}")
        """,
        """
        function fact(n: integer) -> integer {
            if n <= 1 {
                return 1
            }
            return n * fact(n - 1)
        }
        io.write_standard_output_line("{fact(10)}")
        """,
        """
        let xs = [3, 1, 2]
        let ys = xs.sorted()
        io.write_standard_output_line("{ys.join(",")}")
        """,
        """
        let m = {"a": 1, "b": 2}
        for k in m.keys() {
            io.write_standard_output_line("{k}={m[k]}")
        }
        """,
        """
        let r = "42".parse_integer()
        match r {
            ok(n) => io.write_standard_output_line("ok {n + 1}")
            error(e) => io.write_standard_output_line("err {e.kind}")
        }
        """,
    ];

    private static readonly byte[] InsertionBytes = Encoding.ASCII.GetBytes("{}()[]<>+-*/%|.,:;=\"'\n\t ");

    private static List<byte[]> SplitLines(byte[] data)
    {
        var lines = new List<byte[]>();
        var start = 0;
        for (var index = 0; index < data.Length; index++)
        {
            if (data[index] != (byte)'\n')
            {
                continue;
            }

            lines.Add(data[start..index]);
            start = index + 1;
        }

        lines.Add(data[start..]);
        return lines;
    }

    private static byte[] JoinLines(IReadOnlyList<byte[]> lines)
    {
        var length = lines.Count - 1;
        foreach (var line in lines)
        {
            length += line.Length;
        }

        var joined = new byte[length];
        var offset = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            lines[index].CopyTo(joined, offset);
            offset += lines[index].Length;
            if (index < lines.Count - 1)
            {
                joined[offset] = (byte)'\n';
                offset++;
            }
        }

        return joined;
    }

    private static byte[] FirstLine(byte[] data)
    {
        var end = Array.IndexOf(data, (byte)'\n');
        return end < 0 ? data : data[..end];
    }

    private static string Head(string text)
    {
        var lines = text.Split('\n');
        return string.Join('\n', lines.Take(3));
    }
}
