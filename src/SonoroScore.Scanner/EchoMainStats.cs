// Ac 2026 RadianceNoMore (Sonoro-Score, MIT).
// D-04: primary / secondary main-stat tables and the cross-field consistency solver.
// Ported from ../Tacet-Lab/src/game-data/echo-main-stats.ts (GPL-3.0; see NOTICES.md),
// whose source ranges were checked against
// https://wutheringwaves.fandom.com/wiki/Echo/Stats (2026-07-14).
//
// Deviation from the JS original: Tacet uses Number.EPSILON (2.22e-16) as a guard
// against floating-point representation loss at range endpoints. C#'s double.Epsilon
// is a different quantity entirely (smallest subnormal), so a plain 1e-9 is used here
// for the same purpose - it can only lift a value that is within a nanounit of an
// integer, which no real stat value ever is.

namespace SonoroScore.Scanner;

public static class EchoMainStats
{
    private const double Eps = 1e-9;

    // ---------------------------------------------------------------- key sets

    private static readonly Dictionary<int, StatKey[]> KeysByCost = new()
    {
        [1] = [StatKey.HpPercent, StatKey.AtkPercent, StatKey.DefPercent],
        [3] =
        [
            StatKey.HpPercent, StatKey.AtkPercent, StatKey.DefPercent,
            StatKey.GlacioDamage, StatKey.FusionDamage, StatKey.ElectroDamage,
            StatKey.AeroDamage, StatKey.SpectroDamage, StatKey.HavocDamage, StatKey.EnergyRegen,
        ],
        [4] =
        [
            StatKey.HpPercent, StatKey.AtkPercent, StatKey.DefPercent,
            StatKey.CritRate, StatKey.CritDamage, StatKey.HealingBonus,
        ],
    };

    /// <summary>mainStatKeysByCost: can a cost-<paramref name="cost"/> echo roll this main stat?</summary>
    public static bool IsMainStatAllowed(int cost, StatKey key)
        => KeysByCost.TryGetValue(cost, out var keys) && Array.IndexOf(keys, key) >= 0;

    public static IReadOnlyList<StatKey> AllowedMainStats(int cost)
        => KeysByCost.TryGetValue(cost, out var keys) ? keys : [];

    /// <summary>maxLevelByRarity.</summary>
    public static int MaxLevelByRarity(int rarity) => rarity switch
    {
        1 => 5, 2 => 10, 3 => 15, 4 => 20, _ => 25,
    };

    // ------------------------------------------------------------------ ranges

    private sealed record Range(double Start, double End);

    private sealed record Ranges(Range R2, Range R3, Range R4, Range R5)
    {
        public Range? For(int rarity) => rarity switch
        {
            2 => R2, 3 => R3, 4 => R4, 5 => R5, _ => null,
        };
    }

    private static Range R(double s, double e) => new(s, e);
    private static Ranges Rs(Range r2, Range r3, Range r4, Range r5) => new(r2, r3, r4, r5);

    private static readonly Ranges CommonPercent = Rs(R(2.8, 7.2), R(3, 10.2), R(3.4, 14.2), R(4.5, 22.8));
    private static readonly Ranges EliteNormal = Rs(R(3.7, 9.6), R(4, 14), R(4.5, 18.9), R(6, 30));

