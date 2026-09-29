// Ac 2026 RadianceNoMore (Sonoro-Score, MIT).
// Single source of truth for the game rules the scanner enforces (C-01).
// (The D-04 main-value / second-main tables will live here too.)

namespace SonoroScore.Scanner;

public static class EchoRules
{
    /// <summary>
    /// Expected substat count for an echo at <paramref name="level"/>: one substat
    /// per 5 levels, capped at 5 (C-01). Verified against the 25-fixture corpus,
    /// where every fixture satisfies it.
    /// </summary>
    public static int ExpectedSubstatCount(int level) => Math.Min(level / 5, 5);
}
