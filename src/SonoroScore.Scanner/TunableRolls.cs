namespace SonoroScore.Scanner;

/// <summary>
/// Tunable substat roll values — ported from Tacet-Lab tunable-rolls.ts (updated 2026-07-28).
/// </summary>
public static class TunableRolls
{
    private record Roll(float Value, float Probability);

    private static Roll[] Common(params (float v, float p)[] pairs)
        => pairs.Select(x => new Roll(x.v, x.p)).ToArray();

    private static readonly Dictionary<StatKey, Roll[]> Rolls = new()
    {
        [StatKey.Hp] = Common((320, 6.8f), (360, 7.77f), (390, 20.39f), (430, 24.27f),
                               (470, 17.48f), (510, 14.56f), (540, 5.83f), (580, 2.91f)),
        [StatKey.Atk] = Common((30, 6.8f), (40, 52.43f), (50, 37.86f), (60, 2.91f)),
        [StatKey.Def] = Common((40, 14.56f), (50, 44.66f), (60, 32.04f), (70, 8.74f)),
        [StatKey.CritRate]     = Common((6.3f,23.33f),(6.9f,23.33f),(7.5f,23.33f),(8.1f,8),(8.7f,8),(9.3f,8),(9.9f,3),(10.5f,3)),
        [StatKey.CritDamage]   = Common((12.6f,23.33f),(13.8f,23.33f),(15,23.33f),(16.2f,8),(17.4f,8),(18.6f,8),(19.8f,3),(21,3)),
        [StatKey.EnergyRegen]  = Common((6.8f,6.8f),(7.6f,7.77f),(8.4f,20.39f),(9.2f,24.27f),(10,17.48f),(10.8f,14.56f),(11.6f,5.83f),(12.4f,2.91f)),
    };

    // All percent-based common substats share the same roll table
    private static readonly Roll[] CommonPercent = Common(
        (6.4f, 6.8f), (7.1f, 7.77f), (7.9f, 20.39f), (8.6f, 24.27f),
        (9.4f, 17.48f), (10.1f, 14.56f), (10.9f, 5.83f), (11.6f, 2.91f));

    // DEF% has its OWN table (8.1 .. 14.7) and is NOT the common percent table.
    // C-08 caught this against ../Tacet-Lab/src/game-data/tunable-rolls.ts: the
    // original port put DefPercent on CommonPercent, so real DEF% rolls such as
    // 8.1 and 10 were treated as non-rolls and tolerance-snapped to 7.9 / 10.1.
    private static readonly Roll[] DefPercentRolls = Common(
        (8.1f, 6.8f), (9f, 7.77f), (10f, 20.39f), (10.9f, 24.27f),
        (11.8f, 17.48f), (12.8f, 14.56f), (13.8f, 5.83f), (14.7f, 2.91f));

    private static Roll[]? GetRolls(StatKey key)
    {
        if (Rolls.TryGetValue(key, out var r)) return r;
        return key switch
        {
            StatKey.DefPercent => DefPercentRolls,
            StatKey.HpPercent or StatKey.AtkPercent
                or StatKey.BasicDamage or StatKey.HeavyDamage or StatKey.SkillDamage
                or StatKey.LiberationDamage => CommonPercent,
            _ => null,
        };
    }

    private static readonly float[] FlatTolerances = [3f]; // hp/atk/def: max(3, value*0.08)

    /// <summary>
    /// True when this key is a legal SUBSTAT: it has a tunable roll table.
    /// Data-driven from the tables above instead of a hand-typed list (C-07);
    /// element-DMG / healing keys have no roll table and are therefore rejected.
    /// </summary>
    public static bool IsValidSubstatKey(StatKey key) => GetRolls(key) != null;

    /// <summary>Returns the exact matching roll, or null.</summary>
    public static float? Exact(StatKey key, float value)
    {
        var rolls = GetRolls(key);
        if (rolls == null) return null;
        var match = rolls.FirstOrDefault(r => MathF.Abs(r.Value - value) < 0.001f);
        return match?.Value;
    }

