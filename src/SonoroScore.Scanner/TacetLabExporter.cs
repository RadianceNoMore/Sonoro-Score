// © 2026 RadianceNoMore (Sonoro-Score, MIT).
// Tacet-Lab backup envelope reverse-engineered from Tacet-Lab
// (https://github.com/DJ12421/Tacet-Lab, GPL-3.0):
//   src/storage/database.ts → exportAccount() / validateAccount()
//   src/domain/types.ts → AccountDocument / Echo / AppSettings
//   src/game-data/core.ts → GAME_DATA_VERSION ('nanoka-3.6-catalog')
// Produces a schemaVersion-7 backup Tacet-Lab imports via top-bar
// Export/Restore → Settings & data. See NOTICES.md.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonoroScore.Scanner;

/// <summary>
/// 1-click Tacet-Lab backup exporter (<c>tacet-lab-backup.json</c>).
/// Primary (lossless) export path; GOOD export is a best-effort bridge.
/// </summary>
public static class TacetLabExporter
{
    public const int SchemaVersion = 7;

    /// <summary>Tacet-Lab <c>GAME_DATA_VERSION</c> (core.ts). Our catalog tracks nanoka 3.6.</summary>
    public const string TacetGameDataVersion = "nanoka-3.6-catalog";

    public const string DefaultFileName = "tacet-lab-backup.json";

