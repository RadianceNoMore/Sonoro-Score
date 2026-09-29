// Ac 2026 RadianceNoMore (Sonoro-Score, MIT).
// E-01/E-02 export-safety pins: missing fields are REJECTED (never defaulted),
// only accepted roll values may be exported, and rarity is the documented constant.

using SonoroScore.Scanner;
using Xunit;

namespace SonoroScore.Scanner.Tests;

public sealed class ExportSafetyTests
{
    private static EchoScanResult Scan(
        string? name = "Dreamless", int? cost = 1, int? level = 0,
        string? sonata = "Havoc Eclipse", string? mainKey = "CritRate", float mainVal = 8.7f,
        params SubstatResult[] subs)
        => new()
        {
            ImageFile = "echo_p99_r01_c01_idx999.png",
            ImagePath = @"C:\fixtures\echo_p99_r01_c01_idx999.png",
            ScannedAt = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc),
            EchoName = name == null ? null : new FieldResult(name, 0.9f),
            Cost = cost == null ? null : new FieldResult(cost.Value, 0.9f),
            Rarity = new FieldResult(5, 1.0f, "constant 5* (panel shows no rarity)"),
            Level = level == null ? null : new FieldResult(level.Value, 0.9f),
            Sonata = sonata == null ? null : new FieldResult(sonata, 0.9f),
            MainStatKey = mainKey == null ? null : new FieldResult(mainKey, 0.9f),
            MainStatValue = new FieldResult(mainVal, 0.9f),
            Substats = subs.ToList(),
        };

    private static SubstatResult Sub(string key, float value, float? snapped) => new(key, value, snapped, 0.9f);

    [Fact]
    public void MissingName_IsRejected_NeverDefaultedToUnknown()
    {
        var outcome = ExportableEcho.FromScanResult(Scan(name: null));

        Assert.Null(outcome.Echo);
        Assert.False(outcome.Ok);
        Assert.Contains(outcome.Rejected, r => r.Contains("name", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("cost")]
    [InlineData("level")]
    [InlineData("sonata")]
    [InlineData("mainStat")]
    public void MissingRequiredField_IsRejected(string field)
    {
        var scan = field switch
        {
            "cost" => Scan(cost: null),
            "level" => Scan(level: null),
            "sonata" => Scan(sonata: null),
            _ => Scan(mainKey: null),
        };

        var outcome = ExportableEcho.FromScanResult(scan);
        Assert.Null(outcome.Echo);
        Assert.NotEmpty(outcome.Rejected);
    }

    [Fact]
    public void Rarity_IsTheDocumentedConstant_AndNeverARejectionReason()
    {
        var outcome = ExportableEcho.FromScanResult(Scan());

        Assert.NotNull(outcome.Echo);
        Assert.Equal(5, outcome.Echo!.Rarity);
        Assert.DoesNotContain(outcome.Rejected, r => r.Contains("rarity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnusableRoll_QuarantinesStrict_ButOnlyDropsTheRowWhenPartial()
    {
        var scan = Scan(level: 10,
            subs: [Sub("CritRate", 8.7f, 8.7f), Sub("AtkPercent", 7.1f, null)]);

        var strict = ExportableEcho.FromScanResult(scan, ExportPolicy.Strict);
        Assert.Null(strict.Echo);
        Assert.Contains(strict.Rejected, r => r.Contains("AtkPercent"));

        var partial = ExportableEcho.FromScanResult(scan, ExportPolicy.IncludePartial);
        Assert.NotNull(partial.Echo);
        Assert.Single(partial.Echo!.Substats);
        Assert.Equal("CritRate", partial.Echo.Substats[0].Key);
        // the reason is still reported, it just is not fatal
        Assert.Contains(partial.Rejected, r => r.Contains("AtkPercent"));
    }

    [Fact]
    public void ExportedSubstatValue_IsTheAcceptedRoll_NotTheRawRead()
    {
        // raw 79 is not a roll; the uniquely corrected 7.9 is what may be exported
        var outcome = ExportableEcho.FromScanResult(Scan(level: 5, subs: [Sub("AtkPercent", 79f, 7.9f)]));

        Assert.NotNull(outcome.Echo);
        Assert.Equal(7.9f, outcome.Echo!.Substats[0].Value, 3);
    }

    [Fact]
    public void SubstatCountBelowTheLevelRule_IsReported()
    {
        var outcome = ExportableEcho.FromScanResult(Scan(level: 15, subs: [Sub("CritRate", 8.7f, 8.7f)]));

        Assert.Null(outcome.Echo); // Strict
        Assert.Contains(outcome.Rejected, r => r.Contains("below the level rule"));
    }

    [Fact]
    public void Quarantine_Json_CarriesReasonsAndCounts()
    {
        var rejections = new[]
        {
            new ExportRejection("echo_a.png", ["name missing"]),
            new ExportRejection("echo_b.png",
                ["substat 'AtkPercent' has no accepted roll (raw 7.1)",
                 "substat count 1 is below the level rule (5)"]),
        };

        string json = Quarantine.ToJson(rejections, "test note", totalScans: 10, exported: 8);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(10, root.GetProperty("TotalScans").GetInt32());
        Assert.Equal(8, root.GetProperty("Exported").GetInt32());
        Assert.Equal(2, root.GetProperty("Quarantined").GetInt32());

        var entries = root.GetProperty("Entries");
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Equal("echo_a.png", entries[0].GetProperty("ImageFile").GetString());
        Assert.Equal("name missing", entries[0].GetProperty("Reasons")[0].GetString());
        Assert.Equal(2, entries[1].GetProperty("Reasons").GetArrayLength());
    }

    // -------------------------------------------------------- E-04 round trips

    private static ExportableEcho Model() => new(
        "Dreamless", 1, 5, 10, "Havoc Eclipse", "",
        "CritRate", 8.7f, [("AtkPercent", 7.9f), ("HpPercent", 9.4f)]);

    [Fact]
    public void RoundTrip_TacetPayload_ValidatesAndParsesBack()
    {
        string json = TacetLabExporter.Export([Model()], out int skipped);

        Assert.Equal(0, skipped);
        Assert.Empty(PayloadValidator.ValidateTacet(json));

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var e = doc.RootElement.GetProperty("echoes")[0];
        Assert.Equal("Dreamless", e.GetProperty("name").GetString());
        Assert.Equal(1, e.GetProperty("cost").GetInt32());
        Assert.Equal(5, e.GetProperty("rarity").GetInt32());
        Assert.Equal(10, e.GetProperty("level").GetInt32());
        Assert.Equal("Havoc Eclipse", e.GetProperty("sonata").GetString());
        Assert.Equal("critRate", e.GetProperty("mainStat").GetProperty("key").GetString());
        Assert.Equal(8.7, e.GetProperty("mainStat").GetProperty("value").GetDouble(), 3);
        Assert.Equal(2, e.GetProperty("subStats").GetArrayLength());
        Assert.Equal("atkPercent", e.GetProperty("subStats")[0].GetProperty("key").GetString());
        Assert.Equal(7.9, e.GetProperty("subStats")[0].GetProperty("value").GetDouble(), 3);
    }

    [Fact]
    public void RoundTrip_GoodPayload_ValidatesAndParsesBack()
    {
        string json = GoodExporter.Export([Model()], out int skipped);

        Assert.Equal(0, skipped);
        Assert.Empty(PayloadValidator.ValidateGood(json));

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var a = doc.RootElement.GetProperty("artifacts")[0];
        Assert.Equal(10, a.GetProperty("level").GetInt32());
        Assert.Equal(5, a.GetProperty("rarity").GetInt32());
        Assert.Equal(2, a.GetProperty("substats").GetArrayLength());
    }

    [Fact]
    public void Validator_IsNotARubberStamp()
    {
        const string bad = """{"schemaVersion":7,"gameDataVersion":"x","exportedAt":"y","echoes":[{"id":"a","name":"n","cost":2,"rarity":5,"level":10,"sonata":"s","mainStat":{"key":"critRate","value":8.7},"subStats":[],"locked":false,"excluded":false,"createdAt":1,"source":"scan"}]}""";

        var problems = PayloadValidator.ValidateTacet(bad);

        Assert.Contains(problems, p => p.Contains("cost must be 1, 3 or 4"));
    }

    [Fact]
    public void Validator_FlagsSubstatsBeyondTheLevelRule()
    {
        const string bad = """{"schemaVersion":7,"gameDataVersion":"x","exportedAt":"y","echoes":[{"id":"a","name":"n","cost":1,"rarity":5,"level":5,"sonata":"s","mainStat":{"key":"critRate","value":8.7},"subStats":[{"key":"atkPercent","value":7.9},{"key":"hpPercent","value":9.4}],"locked":false,"excluded":false,"createdAt":1,"source":"scan"}]}""";

        var problems = PayloadValidator.ValidateTacet(bad);

        Assert.Contains(problems, p => p.Contains("maxSubStatsForLevel(5) = 1"));
    }

    [Fact]
    public void TacetExport_OmitsOptionalFields_InsteadOfWritingNull()
    {
        // F-48: Tacet's isEcho accepts `undefined` or a string for equippedBy /
        // equippedByName - a JSON null fails validateAccount, so the whole backup is
        // rejected. The exporter must OMIT them when unknown.
        var outcome = ExportableEcho.FromScanResult(Scan());
        Assert.NotNull(outcome.Echo);

        string json = TacetLabExporter.Export([outcome.Echo!], out _);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var e0 = doc.RootElement.GetProperty("echoes")[0];

        Assert.False(e0.TryGetProperty("equippedBy", out _), "equippedBy must be omitted when unknown");
        Assert.False(e0.TryGetProperty("equippedByName", out _), "equippedByName must be omitted when unknown");
        Assert.Empty(PayloadValidator.ValidateTacet(json));
    }

    [Fact]
    public void TacetValidator_RejectsNullOptionalFields_AndBadSettings()
    {
        // The self-check used to let equippedBy:null through - that is exactly how the
        // broken payload shipped once. Pin both the null rule and the settings mirror.
        const string withNull = """
        {"schemaVersion":7,"gameDataVersion":"x","exportedAt":"y",
         "echoes":[{"id":"a","name":"b","cost":3,"rarity":5,"level":0,"sonata":"s",
                    "mainStat":{"key":"atk","value":1},"subStats":[],"locked":false,
                    "excluded":false,"equippedBy":null,"createdAt":1,"source":"scan"}],
         "characters":[],"weapons":[],"builds":[],"teams":[],"equippedLoadouts":[],
         "theorycraftBuilds":[],"optimizerProfiles":[],"optimizerRuns":[],
         "settings":{"displayName":"R","privacyMode":false,"background":"signal",
                     "scanIntervalMs":900,"scoreWeights":{}}}
        """;
        var problems = PayloadValidator.ValidateTacet(withNull);
        Assert.Contains(problems, pr => pr.Contains("equippedBy"));

        const string badSettings = """
        {"schemaVersion":7,"gameDataVersion":"x","exportedAt":"y",
         "echoes":[],
         "characters":[],"weapons":[],"builds":[],"teams":[],"equippedLoadouts":[],
         "theorycraftBuilds":[],"optimizerProfiles":[],"optimizerRuns":[],
         "settings":{"displayName":"R","privacyMode":false,"background":"neon",
                     "scanIntervalMs":100,"scoreWeights":{}}}
        """;
        var problems2 = PayloadValidator.ValidateTacet(badSettings);
        Assert.Contains(problems2, pr => pr.Contains("background"));
        Assert.Contains(problems2, pr => pr.Contains("scanIntervalMs"));

        // and a missing collection is caught too
        const string missingCollection = """
        {"schemaVersion":7,"gameDataVersion":"x","exportedAt":"y",
         "echoes":[],
         "characters":[],"weapons":[],"builds":[],"teams":[],
         "theorycraftBuilds":[],"optimizerProfiles":[],"optimizerRuns":[],
         "settings":{"displayName":"R","privacyMode":false,"background":"signal",
                     "scanIntervalMs":900,"scoreWeights":{}}}
        """;
        var problems3 = PayloadValidator.ValidateTacet(missingCollection);
        Assert.Contains(problems3, pr => pr.Contains("equippedLoadouts"));
    }
}
