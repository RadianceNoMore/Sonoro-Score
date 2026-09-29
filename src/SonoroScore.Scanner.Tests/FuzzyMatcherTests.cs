// Ac 2026 RadianceNoMore (Sonoro-Score, MIT).
// B-01 regression pins for the strict fuzzy matcher (F-01): exact wins,
// containment never scores 1.0, short queries never match, results do not depend
// on catalog order, and substring-collision names resolve to themselves.

using SonoroScore.Scanner;
using Xunit;

namespace SonoroScore.Scanner.Tests;

public sealed class FuzzyMatcherTests
{
    private static readonly EchoCatalogEntry[] Catalog = LoadCatalog();
    private static readonly Dictionary<string, string> Norm =
        Catalog.ToDictionary(e => e.Name, e => FuzzyMatcher.Normalize(e.Name));

    private static EchoCatalogEntry[] LoadCatalog()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "fixtures", "catalog", "echo_catalog.json");
        return GameDatabase.LoadFromFileAsync(path).GetAwaiter().GetResult();
    }

    [Fact]
    public void EveryCatalogName_MatchesItself_WithScoreOne()
    {
        foreach (var e in Catalog)
        {
            var (entry, score) = FuzzyMatcher.ClosestMatch(e.Name, Catalog, c => c.Name);
            Assert.NotNull(entry);
            Assert.Equal(e.Name, entry!.Name);
            Assert.Equal(1.0f, score, 3);
        }
    }

    [Fact]
    public void SubstringCollisionPairs_EachResolvesToItself()
    {
        int pairs = 0;
        foreach (var a in Catalog)
        {
            foreach (var b in Catalog)
            {
                if (ReferenceEquals(a, b)) continue;
                string na = Norm[a.Name], nb = Norm[b.Name];
                if (na.Length < 5 || !nb.Contains(na)) continue;
                pairs++;

                var (hitA, _) = FuzzyMatcher.ClosestMatch(a.Name, Catalog, c => c.Name);
                var (hitB, _) = FuzzyMatcher.ClosestMatch(b.Name, Catalog, c => c.Name);
                Assert.Equal(a.Name, hitA!.Name);
                Assert.Equal(b.Name, hitB!.Name);
            }
        }
        Assert.True(pairs >= 20, $"expected the real catalog's substring collisions, found {pairs}");
    }

    [Fact]
    public void SingleCharCorruption_UsuallyResolvesToOriginal_NeverToAnotherAtHighScore()
    {
        int total = 0, resolved = 0;
        var wrongHigh = new List<string>();
        var misses = new List<string>();

        foreach (var e in Catalog)
        {
            string norm = Norm[e.Name];
            for (int i = 0; i < norm.Length; i++)
            {
                char orig = norm[i];
                char sub = orig == 'x' ? 'y' : 'x';   // deterministic single substitution
                string q = norm[..i] + sub + norm[(i + 1)..];
                total++;

                var (entry, score) = FuzzyMatcher.ClosestMatch(q, Catalog, c => c.Name);
                if (entry != null && entry.Name == e.Name) resolved++;
                else if (entry != null && score >= 0.9f)
                    wrongHigh.Add($"{e.Name} q='{q}' -> {entry.Name} @{score:F3}");
                else if (misses.Count < 12)
                    misses.Add($"{e.Name} q='{q}' -> {entry?.Name ?? "null"} @{score:F3}");
            }
        }

        Assert.True(wrongHigh.Count == 0,
            "corruption resolved to a DIFFERENT entry at score >= 0.9:\n" + string.Join("\n", wrongHigh.Take(10)));

        double rate = (double)resolved / total;
        Assert.True(rate >= 0.95,
            $"resolve rate {rate:P1} is below 95%; sample misses: " + string.Join(" | ", misses));
    }

    [Fact]
    public void QueriesShorterThanFourCharacters_NeverMatch()
    {
        foreach (var q in new[] { "a", "ab", "abc", "12", "xy" })
        {
            var (entry, _) = FuzzyMatcher.ClosestMatch(q, Catalog, c => c.Name);
            Assert.Null(entry);
        }
    }

    [Fact]
    public void Result_IsIndependentOfCatalogOrder()
    {
        var rng = new Random(20260928);           // seeded => deterministic
        var probes = Catalog.Where((_, i) => i % 9 == 0).ToArray();

        foreach (var probe in probes)
        {
            var (expectEntry, expectScore) = FuzzyMatcher.ClosestMatch(probe.Name, Catalog, c => c.Name);

            for (int shuffle = 0; shuffle < 20; shuffle++)
            {
                var bag = Catalog.ToArray();
                for (int i = bag.Length - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (bag[i], bag[j]) = (bag[j], bag[i]);
                }
                var (entry, score) = FuzzyMatcher.ClosestMatch(probe.Name, bag, c => c.Name);
                Assert.Equal(expectEntry?.Name, entry?.Name);
                Assert.Equal(expectScore, score, 3);
            }
        }
    }

    [Fact]
    public void NearTie_BetweenDistinctEntries_IsReportedAmbiguous()
    {
        var cat = new[]
        {
            new EchoCatalogEntry("testab", 1, [5], []),
            new EchoCatalogEntry("testac", 1, [5], []),
        };

        var r = FuzzyMatcher.Match("testad", cat, c => c.Name, threshold: 0.6f);

        Assert.True(r.Ambiguous);
        Assert.NotNull(r.Entry);
        Assert.NotNull(r.RunnerUp);
        Assert.NotEqual(r.Entry!.Name, r.RunnerUp!.Name);
        Assert.True(Math.Abs(r.Score - r.RunnerUpScore) < 0.05f);
    }
}
