// Ac 2026 RadianceNoMore (Sonoro-Score, MIT).
// D-04 pins for the main-stat tables (ported from ../Tacet-Lab echo-main-stats.ts).
// The strongest test here is the corpus cross-check: the tables must reproduce the
// panel values of real fixtures, not just the values we typed in.

using System.Text.Json;
using SonoroScore.Scanner;
using Xunit;

namespace SonoroScore.Scanner.Tests;

public sealed class EchoMainStatsTests
{
    private static string FixtureDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
        "fixtures", "echoes", "english-1080p");

    /// <summary>
    /// Sidecars already known (and image-verified) to be stale: idx061's name reads
    /// "Chop Chop: Headless" and its cost contradicts its own main/secondary values.
    /// Anything OUTSIDE this list that fails the cross-check is a real problem.
    /// </summary>
    private static readonly HashSet<string> KnownStaleSidecars = ["echo_p05_r01_c01_idx061.json"];

    private static List<(string File, EchoFixture Fx)> LoadFixtures()
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return Directory.GetFiles(FixtureDir, "*.json").OrderBy(f => f)
            .Select(f => (Path.GetFileName(f),
                          JsonSerializer.Deserialize<EchoFixture>(File.ReadAllText(f), opts)!))
            .ToList();
    }

    [Theory]
    [InlineData(4, 5, 25, StatKey.CritRate, 22.0f)]
    [InlineData(4, 5, 0, StatKey.CritRate, 4.4f)]
    [InlineData(4, 5, 25, StatKey.CritDamage, 44.0f)]
    [InlineData(3, 5, 25, StatKey.AtkPercent, 30.0f)]
    [InlineData(1, 5, 25, StatKey.HpPercent, 22.8f)]
    [InlineData(3, 5, 25, StatKey.EnergyRegen, 32.0f)]
    [InlineData(4, 5, 0, StatKey.CritDamage, 8.8f)]
    public void PrimaryValues_MatchKnownGameValues(int cost, int rarity, int level, StatKey key, float expected)
        => Assert.Equal(expected, EchoMainStats.PrimaryValue(cost, rarity, level, key)!.Value, 3);

    [Theory]
    [InlineData(1, 5, 25, 2280)]
    [InlineData(1, 5, 0, 456)]
    [InlineData(3, 5, 25, 100)]
    [InlineData(3, 5, 0, 20)]
    [InlineData(4, 5, 25, 150)]
    [InlineData(4, 5, 0, 30)]
    public void SecondaryValues_MatchKnownGameValues(int cost, int rarity, int level, float expected)
        => Assert.Equal(expected, EchoMainStats.SecondaryValue(cost, rarity, level)!.Value, 1);

    [Fact]
    public void SecondaryKey_IsHpForCostOne_AndAtkOtherwise()
    {
        Assert.Equal(StatKey.Hp, EchoMainStats.SecondaryKey(1));
        Assert.Equal(StatKey.Atk, EchoMainStats.SecondaryKey(3));
        Assert.Equal(StatKey.Atk, EchoMainStats.SecondaryKey(4));
    }

    [Fact]
    public void MainStatsAreCostConstrained()
    {
        Assert.False(EchoMainStats.IsMainStatAllowed(1, StatKey.CritRate));
        Assert.False(EchoMainStats.IsMainStatAllowed(1, StatKey.EnergyRegen));
        Assert.True(EchoMainStats.IsMainStatAllowed(3, StatKey.EnergyRegen));
        Assert.True(EchoMainStats.IsMainStatAllowed(3, StatKey.HavocDamage));
        Assert.False(EchoMainStats.IsMainStatAllowed(3, StatKey.CritRate));
        Assert.True(EchoMainStats.IsMainStatAllowed(4, StatKey.CritRate));
        Assert.False(EchoMainStats.IsMainStatAllowed(4, StatKey.EnergyRegen));
        Assert.True(EchoMainStats.IsMainStatAllowed(4, StatKey.HealingBonus));
    }

    [Fact]
    public void FindConsistentLevel_IsUniqueOrNothing()
    {
        Assert.Equal(25, EchoMainStats.FindConsistentLevel(4, 5, StatKey.CritRate, 22f));
        Assert.Equal(0, EchoMainStats.FindConsistentLevel(4, 5, StatKey.CritDamage, 8.8f));
        Assert.Null(EchoMainStats.FindConsistentLevel(4, 5, StatKey.CritRate, 99f));
    }

    [Fact]
    public void PrimaryTable_ReproducesTheFixtureCorpus()
    {
        var unexpected = new List<string>();
        int checkedCount = 0;

        foreach (var (file, fx) in LoadFixtures())
        {
            if (fx.Level is not { } level || fx.Cost is not { } cost || fx.MainStat?.Key is not { } keyStr) continue;
            if (!Enum.TryParse<StatKey>(keyStr, ignoreCase: true, out var key)) continue;
            checkedCount++;

            var expected = EchoMainStats.PrimaryValue(cost, fx.Rarity ?? 5, level, key);
            bool ok = expected.HasValue && Math.Abs(expected.Value - fx.MainStat.Value) <= 0.051f;
            if (!ok && !KnownStaleSidecars.Contains(file))
                unexpected.Add($"{file}: cost={cost} lvl={level} {key} fixture={fx.MainStat.Value} " +
                               $"table={(expected?.ToString() ?? "n/a")}");
        }

        Assert.True(checkedCount >= 20, $"only {checkedCount} fixtures were checkable");
        Assert.True(unexpected.Count == 0,
            "the ported table does not reproduce these fixtures:\n  " + string.Join("\n  ", unexpected));
    }

    [Fact]
    public void SecondaryTable_ReproducesTheFixtureCorpus()
    {
        var unexpected = new List<string>();
        int checkedCount = 0;

        foreach (var (file, fx) in LoadFixtures())
        {
            if (fx.Level is not { } level || fx.Cost is not { } cost) continue;
            var second = fx.SecondMainStat;
            if (second?.Key is not { } keyStr) continue;
            if (!Enum.TryParse<StatKey>(keyStr, ignoreCase: true, out var key)) continue;
            checkedCount++;

            bool keyOk = key == EchoMainStats.SecondaryKey(cost);
            var expected = EchoMainStats.SecondaryValue(cost, fx.Rarity ?? 5, level);
            bool valueOk = expected.HasValue && Math.Abs(expected.Value - second.Value) <= 0.51f;

            if ((!keyOk || !valueOk) && !KnownStaleSidecars.Contains(file))
                unexpected.Add($"{file}: cost={cost} lvl={level} {key}={second.Value} " +
                               $"table={(expected?.ToString() ?? "n/a")} key={(keyOk ? "ok" : "wrong")}");
        }

        Assert.True(checkedCount >= 20, $"only {checkedCount} fixtures were checkable");
        Assert.True(unexpected.Count == 0,
            "the ported secondary table does not reproduce these fixtures:\n  " + string.Join("\n  ", unexpected));
    }
}
