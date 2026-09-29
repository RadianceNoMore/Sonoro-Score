// © 2026 RadianceNoMore (Sonoro-Score, MIT).
// Tacet-Lab envelope (schemaVersion 7 / AccountDocument) reverse-engineered from
// Tacet-Lab (https://github.com/DJ12421/Tacet-Lab, GPL-3.0) src/storage/database.ts
// (exportAccount) + src/domain/types.ts. See NOTICES.md.

using System.Text.Json.Serialization;

namespace SonoroScore.Scanner;

/// <summary>E-02 export strictness. Strict = anything degraded is quarantined.</summary>
public enum ExportPolicy { Strict, IncludePartial }

/// <summary>E-01: outcome of an export attempt - the DTO, or why it was refused.</summary>
public sealed record ExportOutcome(ExportableEcho? Echo, IReadOnlyList<string> Rejected)
{
    public bool Ok => Echo != null;
}

/// <summary>E-03: one quarantined echo with the reasons it was refused.</summary>
public sealed record ExportRejection(string ImageFile, IReadOnlyList<string> Reasons);

/// <summary>
/// Lossless-ish echo DTO shared by both exporters.
/// Build via <see cref="FromScanResult"/> (CLI/debugger) or
/// <c>EchoReviewItem → ExportableEcho</c> mapping in the GUI.
/// NOTE: the second main stat lives in SS verified JSON only — Tacet-Lab and
/// GOOD schemas carry a single main stat, so exporters keep the primary.
/// </summary>
public sealed record ExportableEcho(
    string Name,
    int Cost,
    int Rarity,
    int Level,
    string Sonata,
    string EquippedBy,
    string MainStatKey,
    float MainStatValue,
    IReadOnlyList<(string Key, float Value)> Substats)
{
    /// <summary>
    /// E-01/E-02: build an export DTO from a scan.
    ///
    /// Missing required fields and unusable substat rolls produce REJECTION REASONS -
    /// never a plausible default (no "UNKNOWN", no cost 1, no level 0). Rarity is
    /// exempt: it is a documented constant because the panel shows none (F-30).
    ///
    /// Under <see cref="ExportPolicy.Strict"/> any degradation quarantines the echo;
    /// under <see cref="ExportPolicy.IncludePartial"/> only unusable substat ROWS are
    /// dropped (never the whole echo), and the reasons are still reported.
    /// </summary>
    public static ExportOutcome FromScanResult(EchoScanResult scan, ExportPolicy policy = ExportPolicy.Strict)
    {
        var fatal = new List<string>();          // missing required fields
        var degradations = new List<string>();   // unusable rows / count shortfall

        string name = EchoReviewCompat.ExtractString(scan.EchoName?.Value, "");
        if (string.IsNullOrWhiteSpace(name) || name.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
            fatal.Add("name missing");

        int cost = EchoReviewCompat.ExtractInt(scan.Cost?.Value, 0);
        if (cost is not (1 or 3 or 4)) fatal.Add($"cost missing or invalid ({cost})");

        int level = EchoReviewCompat.ExtractInt(scan.Level?.Value, -1);
        if (level is < 0 or > 25) fatal.Add($"level missing or invalid ({level})");

        string sonata = EchoReviewCompat.ExtractString(scan.Sonata?.Value, "");
        if (string.IsNullOrWhiteSpace(sonata)) fatal.Add("sonata missing");

        string mainKey = EchoReviewCompat.ExtractString(scan.MainStatKey?.Value, "");
        if (string.IsNullOrWhiteSpace(mainKey) || mainKey.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            fatal.Add("main stat missing"); // Tacet isEcho requires a valid mainStat

        float mainVal = EchoReviewCompat.ExtractFloat(scan.MainStatValue?.Value, 0f);
        if (mainVal <= 0) fatal.Add("main stat value missing");

        // Rarity: documented constant - never a read value, never a rejection reason.
        const int rarity = 5;

        var subs = new List<(string Key, float Value)>();
        foreach (var s in scan.Substats)
        {
            if (string.IsNullOrWhiteSpace(s.Key) || s.Key.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                continue;

            if (s.SnappedValue == null)
            {
                degradations.Add($"substat '{s.Key}' has no accepted roll (raw {s.Value})");
                continue; // E-02: only accepted data may be exported
            }
            subs.Add((s.Key, s.SnappedValue.Value));
        }

        int expected = EchoRules.ExpectedSubstatCount(level < 0 ? 0 : level);
        if (subs.Count < expected)
            degradations.Add($"substat count {subs.Count} is below the level rule ({expected})");

        var reasons = fatal.Concat(degradations).ToList();
        bool blocked = fatal.Count > 0 || (policy == ExportPolicy.Strict && degradations.Count > 0);
        if (blocked) return new ExportOutcome(null, reasons);

        string equipped = EchoReviewCompat.ExtractString(scan.EquippedBy?.Value, "");
        return new ExportOutcome(
            new ExportableEcho(name, cost, rarity, level, sonata, equipped, mainKey, mainVal, subs),
            reasons);
    }
}

/// <summary>JsonElement-safe scalar extraction (mirrors EchoReviewItem helpers without GUI dependency).</summary>
internal static class EchoReviewCompat
{
    public static string ExtractString(object? val, string defaultVal = "")
    {
        if (val == null) return defaultVal;
        if (val is string s) return string.IsNullOrWhiteSpace(s) ? defaultVal : s;
        if (val is System.Text.Json.JsonElement je)
        {
            if (je.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var str = je.GetString();
                return string.IsNullOrWhiteSpace(str) ? defaultVal : str;
            }
            if (je.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined)
                return defaultVal;
            return je.ToString();
        }
        return val.ToString() ?? defaultVal;
    }

    public static int ExtractInt(object? val, int defaultVal = 0)
    {
        if (val == null) return defaultVal;
        if (val is int i) return i;
        if (val is long l) return (int)l;
        if (val is System.Text.Json.JsonElement je)
        {
            if (je.TryGetInt32(out int ji)) return ji;
            if (int.TryParse(je.GetString(), out int js)) return js;
        }
        if (int.TryParse(val.ToString(), out int parsed)) return parsed;
        return defaultVal;
    }

    public static float ExtractFloat(object? val, float defaultVal = 0f)
    {
        if (val == null) return defaultVal;
        if (val is float f) return f;
        if (val is double d) return (float)d;
        if (val is int i) return i;
        if (val is System.Text.Json.JsonElement je)
        {
            if (je.TryGetSingle(out float jf)) return jf;
            if (je.TryGetDouble(out double jd)) return (float)jd;
            if (float.TryParse(je.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float js)) return js;
        }
        if (float.TryParse(val.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float parsed)) return parsed;
        return defaultVal;
    }
}

/// <summary>Shared C# StatKey → Tacet camelCase key mapping.</summary>
internal static class TacetStatKeys
{
    public static string? ToTacetKey(string csharpKey)
    {
        if (string.IsNullOrWhiteSpace(csharpKey)) return null;
        string k = csharpKey.Trim();
        if (k.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return null;
        // Hp → hp, HpPercent → hpPercent, CritRate → critRate, …
        return char.ToLowerInvariant(k[0]) + k.Substring(1);
    }
}