    /// <summary>Returns the closest roll within tolerance, or null.</summary>
    public static float? Closest(StatKey key, float value)
    {
        var rolls = GetRolls(key);
        if (rolls == null || !float.IsFinite(value)) return null;
        var closest = rolls.MinBy(r => MathF.Abs(r.Value - value))!;
        float tol = (key is StatKey.Hp or StatKey.Atk or StatKey.Def)
            ? MathF.Max(3f, closest.Value * 0.08f)
            : 0.35f;
        return MathF.Abs(closest.Value - value) <= tol ? closest.Value : null;
    }

    /// <summary>
    /// Discriminated roll resolution (C-05). Tolerance-based snapping is GONE: a
    /// value is corrected only when the bounded confusion search (C-06) yields
    /// EXACTLY ONE legal roll; otherwise it stays a proposal for review.
    /// </summary>
    public static RollResolution ResolveDetailed(StatKey key, float value)
    {
        var rolls = GetRolls(key);
        if (rolls == null) return new RollResolution(RollState.NotARoll, null, []);

        var exact = Exact(key, value);
        if (exact.HasValue) return new RollResolution(RollState.Exact, exact.Value, [exact.Value]);

        var candidates = ConfusionCandidates(key, value);
        if (candidates.Count == 1) return new RollResolution(RollState.Corrected, candidates[0], candidates);
        if (candidates.Count > 1) return new RollResolution(RollState.Ambiguous, null, candidates);
        return new RollResolution(RollState.NotARoll, null, []);
    }

    /// <summary>Legal rolls for a key, ascending (empty when it has no roll table).</summary>
    public static IReadOnlyList<float> RollsFor(StatKey key)
        => GetRolls(key)?.Select(r => r.Value).OrderBy(v => v).ToArray() ?? [];

    /// <summary>
    /// Backward-compatible tuple wrapper over <see cref="ResolveDetailed"/>: only
    /// Exact/Corrected carry a value; everything else is (null, 0.50).
    /// </summary>
    public static (float? Value, float Confidence) Resolve(StatKey key, float value)
    {
        var r = ResolveDetailed(key, value);
        return r.State switch
        {
            RollState.Exact => (r.Value, 0.92f),
            RollState.Corrected => (r.Value, 0.85f),
            _ => (null, 0.50f),
        };
    }

    /// <summary>Where a value landed against the roll table (C-05).</summary>
    public enum RollState { Exact, Corrected, Ambiguous, NotARoll }

    public sealed record RollResolution(RollState State, float? Value, IReadOnlyList<float> Candidates)
    {
        /// <summary>Only an unambiguous result may be exported (E-02).</summary>
        public bool IsUsable => State is RollState.Exact or RollState.Corrected;
    }

    /// <summary>
    /// Bounded OCR-confusion search (C-06): at most one substitution drawn from the
    /// (data) confusion table, plus a decimal shift for percent keys, collected as
    /// the set of DISTINCT legal rolls it can reach.
    /// </summary>
    private static List<float> ConfusionCandidates(StatKey key, float value)
    {
        var found = new List<float>();
        if (!float.IsFinite(value)) return found;

        void TryAdd(float v)
        {
            var exact = Exact(key, v);
            if (exact.HasValue && !found.Contains(exact.Value)) found.Add(exact.Value);
        }

        string raw = value.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);

        foreach (var (fromCh, toCh) in OcrConfusions.Substitutions)
        {
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] != fromCh) continue;
                char[] swapped = raw.ToCharArray();
                swapped[i] = toCh;
                if (float.TryParse(new string(swapped), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float v))
                    TryAdd(v);
            }
        }

        // A missing/extra decimal point only makes sense for percent-typed stats.
        if (OcrConfusions.DecimalShift && StatParser.IsPercentKey(key))
        {
            TryAdd(value / 10f);
            TryAdd(value * 10f);
        }

        found.Sort();
        return found;
    }
}