    private sealed record TacetStatLine(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("value")] double Value);

    private sealed record TacetEcho(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("cost")] int Cost,
        [property: JsonPropertyName("rarity")] int Rarity,
        [property: JsonPropertyName("level")] int Level,
        [property: JsonPropertyName("sonata")] string Sonata,
        [property: JsonPropertyName("mainStat")] TacetStatLine MainStat,
        [property: JsonPropertyName("subStats")] List<TacetStatLine> SubStats,
        [property: JsonPropertyName("locked")] bool Locked,
        [property: JsonPropertyName("excluded")] bool Excluded,
        // Tacet's isEcho accepts `undefined` or a string for these - a JSON null FAILS
        // (found by running our export through Tacet's own validator, F-48). Omit when
        // unknown instead of writing null.
        [property: JsonPropertyName("equippedBy")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? EquippedBy,
        [property: JsonPropertyName("equippedByName")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? EquippedByName,
        [property: JsonPropertyName("createdAt")] long CreatedAt,
        [property: JsonPropertyName("source")] string Source);

    private sealed record TacetSettings(
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("uid")] string Uid,
        [property: JsonPropertyName("privacyMode")] bool PrivacyMode,
        [property: JsonPropertyName("background")] string Background,
        [property: JsonPropertyName("scanIntervalMs")] int ScanIntervalMs,
        [property: JsonPropertyName("roverGender")] string RoverGender,
        [property: JsonPropertyName("liveCharacterArt")] bool LiveCharacterArt,
        [property: JsonPropertyName("scoreWeights")] Dictionary<string, object> ScoreWeights,
        [property: JsonPropertyName("characterSubstatWeights")] Dictionary<string, object> CharacterSubstatWeights,
        [property: JsonPropertyName("characterEnergyRegenMinimums")] Dictionary<string, object> CharacterEnergyRegenMinimums);

    private sealed record TacetBackup(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("gameDataVersion")] string GameDataVersion,
        [property: JsonPropertyName("exportedAt")] string ExportedAt,
        [property: JsonPropertyName("echoes")] List<TacetEcho> Echoes,
        [property: JsonPropertyName("characters")] List<object> Characters,
        [property: JsonPropertyName("weapons")] List<object> Weapons,
        [property: JsonPropertyName("builds")] List<object> Builds,
        [property: JsonPropertyName("equippedLoadouts")] List<object> EquippedLoadouts,
        [property: JsonPropertyName("theorycraftBuilds")] List<object> TheorycraftBuilds,
        [property: JsonPropertyName("teams")] List<object> Teams,
        [property: JsonPropertyName("optimizerProfiles")] List<object> OptimizerProfiles,
        [property: JsonPropertyName("optimizerRuns")] List<object> OptimizerRuns,
        [property: JsonPropertyName("settings")] TacetSettings Settings);

    /// <summary>
    /// Build the backup JSON. Echoes without a known main stat are skipped
    /// (Tacet <c>isEcho</c> requires a valid mainStat); <paramref name="skipped"/>
    /// reports how many were dropped.
    /// </summary>
    public static string Export(IEnumerable<ExportableEcho> echoes, out int skipped)
    {
        var list = new List<TacetEcho>();
        skipped = 0;
        long baseMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int i = 0;

        foreach (var e in echoes)
        {
            string? mainKey = TacetStatKeys.ToTacetKey(e.MainStatKey);
            if (mainKey == null)
            {
                skipped++;
                continue;
            }

            // E-01/E-04: no silent defaults. A value the schema cannot accept means the
            // echo is SKIPPED, never laundered into a plausible one (old code turned a
            // bad cost into 1, a bad level into 0, a missing sonata into "").
            if (e.Cost is not (1 or 3 or 4)) { skipped++; continue; }
            if (e.Rarity is < 1 or > 5) { skipped++; continue; }
            if (e.Level is < 0 or > 25) { skipped++; continue; }
            if (string.IsNullOrWhiteSpace(e.Sonata)) { skipped++; continue; }

            var subs = new List<TacetStatLine>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (k, v) in e.Substats)
            {
                string? tk = TacetStatKeys.ToTacetKey(k);
                if (tk == null || !seen.Add(tk)) continue; // Tacet rejects duplicate substat keys
                if (subs.Count >= 5) break;
                subs.Add(new TacetStatLine(tk, Math.Round(v, 2)));
            }

            string safeName = new string(e.Name.Where(char.IsLetterOrDigit).ToArray());
            if (string.IsNullOrEmpty(safeName)) { skipped++; continue; }   // E-01: no "echo" placeholder
            string id = $"sonoro_{baseMs}_{i:000}_{safeName}";
            string equippedName = string.IsNullOrWhiteSpace(e.EquippedBy) ? "" : e.EquippedBy.Trim();

            list.Add(new TacetEcho(
                Id: id,
                Name: e.Name,
                Cost: e.Cost,
                Rarity: e.Rarity,
                Level: e.Level,
                Sonata: e.Sonata,
                MainStat: new TacetStatLine(mainKey, Math.Round(e.MainStatValue, 2)),
                SubStats: subs,
                Locked: false,
                Excluded: false,
                EquippedBy: null, // no Tacet character IDs in scanner; name links on import
                EquippedByName: string.IsNullOrEmpty(equippedName) ? null : equippedName,
                CreatedAt: baseMs + i,
                Source: "scan"));
            i++;
        }

        var backup = new TacetBackup(
            SchemaVersion: SchemaVersion,
            GameDataVersion: TacetGameDataVersion,
            ExportedAt: DateTime.UtcNow.ToString("o"),
            Echoes: list,
            Characters: [],
            Weapons: [],
            Builds: [],
            EquippedLoadouts: [],
            TheorycraftBuilds: [],
            Teams: [],
            OptimizerProfiles: [],
            OptimizerRuns: [],
            Settings: new TacetSettings(
                DisplayName: "Resonator",
                Uid: "",
                PrivacyMode: false,
                Background: "signal",
                ScanIntervalMs: 900,
                RoverGender: "male",
                LiveCharacterArt: true,
                ScoreWeights: new Dictionary<string, object>(),
                CharacterSubstatWeights: new Dictionary<string, object>(),
                CharacterEnergyRegenMinimums: new Dictionary<string, object>()));

        string json = JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true });

        // E-04: the bytes we are about to write must satisfy the schema Tacet-Lab
        // itself enforces (isEcho / validateAccount). Abort rather than write junk.
        var problems = PayloadValidator.ValidateTacet(json);
        if (problems.Count > 0) throw new ExportSchemaException("Tacet-Lab v7", problems);

        return json;
    }

    public static string Export(IEnumerable<ExportableEcho> echoes)
        => Export(echoes, out _);

    /// <summary>Export scan results directly (skips entries with unknown main stat).</summary>
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
        log?.Invoke($"[EXPORT] Tacet-Lab backup: {path} ({echoes.Count()} echoes, {skipped} skipped w/o main stat).");
        return path;
    }
}
