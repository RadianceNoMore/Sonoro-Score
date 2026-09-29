// Ac 2026 RadianceNoMore (Sonoro-Score, MIT).
// E-04: validate the payloads WE build against the rules Tacet-Lab itself enforces,
// BEFORE anything is written. Rules mirrored from
//   ../Tacet-Lab/src/storage/database.ts      (validateAccount / isEcho)
//   ../Tacet-Lab/src/game-data/echo-main-stats.ts (maxSubStatsForLevel)
//   GOOD v3 artifact shape (setKey/slotKey/level/rarity/mainStatKey/substats).

using System.Text.Json;

namespace SonoroScore.Scanner;

/// <summary>Thrown when a built export payload violates its own schema (E-04).</summary>
public sealed class ExportSchemaException : Exception
{
    public IReadOnlyList<string> Problems { get; }

    public ExportSchemaException(string schema, IReadOnlyList<string> problems)
        : base($"{schema} payload failed its own schema self-check ({problems.Count} problem(s)): " +
               string.Join("; ", problems.Take(5)))
        => Problems = problems;
}

public static class PayloadValidator
{
    /// <summary>Tacet-Lab's <c>maxSubStatsForLevel</c>: max(0, min(5, floor(level/5))).</summary>
    public static int MaxSubStatsForLevel(int level) => Math.Max(0, Math.Min(5, level / 5));

    private static readonly string[] Sources = ["scan", "screenshot", "manual", "import"];

    // ------------------------------------------------------------- Tacet-Lab v7

    /// <summary>Validate a Tacet-Lab backup payload. An empty list means valid.</summary>
    public static List<string> ValidateTacet(string json)
    {
        var problems = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (Exception ex) { return [$"envelope: not valid JSON ({ex.Message})"]; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ["envelope: root is not an object"];

            if (!root.TryGetProperty("schemaVersion", out var sv) || sv.ValueKind != JsonValueKind.Number
                || sv.GetInt32() is < 1 or > 7)
                problems.Add("envelope: schemaVersion must be 1..7");

            RequireString(problems, root, "envelope", "gameDataVersion");
            RequireString(problems, root, "envelope", "exportedAt");

            if (!root.TryGetProperty("echoes", out var echoes) || echoes.ValueKind != JsonValueKind.Array)
                return problems.Count > 0 ? problems : ["envelope: echoes must be an array"];

            int i = -1;
            foreach (var e in echoes.EnumerateArray())
            {
                i++;
                string where = $"echoes[{i}]";
                if (e.ValueKind != JsonValueKind.Object) { problems.Add($"{where}: not an object"); continue; }

                RequireString(problems, e, where, "id");
                RequireString(problems, e, where, "name");

                if (!e.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Number
                    || cost.GetInt32() is not (1 or 3 or 4))
                    problems.Add($"{where}: cost must be 1, 3 or 4");

                if (!e.TryGetProperty("rarity", out var rarity) || rarity.ValueKind != JsonValueKind.Number
                    || rarity.GetInt32() is < 1 or > 5)
                    problems.Add($"{where}: rarity must be 1..5");

                int level = -1;
                if (!e.TryGetProperty("level", out var lv) || lv.ValueKind != JsonValueKind.Number
                    || !lv.TryGetInt32(out level) || level is < 0 or > 25)
                    problems.Add($"{where}: level must be 0..25");

                RequireString(problems, e, where, "sonata");
                ValidateStatLine(problems, e, where, "mainStat");

                if (!e.TryGetProperty("subStats", out var subs) || subs.ValueKind != JsonValueKind.Array)
                {
                    problems.Add($"{where}: subStats must be an array");
                }
                else
                {
                    int max = level >= 0 ? MaxSubStatsForLevel(level) : 5;
                    if (subs.GetArrayLength() > max)
                        problems.Add($"{where}: subStats {subs.GetArrayLength()} exceeds " +
                                     $"maxSubStatsForLevel({level}) = {max}");
                    int k = 0;
                    foreach (var s in subs.EnumerateArray())
                        ValidateStatLine(problems, s, $"{where}.subStats[{k++}]", null);
                }

                RequireBool(problems, e, where, "locked");
                RequireBool(problems, e, where, "excluded");

                if (!e.TryGetProperty("createdAt", out var ca) || ca.ValueKind != JsonValueKind.Number)
                    problems.Add($"{where}: createdAt must be a number");

                if (!e.TryGetProperty("source", out var src) || src.ValueKind != JsonValueKind.String
                    || !Sources.Contains(src.GetString()))
                    problems.Add($"{where}: source must be one of {string.Join('/', Sources)}");
            }
        }

        return problems;
    }

    // ----------------------------------------------------------------- GOOD v3

    /// <summary>Validate a GOOD v3 document. An empty list means valid.</summary>
    public static List<string> ValidateGood(string json)
    {
        var problems = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (Exception ex) { return [$"envelope: not valid JSON ({ex.Message})"]; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ["envelope: root is not an object"];

            RequireString(problems, root, "envelope", "format");
            RequireString(problems, root, "envelope", "source");

            if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 3)
                problems.Add("envelope: version must be 3");

            if (!root.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array)
                return problems.Count > 0 ? problems : ["envelope: artifacts must be an array"];

            int i = -1;
            foreach (var a in artifacts.EnumerateArray())
            {
                i++;
                string where = $"artifacts[{i}]";
                if (a.ValueKind != JsonValueKind.Object) { problems.Add($"{where}: not an object"); continue; }

                RequireString(problems, a, where, "setKey");
                RequireString(problems, a, where, "slotKey");
                RequireString(problems, a, where, "mainStatKey");

                if (!a.TryGetProperty("level", out var lv) || lv.ValueKind != JsonValueKind.Number
                    || lv.GetInt32() is < 0 or > 25)
                    problems.Add($"{where}: level must be 0..25");

                if (!a.TryGetProperty("rarity", out var r) || r.ValueKind != JsonValueKind.Number
                    || r.GetInt32() is < 1 or > 5)
                    problems.Add($"{where}: rarity must be 1..5");

                RequireBool(problems, a, where, "lock");

                if (!a.TryGetProperty("substats", out var subs) || subs.ValueKind != JsonValueKind.Array)
                {
                    problems.Add($"{where}: substats must be an array");
                }
                else
                {
                    int k = 0;
                    foreach (var s in subs.EnumerateArray())
                        ValidateStatLine(problems, s, $"{where}.substats[{k++}]", null);
                }
            }
        }

        return problems;
    }

    // ---------------------------------------------------------------- helpers

    private static void RequireString(List<string> problems, JsonElement parent, string where, string name)
    {
        if (!parent.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(v.GetString()))
            problems.Add($"{where}: {name} must be a non-empty string");
    }

    private static void RequireBool(List<string> problems, JsonElement parent, string where, string name)
    {
        if (!parent.TryGetProperty(name, out var v)
            || v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            problems.Add($"{where}: {name} must be a boolean");
    }

    private static void ValidateStatLine(List<string> problems, JsonElement parent, string where, string? name)
    {
        JsonElement line = parent;
        if (name != null)
        {
            if (!parent.TryGetProperty(name, out line) || line.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"{where}: {name} must be an object");
                return;
            }
        }

        RequireString(problems, line, where, "key");

        if (!line.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Number
            || !double.IsFinite(v.GetDouble()))
            problems.Add($"{where}: value must be a finite number");
    }
}
