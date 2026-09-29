using System;

namespace SonoroScore.Scanner;

/// <summary>
/// Pure helpers for the substat pipeline.
///
/// Rules of engagement (see the project notes):
///   - Never silently drop a real OCR row.
///   - A substat MAY legitimately share a main stat's key: an echo has two main
///     stats (primary + secondary) and the game allows substats to equal a main
///     stat. Key equality alone must therefore never discard a row; only an
///     exact duplicate (same key AND same value) of the primary main stat is
///     dropped.
/// </summary>
public static class SubstatSlotter
{
    /// <summary>
    /// True only when a parsed substat row is an exact duplicate of the echo's
    /// main stat: same key and (near-)identical value. A row that shares the
    /// main stat's key but carries a different value is a real, independent
    /// substat and must be kept (e.g. main = ATK% 30.0, substat = ATK% 7.9).
    /// </summary>
    public static bool IsMainStatDuplicate(ParsedStat? mainStat, StatKey key, float value)
        => mainStat is { } main
           && main.Key == key
           && Math.Abs(value - main.Value) < 0.05f;

    /// <summary>
    /// Header/footer text that must never be slotted as a substat row: excluded
    /// by pattern, not by luck (C-04).
    /// </summary>
    public static readonly string[] HeaderPatterns =
        ["echo skill", "sonata effect", "equipped by", "equipped"];

    public static bool IsHeaderLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.ToLowerInvariant();
        foreach (var p in HeaderPatterns)
            if (t.Contains(p, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Nearest slot centre and its distance for a line centre (C-04).</summary>
    public static (int Slot, double Distance) NearestSlot(double y, IReadOnlyList<double> slotCenters)
    {
        if (slotCenters.Count == 0) return (-1, double.PositiveInfinity);
        int best = 0;
        double bestDist = Math.Abs(y - slotCenters[0]);
        for (int i = 1; i < slotCenters.Count; i++)
        {
            double d = Math.Abs(y - slotCenters[i]);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return (best, bestDist);
    }

    /// <summary>
    /// How far a line may sit from a slot centre before it is treated as an
    /// orphan: 0.6 x the median slot pitch (C-04 / F-08).
    /// </summary>
    public static double MaxSlotDistance(IReadOnlyList<double> slotCenters)
    {
        if (slotCenters.Count < 2) return double.PositiveInfinity;
        var pitches = new List<double>();
        for (int i = 1; i < slotCenters.Count; i++)
            pitches.Add(Math.Abs(slotCenters[i] - slotCenters[i - 1]));
        pitches.Sort();
        return 0.6 * pitches[pitches.Count / 2];
    }
}