    private static readonly Dictionary<int, Dictionary<StatKey, Ranges>> PrimaryByCost = new()
    {
        [1] = new()
        {
            [StatKey.HpPercent] = CommonPercent,
            [StatKey.AtkPercent] = Rs(R(2.2, 5.7), R(2.4, 8.1), R(2.7, 11.3), R(3.6, 18)),
            [StatKey.DefPercent] = Rs(R(2.2, 5.7), R(2.4, 8.1), R(2.7, 11.3), R(3.6, 18)),
        },
        [3] = new()
        {
            [StatKey.HpPercent] = EliteNormal,
            [StatKey.AtkPercent] = EliteNormal,
            [StatKey.DefPercent] = Rs(R(4.7, 12.3), R(5, 17), R(5.7, 23.9), R(7.6, 38)),
            [StatKey.GlacioDamage] = EliteNormal,
            [StatKey.FusionDamage] = EliteNormal,
            [StatKey.ElectroDamage] = EliteNormal,
            [StatKey.AeroDamage] = EliteNormal,
            [StatKey.SpectroDamage] = EliteNormal,
            [StatKey.HavocDamage] = EliteNormal,
            [StatKey.EnergyRegen] = Rs(R(3.8, 10), R(4.2, 14.2), R(4.8, 20.1), R(6.4, 32)),
        },
        [4] = new()
        {
            [StatKey.HpPercent] = Rs(R(4.1, 10.6), R(4.3, 14.6), R(4.9, 20.5), R(6.6, 33)),
            [StatKey.AtkPercent] = Rs(R(4.1, 10.6), R(4.3, 14.6), R(4.9, 20.5), R(6.6, 33)),
            [StatKey.DefPercent] = Rs(R(5.2, 13.5), R(5.5, 18.7), R(6.2, 26), R(8.3, 41.5)),
            [StatKey.CritRate] = Rs(R(2.7, 7.1), R(2.9, 9.8), R(3.3, 13.8), R(4.4, 22)),
            [StatKey.CritDamage] = Rs(R(5.4, 14.3), R(5.8, 19.7), R(6.6, 27.7), R(8.8, 44)),
            [StatKey.HealingBonus] = Rs(R(3.3, 8.5), R(3.5, 11.9), R(3.9, 16.3), R(5.2, 26.4)),
        },
    };

    private static readonly Dictionary<int, Ranges> SecondaryByCost = new()
    {
        [1] = Rs(R(114, 296), R(152, 516), R(228, 957), R(456, 2280)),
        [3] = Rs(R(12, 31), R(13, 44), R(15, 63), R(20, 100)),
        [4] = Rs(R(18, 46), R(20, 68), R(22, 92), R(30, 150)),
    };

    // ------------------------------------------------------------- scaled value

    private static double Scaled(Range range, int level, int maxLevel, bool flat = false)
    {
        double value = range.Start + (range.End - range.Start) * Math.Max(0, Math.Min(maxLevel, level)) / (double)maxLevel;
        return flat ? Math.Floor(value + Eps)
                    : Math.Floor(value * 10 + Eps) / 10.0;
    }

    /// <summary>primaryMainStatValue: the deterministic value, or null when the combo is illegal.</summary>
    public static float? PrimaryValue(int cost, int rarity, int level, StatKey key)
    {
        if (!PrimaryByCost.TryGetValue(cost, out var byKey) || !byKey.TryGetValue(key, out var ranges)) return null;
        var range = ranges.For(rarity);
        return range == null ? null : (float)Scaled(range, level, MaxLevelByRarity(rarity));
    }

    /// <summary>fixedSecondaryMainStat key: HP for cost 1, ATK otherwise.</summary>
    public static StatKey SecondaryKey(int cost) => cost == 1 ? StatKey.Hp : StatKey.Atk;

    /// <summary>fixedSecondaryMainStat: the deterministic secondary value.</summary>
    public static float? SecondaryValue(int cost, int rarity, int level)
    {
        if (!SecondaryByCost.TryGetValue(cost, out var ranges)) return null;
        var range = ranges.For(rarity) ?? ranges.For(2);
        return range == null ? null : (float)Scaled(range, level, MaxLevelByRarity(rarity), flat: true);
    }

    /// <summary>
    /// D-04: the single level in 0..25 at which <paramref name="value"/> is the legal
    /// primary value for (cost, rarity, key). Null means zero or several candidates -
    /// in both cases the level must NOT be "corrected".
    /// </summary>
    public static int? FindConsistentLevel(int cost, int rarity, StatKey key, float value)
    {
        int? found = null;
        for (int lvl = 0; lvl <= 25; lvl++)
        {
            var expected = PrimaryValue(cost, rarity, lvl, key);
            if (expected.HasValue && Math.Abs(expected.Value - value) <= 0.051f)
            {
                if (found.HasValue) return null;   // ambiguous: refuse to guess
                found = lvl;
            }
        }
        return found;
    }
}
