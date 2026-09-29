// © 2026 RadianceNoMore (Sonoro-Score, MIT).
// GOOD envelope follows Genshin Optimizer's GOOD spec
// (format:"GOOD", source, version 1|2|3, characters?, artifacts?, weapons?).
// WuWa→GOOD is a BEST-EFFORT bridge: Tacet-Lab backup is the primary lossless
// path. Unmappable WuWa stats/slots are preserved verbatim in wuwa* fields.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonoroScore.Scanner;

/// <summary>
/// 1-click GOOD-format exporter (<c>sonoro-good.json</c>).
/// Best-effort WuWa→GOOD bridge; prefer <see cref="TacetLabExporter"/> for Tacet-Lab.
/// </summary>
public static class GoodExporter
{
    public const string Source = "Sonoro-Score";
    public const int Version = 3;
    public const string DefaultFileName = "sonoro-good.json";

    private sealed record GoodSubstat(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("value")] double Value,
        [property: JsonPropertyName("wuwaKey")] string? WuwaKey = null);

    private sealed record GoodArtifact(
        [property: JsonPropertyName("setKey")] string SetKey,
        [property: JsonPropertyName("slotKey")] string SlotKey,
        [property: JsonPropertyName("level")] int Level,
        [property: JsonPropertyName("rarity")] int Rarity,
        [property: JsonPropertyName("mainStatKey")] string MainStatKey,
        [property: JsonPropertyName("location")] string Location,
        [property: JsonPropertyName("lock")] bool Lock,
        [property: JsonPropertyName("substats")] List<GoodSubstat> Substats,
        [property: JsonPropertyName("wuwaName")] string? WuwaName = null,
        [property: JsonPropertyName("wuwaSonata")] string? WuwaSonata = null,
        [property: JsonPropertyName("wuwaCost")] int? WuwaCost = null,
        [property: JsonPropertyName("wuwaLevel")] int? WuwaLevel = null,
        [property: JsonPropertyName("wuwaMainStatKey")] string? WuwaMainStatKey = null);

    private sealed record GoodDocument(
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("source")] string DocSource,
        [property: JsonPropertyName("version")] int DocVersion,
        [property: JsonPropertyName("characters")] List<object> Characters,
        [property: JsonPropertyName("artifacts")] List<GoodArtifact> Artifacts,
        [property: JsonPropertyName("weapons")] List<object> Weapons);

    /// <summary>
    /// WuWa main/DMG stat → GOOD stat key. Elemental mapping is approximate
    /// (documented): Fusion≈pyro, Glacio≈cryo, Aero≈anemo, Electro≈electro,
    /// Spectro≈geo, Havoc≈physical, Basic/Heavy/Skill/Liberation≈physical.
    /// The lossless WuWa key is always kept in wuwaKey/wuwaMainStatKey.
    /// </summary>
    public static string ToGoodStatKey(string csharpKey) => csharpKey switch
    {
        "Hp" => "hp",
        "HpPercent" => "hp_",
        "Atk" => "atk",
        "AtkPercent" => "atk_",
        "Def" => "def",
        "DefPercent" => "def_",
        "CritRate" => "cr",
        "CritDamage" => "cd",
        "EnergyRegen" => "enerRech",
        "HealingBonus" => "heal_",
        "ElectroDamage" => "electro_dmg_",
        "FusionDamage" => "pyro_dmg_",
        "GlacioDamage" => "cryo_dmg_",
        "AeroDamage" => "anemo_dmg_",
        "SpectroDamage" => "geo_dmg_",
        "HavocDamage" => "physical_dmg_",
        "BasicDamage" => "physical_dmg_",
        "HeavyDamage" => "physical_dmg_",
        "SkillDamage" => "physical_dmg_",
        "LiberationDamage" => "physical_dmg_",
        _ => "eleMas", // unknown → generic mastery placeholder; see wuwaKey
    };

    /// <summary>WuWa echo cost → GOOD slot (inventory may hold duplicates per slot).</summary>
    public static string ToGoodSlot(int cost) => cost switch
    {
        1 => "flower",
        3 => "sands",
        4 => "circlet",
        _ => "flower",
    };

    /// <summary>Sonata → GOOD setKey (WuWa extension: sanitized, verbatim in wuwaSonata).</summary>
    public static string ToGoodSetKey(string sonata)
    {
        if (string.IsNullOrWhiteSpace(sonata)) return "UnknownSet";
        string flat = new string(sonata.Where(c => char.IsLetterOrDigit(c)).ToArray());
        return string.IsNullOrEmpty(flat) ? "UnknownSet" : flat;
    }

    public static string Export(IEnumerable<ExportableEcho> echoes, out int skipped)
    {
        var artifacts = new List<GoodArtifact>();
        skipped = 0;

        foreach (var e in echoes)
        {
            if (string.IsNullOrWhiteSpace(e.MainStatKey) ||
                e.MainStatKey.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            var subs = new List<GoodSubstat>();
            foreach (var (k, v) in e.Substats)
            {
                if (string.IsNullOrWhiteSpace(k) || k.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    continue;
                string gk = ToGoodStatKey(k);
                subs.Add(new GoodSubstat(gk, v, WuwaKey: k));
                if (subs.Count >= 9) break; // GOOD totalRolls cap context; keep bounded
            }

            // E-01/E-04: skip rather than launder.
            if (e.Cost is not (1 or 3 or 4)) { skipped++; continue; }
            if (e.Rarity is < 1 or > 5) { skipped++; continue; }
            if (e.Level is < 0 or > 25) { skipped++; continue; }
            if (string.IsNullOrWhiteSpace(e.Sonata)) { skipped++; continue; }

            artifacts.Add(new GoodArtifact(
                SetKey: ToGoodSetKey(e.Sonata),
                SlotKey: ToGoodSlot(e.Cost),
                // GOOD levels are 0-20 by spec; the true WuWa level travels in wuwaLevel.
                Level: Math.Min(e.Level, 20),
                Rarity: e.Rarity,
                MainStatKey: ToGoodStatKey(e.MainStatKey),
                Location: e.EquippedBy ?? "",
                Lock: false,
                Substats: subs,
                WuwaName: e.Name,
                WuwaSonata: e.Sonata,
                WuwaCost: e.Cost,
                WuwaLevel: e.Level,
                WuwaMainStatKey: e.MainStatKey));
        }

        var doc = new GoodDocument(
            Format: "GOOD",
            DocSource: Source,
            DocVersion: Version,
            Characters: [],
            Artifacts: artifacts,
            Weapons: []);

        string json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });

        // E-04: self-check the payload against the GOOD v3 shape before writing.
        var problems = PayloadValidator.ValidateGood(json);
        if (problems.Count > 0) throw new ExportSchemaException("GOOD v3", problems);

        return json;
    }

    public static string Export(IEnumerable<ExportableEcho> echoes)
        => Export(echoes, out _);

    public static string ExportScans(IEnumerable<EchoScanResult> scans, out int skipped)
        => ExportScans(scans, ExportPolicy.Strict, out skipped, out _);

    /// <summary>
    /// E-01/E-03: export scans under an explicit policy and report exactly which
    /// echoes were refused and why.
    /// </summary>
    public static string ExportScans(IEnumerable<EchoScanResult> scans, ExportPolicy policy,
                                     out int skipped, out List<ExportRejection> rejected)
    {
        var models = new List<ExportableEcho>();
        rejected = new List<ExportRejection>();
        foreach (var s in scans)
        {
            var outcome = ExportableEcho.FromScanResult(s, policy);
            if (outcome.Echo == null)
                rejected.Add(new ExportRejection(s.ImageFile, outcome.Rejected));
            else
                models.Add(outcome.Echo);
        }
        string json = Export(models, out int skippedModels);
        skipped = rejected.Count + skippedModels;
        return json;
    }

    public static async Task<string> SaveAsync(
        IEnumerable<ExportableEcho> echoes,
        string path,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        string json = Export(echoes, out int skipped);
        await File.WriteAllTextAsync(path, json, ct);
        log?.Invoke($"[EXPORT] GOOD file: {path} ({echoes.Count()} echoes, {skipped} skipped w/o main stat).");
        return path;
    }
}
