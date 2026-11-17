using Scripts.Core;

namespace Scripts.Syntax.Tests;

public sealed class FuzzTests
{
    private const int Iterations = 2000;
    private const int Seed = 20261013;

    private static readonly string[] Fragments =
    [
        "{", "}", "(", ")", "[", "]", "\"", "\"{", "}\"", "\\", "#", "\n", "\r", "\r\n", ";", ",", ".", "?", ":", "|>", "|", "->", "=>", "-", "--", "&&", "$",
        "let ", "mutable ", "function ", "if ", "else ", "match ", "import ", "public ", "private ", "return", "not ", "and ", "or ", " _ ", "r\"", "p\"",
        "0x", "1e", "1_", "007", "é", "😀", "\u0000", "\t",
    ];

    private static List<byte[]> LoadCorpus()
    {
        var corpus = new List<byte[]>();
        foreach (var path in Directory.GetFiles(Repository.MigrationDirectory, "*.scs"))
        {
            corpus.Add(File.ReadAllBytes(path));
        }

        foreach (var path in Directory.GetFiles(Repository.ConformanceErrorsDirectory, "*.scs"))
        {
            corpus.Add(ConformanceCases.ExpandMarkers(File.ReadAllText(path)));
        }

        foreach (var path in Directory.GetFiles(Repository.ConformanceErrorsDirectory, "*.scs", SearchOption.AllDirectories).Where(path => path.Contains(".files", StringComparison.Ordinal)))
        {
            corpus.Add(File.ReadAllBytes(path));
        }

        return corpus;
    }

    private static byte[] Mutate(byte[] original, Random random)
    {
        var bytes = original.ToList();
        var mutations = random.Next(1, 4);
        for (var mutation = 0; mutation < mutations && bytes.Count > 0; mutation++)
        {
            var position = random.Next(bytes.Count);
            switch (random.Next(7))
            {
                case 0:
                    bytes[position] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    bytes[position] = (byte)random.Next(256);
                    break;
                case 2:
                    bytes.RemoveRange(position, bytes.Count - position);
                    break;
                case 3:
                    bytes.RemoveRange(position, Math.Min(random.Next(1, 8), bytes.Count - position));
                    break;
                case 4:
                    bytes.InsertRange(position, Parsing.Utf8(Fragments[random.Next(Fragments.Length)]));
                    break;
                case 5:
                    bytes.Insert(position, (byte)random.Next(256));
                    break;
                default:
                    var length = Math.Min(random.Next(1, 40), bytes.Count - position);
                    bytes.InsertRange(random.Next(bytes.Count), bytes.GetRange(position, length));
                    break;
            }
        }

        return [.. bytes];
    }

    [Fact]
    public void Mutated_inputs_never_throw_and_yield_a_program_or_exactly_one_catalogued_diagnostic()
    {
        var corpus = LoadCorpus();
        Assert.True(corpus.Count > 100);
        var catalog = ErrorCatalog.All.Select(info => info.Code).ToHashSet(StringComparer.Ordinal);
        var random = new Random(Seed);
        var programs = 0;
        var diagnostics = 0;
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var mutated = Mutate(corpus[random.Next(corpus.Count)], random);
            var isModule = random.Next(2) == 0;
            var reading = SourceReading.Read("fuzz.scs", mutated);
            var outcome = reading.Error is not null
                ? new ParseResult(null, reading.Error)
                : Parser.Parse(reading.Source!, isModule);
            Assert.True(outcome.Program is null ^ outcome.Error is null, $"iteration {iteration}: neither or both");
            if (outcome.Error is { } error)
            {
                diagnostics++;
                Assert.Contains(error.Code, catalog);
                Assert.StartsWith("SCRIPTS-", error.Code, StringComparison.Ordinal);
                Assert.NotNull(error.File);
                Assert.True(error.Start.Line >= 1 && error.Start.Column >= 1, $"iteration {iteration}: {error.Code} at {error.Start}");
                if (reading.Source is not null)
                {
                    Assert.Equal(error.Code, Parser.Parse(reading.Source, isModule).Error?.Code);
                    Assert.Equal(error.Span, Parser.Parse(reading.Source, isModule).Error?.Span);
                }
            }
            else
            {
                programs++;
            }
        }

        Assert.True(programs > 50 && diagnostics > 500, $"programs={programs} diagnostics={diagnostics}");
    }

    [Fact]
    public void The_lexer_never_throws_on_mutated_text()
    {
        var corpus = LoadCorpus();
        var random = new Random(Seed + 1);
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var reading = SourceReading.Read("fuzz.scs", Mutate(corpus[random.Next(corpus.Count)], random));
            if (reading.Source is not null)
            {
                var tokens = Lexing.Tokens(reading.Source.Text);
                var faultyIndex = tokens.FindIndex(token => token.IsFaulty);
                Assert.True(faultyIndex < 0 || faultyIndex == tokens.Count - 1, "a faulty token must be the last one");
                Assert.True(tokens.All(token => token.Start >= 0 && token.End >= token.Start && token.End <= reading.Source.Text.Length));
            }
        }
    }

    [Fact]
    public void Loading_mutated_importers_never_throws()
    {
        var corpus = LoadCorpus();
        var random = new Random(Seed + 2);
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var provider = new InMemorySourceProvider()
                .AddBytes("/main.scs", Mutate(corpus[random.Next(corpus.Count)], random))
                .AddBytes("/lib.scs", Mutate(corpus[random.Next(corpus.Count)], random))
                .AddText("/other.scs", "public let v = 1\n");
            var result = ProgramLoader.Load("/main.scs", provider);
            Assert.True(result.Program is null ^ result.Error is null);
        }
    }

    [Fact]
    public void Random_token_soup_never_throws()
    {
        var random = new Random(Seed + 3);
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var soup = string.Concat(Enumerable.Range(0, random.Next(1, 60)).Select(_ => Fragments[random.Next(Fragments.Length)] + (random.Next(3) == 0 ? " x" : string.Empty)));
            var result = Parser.Parse(new SourceFile("soup.scs", soup), isModule: random.Next(2) == 0);
            Assert.True(result.Program is null ^ result.Error is null);
        }
    }
}
