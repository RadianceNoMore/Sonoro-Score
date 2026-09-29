using System.Text.RegularExpressions;

namespace SonoroScore.Scanner;

/// <summary>
/// Levenshtein edit-distance fuzzy matcher — ported from Tacet-Lab's parser.ts.
/// </summary>
/// <summary>Fuzzy-lookup outcome, including ambiguity between near-tied candidates (B-01).</summary>
public sealed record MatchResult<T>(
    T? Entry, float Score, bool Ambiguous = false, T? RunnerUp = default, float RunnerUpScore = 0f);

public static class FuzzyMatcher
{
    /// <summary>Normalize to lowercase alphanumeric only (mirrors Tacet-Lab normalizedIdentity).</summary>
    public static string Normalize(string s)
        => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]", "");

    /// <summary>
    /// Levenshtein-based similarity in [0,1].
    /// Substring containment short-circuits to 1.0.
    /// </summary>
    public static float Similarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0f;
        if (a == b) return 1.0f;
        // Containment is evidence, not proof: never 1.0 (B-01 / F-01).
        if ((a.Contains(b) || b.Contains(a)) && Math.Min(a.Length, b.Length) >= 5)
        {
            string shorter = a.Length <= b.Length ? a : b;
            string longer = a.Length <= b.Length ? b : a;

            // Amendment to B-01: a fragment that aligns with the START or END of the
            // longer name is the wrapped-name signature ("Leviathan" in
            // "Reminiscence: Threnodian - Leviathan", "Chop Chop" in "Chop Chop:
            // Headless"). Boundary alignment is real evidence, so it scores a flat
            // 0.9 - still below an exact match, but above the accept threshold.
            if (longer.StartsWith(shorter, StringComparison.Ordinal) ||
                longer.EndsWith(shorter, StringComparison.Ordinal))
                return 0.9f;

            // Arbitrary containment only earns the share of the longer string it covers.
            return 0.9f * shorter.Length / longer.Length;
        }
        int dist = EditDistance(a, b);
        int maxLen = Math.Max(a.Length, b.Length);
        return maxLen == 0 ? 1f : 1f - (float)dist / maxLen;
    }

    /// <summary>
    /// Best-matching catalog entry under the strict B-01 rules:
    /// exact normalized equality wins immediately; containment never scores 1.0;
    /// queries shorter than 4 chars never match; ties break deterministically
    /// (score, then prefix/suffix, then length, then lexicographic); near-ties are
    /// reported as ambiguous instead of silently picking one.
    /// </summary>
    public static MatchResult<T> Match<T>(
        string query, IEnumerable<T> catalog, Func<T, string> nameSelector, float threshold = 0.68f)
    {
        string normQuery = Normalize(query);
        if (string.IsNullOrEmpty(normQuery)) return new MatchResult<T>(default, 0f);

        var scored = new List<(T Entry, string Norm, float Score)>();

        foreach (var entry in catalog)
        {
            string normName = Normalize(nameSelector(entry));
            if (normName.Length == 0) continue;

            // Rule 2: an exact normalized match wins outright.
            if (normName == normQuery)
                return new MatchResult<T>(entry, 1.0f);

            scored.Add((entry, normName, Similarity(normQuery, normName)));
        }

        // Rule 5: shorter queries cannot match unless they were exact (above).
        if (normQuery.Length < 4)
            return new MatchResult<T>(default, scored.Count > 0 ? scored.Max(s => s.Score) : 0f);

        if (scored.Count == 0) return new MatchResult<T>(default, 0f);

        // Rule 6: deterministic tie-break, never "first in list".
        scored.Sort((x, y) =>
        {
            int c = y.Score.CompareTo(x.Score);
            if (c != 0) return c;

            int px = IsPrefixOrSuffix(x.Norm, normQuery) ? 1 : 0;
            int py = IsPrefixOrSuffix(y.Norm, normQuery) ? 1 : 0;
            if (px != py) return py.CompareTo(px);

            c = y.Norm.Length.CompareTo(x.Norm.Length);
            if (c != 0) return c;

            return string.CompareOrdinal(x.Norm, y.Norm);
        });

        var best = scored[0];
        T? runnerUp = scored.Count > 1 ? scored[1].Entry : default;
        float runnerUpScore = scored.Count > 1 ? scored[1].Score : 0f;

        // Rule 7: near-tie between two distinct entries is ambiguous.
        bool ambiguous = scored.Count > 1
                         && !EqualityComparer<T>.Default.Equals(best.Entry, runnerUp)
                         && best.Score - runnerUpScore < 0.05f;

        return best.Score >= threshold
            ? new MatchResult<T>(best.Entry, best.Score, ambiguous, runnerUp, runnerUpScore)
            : new MatchResult<T>(default, best.Score, ambiguous, runnerUp, runnerUpScore);
    }

    private static bool IsPrefixOrSuffix(string normName, string normQuery)
        => normQuery.StartsWith(normName, StringComparison.Ordinal)
           || normQuery.EndsWith(normName, StringComparison.Ordinal)
           || normName.StartsWith(normQuery, StringComparison.Ordinal)
           || normName.EndsWith(normQuery, StringComparison.Ordinal);

    /// <summary>Backward-compatible tuple wrapper over <see cref="Match"/>.</summary>
    public static (T? Entry, float Score) ClosestMatch<T>(
        string query, IEnumerable<T> catalog, Func<T, string> nameSelector, float threshold = 0.68f)
    {
        var r = Match(query, catalog, nameSelector, threshold);
        return (r.Entry, r.Score);
    }

    private static int EditDistance(string s, string t)
    {
        int m = s.Length, n = t.Length;
        int[] prev = new int[n + 1], curr = new int[n + 1];
        for (int j = 0; j <= n; j++) prev[j] = j;
        for (int i = 1; i <= m; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= n; j++)
            {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[n];
    }
}
