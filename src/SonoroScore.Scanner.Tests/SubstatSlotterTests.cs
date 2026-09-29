// Ac 2026 RadianceNoMore (Sonoro-Score, MIT).
// Regression pins: a substat that shares the main stat's key must never be
// dropped, because the game allows substats to equal a main stat.

using SonoroScore.Scanner;
using Xunit;

namespace SonoroScore.Scanner.Tests;

public sealed class SubstatSlotterTests
{
    // Ground truth from the captured echo echo_p01_r01_c02_idx002.png:
    // primary main stat = ATK% 30.0, secondary main stat = ATK 100.
    private static readonly ParsedStat MainStat = new(StatKey.AtkPercent, 30.0f, "ATK", "30.0%");

    [Fact]
    public void MainStatDuplicate_SharedKeyDifferentValue_IsNotDropped()
    {
        // "ATK 7.9%" is parsed as ATK% (StatParser turns ATK + "%" into AtkPercent).
        var sub = StatParser.ParseLine("ATK 7.9%");
        Assert.NotNull(sub);
        Assert.Equal(StatKey.AtkPercent, sub!.Key);

        // Same key as the main stat, different value: a real substat, keep it.
        Assert.False(SubstatSlotter.IsMainStatDuplicate(MainStat, sub.Key, sub.Value));
    }

    [Fact]
    public void MainStatDuplicate_ExactKeyAndValue_IsDropped()
    {
        // Only an exact duplicate of the main stat line may be discarded.
        Assert.True(SubstatSlotter.IsMainStatDuplicate(MainStat, StatKey.AtkPercent, 30.0f));
    }

    [Fact]
    public void MainStatDuplicate_DifferentKey_IsNotDropped()
    {
        Assert.False(SubstatSlotter.IsMainStatDuplicate(MainStat, StatKey.CritRate, 30.0f));
    }

    [Fact]
    public void NullMainStat_NeverDrops()
    {
        Assert.False(SubstatSlotter.IsMainStatDuplicate(null, StatKey.AtkPercent, 30.0f));
    }

    [Fact]
    public void Idx002_AllFiveRawSubstatRowsSurviveTheMainStatGuard()
    {
        // Exact RawSubstatsOcr rows for echo_p01_r01_c02_idx002.png.
        string[] raw =
        [
            "Crit. Rate                       6.9%",
            "ATK                                            7.9%",
            "Energy Regen                    9.2%",
            "Crit. DMG                      12.6%",
            "HP                                      9.4%",
        ];

        var parsed = raw.Select(StatParser.ParseLine).Where(p => p != null).Select(p => p!).ToList();
        Assert.Equal(5, parsed.Count);

        // Every row survives the guard; the ATK% substat must be present.
        var survivors = parsed
            .Where(p => !SubstatSlotter.IsMainStatDuplicate(MainStat, p.Key, p.Value))
            .ToList();

        Assert.Equal(5, survivors.Count);
        Assert.Contains(survivors, p => p.Key == StatKey.AtkPercent && Math.Abs(p.Value - 7.9f) < 0.001f);

        // The old key-only rule would have removed exactly this row (leaving 4).
        Assert.Equal(4, parsed.Count(p => p.Key != MainStat.Key));
    }

    // ---------------------------------------------------------- C-04 slotting

    [Theory]
    [InlineData("Echo Skill")]
    [InlineData("Sonata Effect")]
    [InlineData("Equipped by Aemeath")]
    [InlineData("echo skill text")]
    public void HeaderLines_AreExcluded(string text)
        => Assert.True(SubstatSlotter.IsHeaderLine(text));

    [Theory]
    [InlineData("Crit. Rate")]
    [InlineData("Energy Regen")]
    [InlineData("HP")]
    public void StatLines_AreNotHeaders(string text)
        => Assert.False(SubstatSlotter.IsHeaderLine(text));

    [Fact]
    public void MaxSlotDistance_IsSixtyPercentOfMedianPitch()
    {
        double[] centers = [50, 150, 250, 350, 450];   // even pitch 100
        Assert.Equal(60.0, SubstatSlotter.MaxSlotDistance(centers), 3);
    }

    [Fact]
    public void NearestSlot_ReportsDistance_AndFarLinesBecomeOrphans()
    {
        double[] centers = [50, 150, 250, 350, 450];

        var (slot, dist) = SubstatSlotter.NearestSlot(255, centers);
        Assert.Equal(2, slot);
        Assert.Equal(5.0, dist, 3);

        var (farSlot, farDist) = SubstatSlotter.NearestSlot(1000, centers);
        Assert.Equal(4, farSlot);
        Assert.Equal(550.0, farDist, 3);
        Assert.True(farDist > SubstatSlotter.MaxSlotDistance(centers),
            "a line 550px from the nearest slot must exceed the orphan threshold");
    }

    // ---------------------------------------------------------- C-07 validity

    [Fact]
    public void ValidSubstatKeys_AreDataDrivenFromTheRollTables()
    {
        // Keys with a tunable roll table are legal substats.
        Assert.True(TunableRolls.IsValidSubstatKey(StatKey.Hp));
        Assert.True(TunableRolls.IsValidSubstatKey(StatKey.AtkPercent));
        Assert.True(TunableRolls.IsValidSubstatKey(StatKey.CritRate));
        Assert.True(TunableRolls.IsValidSubstatKey(StatKey.EnergyRegen));
        Assert.True(TunableRolls.IsValidSubstatKey(StatKey.LiberationDamage));

        // Element DMG / healing have no roll table -> never a substat.
        Assert.False(TunableRolls.IsValidSubstatKey(StatKey.SpectroDamage));
        Assert.False(TunableRolls.IsValidSubstatKey(StatKey.HealingBonus));
        Assert.False(TunableRolls.IsValidSubstatKey(StatKey.Unknown));
    }
}
