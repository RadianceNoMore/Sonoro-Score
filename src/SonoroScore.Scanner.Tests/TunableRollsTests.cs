// Ac 2026 RadianceNoMore (Sonoro-Score, MIT).
// C-05 / C-06 pins: exact rolls resolve exactly, tolerance snapping is gone,
// a correction is accepted only when it is provably unique, and midpoints
// between rolls never get "corrected".

using SonoroScore.Scanner;
using Xunit;

namespace SonoroScore.Scanner.Tests;

public sealed class TunableRollsTests
{
    private static readonly StatKey[] RollKeys =
    [
        StatKey.Hp, StatKey.Atk, StatKey.Def,
        StatKey.CritRate, StatKey.CritDamage, StatKey.EnergyRegen,
        StatKey.HpPercent, StatKey.AtkPercent, StatKey.DefPercent,
        StatKey.BasicDamage, StatKey.HeavyDamage, StatKey.SkillDamage, StatKey.LiberationDamage,
    ];

    [Fact]
    public void EveryLegalRoll_ResolvesExactly()
    {
        foreach (var key in RollKeys)
        {
            var rolls = TunableRolls.RollsFor(key);
            Assert.NotEmpty(rolls);

            foreach (var roll in rolls)
            {
                var r = TunableRolls.ResolveDetailed(key, roll);
                Assert.Equal(TunableRolls.RollState.Exact, r.State);
                Assert.Equal(roll, r.Value);
            }
        }
    }

    [Fact]
    public void RollSpacingMidpoints_AreNeverAcceptedAsExact()
    {
        // A midpoint between two legal rolls is never a legal roll itself. It MAY
        // still be Corrected, but only when the bounded search is provably unique
        // (the C-06 "unless exactly-one rule holds" clause, e.g. Hp 560 -> 580 by a
        // single 6->8 substitution). Anything ambiguous must stay a proposal.
        foreach (var key in RollKeys)
        {
            var rolls = TunableRolls.RollsFor(key);
            for (int i = 0; i + 1 < rolls.Count; i++)
            {
                float mid = (rolls[i] + rolls[i + 1]) / 2f;
                var r = TunableRolls.ResolveDetailed(key, mid);
                Assert.NotEqual(TunableRolls.RollState.Exact, r.State);
                if (r.State == TunableRolls.RollState.Corrected)
                    Assert.Single(r.Candidates);
            }
        }
    }

    [Fact]
    public void Corrected_AlwaysHasExactlyOneCandidate()
    {
        foreach (var key in RollKeys)
        {
            foreach (var roll in TunableRolls.RollsFor(key))
            {
                foreach (var delta in new[] { -1.1f, -0.6f, -0.3f, 0.3f, 0.6f, 1.1f })
                {
                    var r = TunableRolls.ResolveDetailed(key, roll + delta);
                    if (r.State == TunableRolls.RollState.Corrected)
                    {
                        Assert.NotNull(r.Value);
                        Assert.Single(r.Candidates);
                    }
                }
            }
        }
    }

    [Fact]
    public void OneSevenConfusion_IsCorrectedUniquely()
    {
        var r = TunableRolls.ResolveDetailed(StatKey.CritRate, 1.5f);
        Assert.Equal(TunableRolls.RollState.Corrected, r.State);
        Assert.Equal(7.5f, r.Value);
        Assert.True(r.IsUsable);
    }

    [Fact]
    public void MissingDecimalPoint_OnPercentKey_IsCorrectedUniquely()
    {
        var r = TunableRolls.ResolveDetailed(StatKey.AtkPercent, 79f);
        Assert.Equal(TunableRolls.RollState.Corrected, r.State);
        Assert.Equal(7.9f, r.Value);
    }

    [Fact]
    public void FarOffValue_IsNotARoll_WithNoFabricatedValue()
    {
        var r = TunableRolls.ResolveDetailed(StatKey.CritRate, 50f);
        Assert.Equal(TunableRolls.RollState.NotARoll, r.State);
        Assert.Null(r.Value);
        Assert.False(r.IsUsable);
    }

    [Fact]
    public void ToleranceBasedSnapping_IsGone()
    {
        // 10.2 is 0.2 away from the legal 10 - the OLD code tolerance-snapped that
        // (tolerance 0.35). C-05 forbids it: no fabricated value without a unique
        // bounded-confusion search.
        var r = TunableRolls.ResolveDetailed(StatKey.DefPercent, 10.2f);
        Assert.NotEqual(TunableRolls.RollState.Exact, r.State);
        Assert.NotEqual(10f, r.Value);
        Assert.False(r.IsUsable);
    }

    [Fact]
    public void DefPercent_UsesItsOwnTable_NotCommonPercent()
    {
        // C-08: Tacet-Lab ships a DEF%-specific table (8.1 .. 14.7). The port had
        // DefPercent on the common percent table, so real DEF% rolls (8.1, 10.0,
        // 11.8) were treated as non-rolls and tolerance-snapped to 7.9 / 10.1 / 11.6.
        Assert.Equal(new[] { 8.1f, 9f, 10f, 10.9f, 11.8f, 12.8f, 13.8f, 14.7f },
                     TunableRolls.RollsFor(StatKey.DefPercent));

        foreach (var real in new[] { 8.1f, 10f, 11.8f })
            Assert.Equal(TunableRolls.RollState.Exact,
                         TunableRolls.ResolveDetailed(StatKey.DefPercent, real).State);

        // A common-percent value is not a DEF% roll.
        Assert.Equal(TunableRolls.RollState.NotARoll,
                     TunableRolls.ResolveDetailed(StatKey.DefPercent, 6.4f).State);
    }

    [Fact]
    public void KeysWithoutRollTable_AreNotARoll()
    {
        var r = TunableRolls.ResolveDetailed(StatKey.SpectroDamage, 30f);
        Assert.Equal(TunableRolls.RollState.NotARoll, r.State);
        Assert.Null(r.Value);
    }
}
