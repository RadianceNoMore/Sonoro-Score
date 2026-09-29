// Ac 2026 RadianceNoMore (Sonoro-Score, MIT).
// Bounded OCR digit-confusion table for roll correction (C-06). It is DATA, kept
// in ocr_confusions.json next to the exe, with a compiled fallback so a missing
// file never silently disables the mechanism.

using System.Text.Json;

namespace SonoroScore.Scanner;

public static class OcrConfusions
{
    /// <summary>Digit pairs tried in BOTH directions during the bounded search.</summary>
    public static IReadOnlyList<(char From, char To)> Substitutions => _subs.Value.Pairs;

    /// <summary>Whether a missing/extra decimal point is tried for percent keys.</summary>
    public static bool DecimalShift => _subs.Value.DecimalShift;

    public const string FileName = "ocr_confusions.json";

    private sealed record Table(List<(char From, char To)> Pairs, bool DecimalShift);

    // Conservative seed (TODO C-06). Entries carry an observation count so the
    // table can be reconciled with real accuracy_report.json evidence.
    private static readonly Table Default = new(
        [('1', '7'), ('0', '8'), ('3', '8'), ('5', '6'), ('6', '8')],
        true);

    private static readonly Lazy<Table> _subs = new(Load);

    private static Table Load()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, FileName);
            if (!File.Exists(path)) return Default;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var pairs = new List<(char, char)>();
            if (root.TryGetProperty("digitPairs", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    string? from = e.TryGetProperty("from", out var f) ? f.GetString() : null;
                    string? to = e.TryGetProperty("to", out var t) ? t.GetString() : null;
                    if (from is { Length: 1 } && to is { Length: 1 })
                    {
                        pairs.Add((from[0], to[0]));
                        pairs.Add((to[0], from[0]));   // symmetric
                    }
                }
            }

            bool decimalShift = !root.TryGetProperty("decimalShift", out var ds) || ds.ValueKind != JsonValueKind.False;

            return pairs.Count > 0 ? new Table(pairs, decimalShift) : Default;
        }
        catch
        {
            return Default;
        }
    }
}
