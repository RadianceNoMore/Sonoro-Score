// Ac 2026 RadianceNoMore (Sonoro-Score, MIT).
// Regression pins for OCR decimal-point recovery (TODO F-11 / C-06):
// a dropped point in a percent value must be restored, not accepted verbatim.

using SonoroScore.Scanner;
using Xunit;

namespace SonoroScore.Scanner.Tests;

public sealed class StatParserTests
{
    [Fact]
    public void PercentAtk_WithDroppedDecimalPoint_IsRecovered()
    {
        // Real case: game shows "ATK 7.9%" but Tesseract emitted "ATK 79%"
        // (echo_p01_r01_c01_idx001). Must become AtkPercent 7.9, not 79.
        var p = StatParser.ParseLine("ATK 79%");
        Assert.NotNull(p);
        Assert.Equal(StatKey.AtkPercent, p!.Key);
        Assert.Equal(7.9f, p.Value, 3);
    }

    [Fact]
    public void PercentHpAndDef_WithDroppedDecimalPoint_AreRecovered()
    {
        Assert.Equal(7.9f, StatParser.ParseLine("HP 79%")!.Value, 3);
        Assert.Equal(7.9f, StatParser.ParseLine("DEF 79%")!.Value, 3);
    }

    [Fact]
    public void Percent_WithTwoDroppedDigits_IsRecovered()
    {
        // "840%" -> 8.4%
        Assert.Equal(8.4f, StatParser.ParseLine("Energy Regen 840%")!.Value, 3);
    }

    [Fact]
    public void FlatValue_WithoutPercentSign_IsUnchanged()
    {
        // A genuine flat integer value must never be scaled down.
        var p = StatParser.ParseLine("ATK 150");
        Assert.NotNull(p);
        Assert.Equal(StatKey.Atk, p!.Key);
        Assert.Equal(150f, p.Value, 3);
    }

    [Fact]
    public void NormalPercent_BelowThreshold_IsUnchanged()
    {
        var p = StatParser.ParseLine("Crit. Rate 22.0%");
        Assert.NotNull(p);
        Assert.Equal(StatKey.CritRate, p!.Key);
        Assert.Equal(22.0f, p.Value, 3);
    }

    [Fact]
    public void StrayGlyphBetweenLabelAndValue_StillParses()
    {
        // F-47 (A-05 idx024): the strip reads "ATK j 100" - the value regex matches with
        // the junk inside the label, so the exact alias used to fail and the field was
        // dropped. Up to two trailing tokens may now be dropped; the alias stays exact.
        var p = StatParser.ParseLine("ATK j 100");
        Assert.NotNull(p);
        Assert.Equal(StatKey.Atk, p!.Key);
        Assert.Equal(100f, p.Value, 3);

        var q = StatParser.ParseLine("DEF Co 12.8%");
        Assert.NotNull(q);
        Assert.Equal(StatKey.DefPercent, q!.Key);
        Assert.Equal(12.8f, q.Value, 3);
    }

    [Fact]
    public void JunkThatIsNotAStat_StillFails()
    {
        Assert.Null(StatParser.ParseLine("blah blup 100"));
        Assert.Null(StatParser.ParseLine("j 100"));
    }
}
