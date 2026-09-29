// Ac 2026 RadianceNoMore (Sonoro-Score, MIT).
// A-07: per-field error taxonomy, confusion pairs and corpus metrics, shared by
// the CLI (`--truth`) and the fixture corpus test so both emit the SAME
// accuracy_report.json.

using System.Text.Json;

namespace SonoroScore.Scanner;

public static class AccuracyReport
{
    /// <summary>One scored field of one fixture, with the evidence behind it.</summary>
    public sealed record FieldEntry(
        string Fixture,
        string Field,
        string? Expected,
        string? Actual,
        bool Match,
        string? RawOcr,
        float? Confidence,
        string Provenance);

    /// <summary>Build per-field entries for one scored fixture.</summary>
    public static IEnumerable<FieldEntry> Entries(EchoFixture fixture, EchoScanResult scan, EchoAccuracyReport report)
    {
        foreach (var f in report.Fields)
        {
            yield return new FieldEntry(
                fixture.SourceImage,
                f.Field,
                f.Expected,
                f.Actual,
                f.Match,
                RawFor(scan, f.Field),
                ConfidenceFor(scan, f.Field),
                ProvenanceFor(scan, f.Field));
        }
    }

    /// <summary>
    /// D-06: real per-field provenance from the scan's diagnostics
    /// ("engine/PSM/preprocess/attempt"), or "not-recorded" when a field has none.
    /// </summary>
    public static string ProvenanceFor(EchoScanResult scan, string field)
    {
        var d = scan.Diagnostics.FirstOrDefault(x => x.Field == field);
        if (d == null) return "not-recorded";
        return $"{d.Engine ?? "?"}/{d.Psm ?? "?"}/{d.Preprocess ?? "?"}/attempt{d.Attempt}";
    }

    public static string? RawFor(EchoScanResult scan, string field) => field switch
    {
        "identity" => scan.RawNameOcr,
        "mainStat" => scan.RawMainStatOcr,
        "secondMainStat" => scan.RawSecondMainStatOcr,
        _ when field.StartsWith("substat", StringComparison.Ordinal) => scan.RawSubstatsOcr,
        _ => null,
    };

    public static float? ConfidenceFor(EchoScanResult scan, string field) => field switch
    {
        "identity" => scan.EchoName?.Confidence,
        "cost" => scan.Cost?.Confidence,
        "rarity" => scan.Rarity?.Confidence,
        "level" => scan.Level?.Confidence,
        "sonata" => scan.Sonata?.Confidence,
        "mainStat" => scan.MainStatValue?.Confidence,
        "secondMainStat" => scan.SecondMainStatValue?.Confidence,
        _ when field.StartsWith("substat-extra", StringComparison.Ordinal) => null,
        _ when field.StartsWith("substat", StringComparison.Ordinal) => SubstatConfidence(scan, field),
        _ => null,
    };

    private static float? SubstatConfidence(EchoScanResult scan, string field)
    {
        if (!int.TryParse(field.AsSpan("substat".Length), out int n)) return null;
        return n >= 1 && n <= scan.Substats.Count ? scan.Substats[n - 1].Confidence : null;
    }

    // ---------------------------------------------------------------- metrics

    /// <summary>Substat recall/precision for one fixture, derived from its field scores.</summary>
    public static (int Matched, int Expected, int Extra) SubstatCounts(EchoAccuracyReport report)
    {
        int matched = 0, expected = 0, extra = 0;
        foreach (var f in report.Fields)
        {
            if (f.Field.StartsWith("substat-extra", StringComparison.Ordinal)) extra++;
            else if (f.Field.StartsWith("substat", StringComparison.Ordinal))
            {
                expected++;
                if (f.Match) matched++;
            }
        }
        return (matched, expected, extra);
    }

    /// <summary>Every field of this fixture matched (nothing extra, nothing missing).</summary>
    public static bool IsExactMatch(EchoAccuracyReport report)
        => report.Fields.Count > 0 && report.Fields.All(f => f.Match);

    // ---------------------------------------------------------------- output

    /// <summary>One confidence bucket of the D-02 calibration harness.</summary>
    public sealed record CalibrationBucket(double Lower, double Upper, int Count, int Matched)
    {
        public double Rate => Count == 0 ? 0 : (double)Matched / Count;
    }

    /// <summary>
    /// D-02 calibration data: confidence bucket vs empirical match rate. The A9 gate
    /// requires this to be monotonically increasing (and measured on VERIFIED
    /// fixtures); until A-02 lands it is a preview over unverified fixtures.
    /// </summary>
    public static IReadOnlyList<CalibrationBucket> Calibration(IEnumerable<FieldEntry> entries)
    {
        var withConf = entries.Where(e => e.Confidence.HasValue).ToList();
        var buckets = new List<CalibrationBucket>();
        for (int i = 0; i < 10; i++)
        {
            double lo = i / 10.0, hi = (i + 1) / 10.0;
            bool last = i == 9;
            var inBucket = withConf
                .Where(e => e.Confidence!.Value >= lo && (last ? e.Confidence!.Value <= hi : e.Confidence!.Value < hi))
                .ToList();
            buckets.Add(new CalibrationBucket(lo, hi, inBucket.Count, inBucket.Count(e => e.Match)));
        }
        return buckets;
    }

    /// <summary>Most frequent expected→actual mistakes, deterministic ordering.</summary>
    public static (string Expected, string Actual, int Count)[] TopConfusions(
        IEnumerable<FieldEntry> entries, int top = 5)
        => entries
            .Where(e => !e.Match && e.Expected != null && e.Actual != null)
            .GroupBy(e => (e.Expected, e.Actual))
            .Select(g => (g.Key.Expected!, g.Key.Actual!, g.Count()))
            .OrderByDescending(x => x.Item3)
            .ThenBy(x => x.Item1, StringComparer.Ordinal)
            .ThenBy(x => x.Item2, StringComparer.Ordinal)
            .Take(top)
            .ToArray();

    /// <summary>C-03 cost accounting: per-fixture per-slot retry counts.</summary>
    public sealed record RetryStat(string Fixture, int Retries, int Recovered, string Slots);

    public static string ToJson(IReadOnlyList<FieldEntry> entries, string note,
                                IReadOnlyList<RetryStat>? retries = null)
    {
        var payload = new
        {
            GeneratedAt = DateTime.UtcNow,
            Note = note,
            ProvenanceNote = "per-field provenance comes from EchoScanResult.Diagnostics " +
                             "(engine / PSM / preprocess / attempt) - D-06.",
            TotalFields = entries.Count,
            CalibrationNote = "D-02 harness: bucket vs empirical match rate. A9 requires monotone " +
                              "buckets on VERIFIED fixtures; with none verified yet this is a preview " +
                              "over unverified fixtures and is not gate-valid.",
            Calibration = Calibration(entries),
            RetryTotal = retries?.Sum(r => r.Retries) ?? 0,
            Retries = retries?.ToList() ?? [],
            FailedFields = entries.Where(e => !e.Match)
                .Select(e => new { e.Fixture, e.Field, e.Expected, e.Actual, e.RawOcr, e.Confidence, e.Provenance })
                .ToList(),
            Fields = entries.ToList(),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}
