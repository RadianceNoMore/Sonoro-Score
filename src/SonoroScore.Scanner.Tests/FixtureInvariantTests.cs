// Ac 2026 RadianceNoMore (Sonoro-Score, MIT).
// A-04: structural invariants every fixture must satisfy, independent of what
// the pipeline currently outputs. These are truth rules, not snapshots.

using System.Text.Json;
using SonoroScore.Scanner;
using Xunit;

namespace SonoroScore.Scanner.Tests;

public sealed class FixtureInvariantTests
{
    private static string FixtureDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
        "fixtures", "echoes", "english-1080p");

    private static List<(string File, EchoFixture Fixture)> LoadFixtures()
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return Directory.GetFiles(FixtureDir, "*.json")
            .OrderBy(f => f)
            .Select(f => (Path.GetFileName(f), JsonSerializer.Deserialize<EchoFixture>(File.ReadAllText(f), opts)!))
            .ToList();
    }

    private static List<FixtureStatLine> Subs(EchoFixture fx)
        => fx.SubStats ?? new List<FixtureStatLine>();

    [Fact]
    public void EveryFixture_HasExpectedSubstatCountForLevel()
    {
        var bad = new List<string>();
        foreach (var (file, fx) in LoadFixtures())
        {
            if (fx.Level is not { } level) { bad.Add($"{file}: missing level"); continue; }
            int expected = Math.Min(level / 5, 5);        // integer division
            int actual = Subs(fx).Count;
            if (actual != expected)
                bad.Add($"{file}: level={level} expects {expected} substats, fixture has {actual}");
        }
        Assert.True(bad.Count == 0, "Substat-count rule violated:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void EveryFixture_FieldsAreInRange()
    {
        var knownSonatas = GameDatabase.KnownSonatas.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bad = new List<string>();
        foreach (var (file, fx) in LoadFixtures())
        {
            if (fx.Level is not { } level || level < 0 || level > 25)
                bad.Add($"{file}: level {fx.Level} not in [0,25]");
            if (fx.Cost is not { } cost || (cost != 1 && cost != 3 && cost != 4))
                bad.Add($"{file}: cost {fx.Cost} not in {{1,3,4}}");
            if (fx.Rarity is not { } rarity || rarity < 1 || rarity > 5)
                bad.Add($"{file}: rarity {fx.Rarity} not in [1,5]");
            if (!string.IsNullOrEmpty(fx.Sonata) && !knownSonatas.Contains(fx.Sonata))
                bad.Add($"{file}: sonata '{fx.Sonata}' not in GameDatabase.KnownSonatas");
        }
        Assert.True(bad.Count == 0, "Fixture field range violations:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void EveryFixture_HasNoDuplicateSubstatKeys()
    {
        var bad = new List<string>();
        foreach (var (file, fx) in LoadFixtures())
        {
            foreach (var g in Subs(fx)
                         .Select(s => s.Key ?? "")
                         .Where(k => k.Length > 0)
                         .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
            {
                bad.Add($"{file}: duplicate substat key '{g.Key}'");
            }
        }
        Assert.True(bad.Count == 0, "Duplicate substat keys:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void EveryFixture_HasNoExactDuplicateOfMainStat()
    {
        // A substat MAY share the main stat's KEY: an echo has two main stats and
        // the game allows substats to equal a main stat (fixture idx078 proves it:
        // main = atkPercent, and it carries a legitimate atkPercent substat).
        // Only an exact key+value duplicate of the main stat is illegal.
        var bad = new List<string>();
        foreach (var (file, fx) in LoadFixtures())
        {
            var main = fx.MainStat;
            if (main is null || main.Key is not { } mainKey) continue;
            foreach (var s in Subs(fx))
            {
                if (string.Equals(s.Key, mainKey, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(s.Value - main.Value) < 0.05f)
                    bad.Add($"{file}: substat {s.Key} {s.Value} duplicates main stat exactly");
            }
        }
        Assert.True(bad.Count == 0, "Substat duplicates main stat:\n" + string.Join("\n", bad));
    }
}
