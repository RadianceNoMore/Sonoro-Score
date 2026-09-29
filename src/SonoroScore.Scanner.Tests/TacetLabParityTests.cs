// Ac 2026 RadianceNoMore (Sonoro-Score, MIT).
// C-08: the port's roll tables must match ../Tacet-Lab/src/game-data/tunable-rolls.ts.
// Skips with a message when the sibling repo is absent (rule 7: dotnet test must be
// green on a clean checkout without it).

using System.Text.RegularExpressions;
using SonoroScore.Scanner;
using Xunit;
using Xunit.Abstractions;

namespace SonoroScore.Scanner.Tests;

public sealed class TacetLabParityTests
{
    private readonly ITestOutputHelper _log;

    public TacetLabParityTests(ITestOutputHelper log) => _log = log;

    /// <summary>Locate the sibling repo relative to the test binaries.</summary>
    private static string? FindTunableRollsTs()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int up = 0; up < 8 && dir != null; up++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Tacet-Lab", "src", "game-data", "tunable-rolls.ts");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static Dictionary<string, List<float>> ParseTs(string text)
    {
        var result = new Dictionary<string, List<float>>(StringComparer.OrdinalIgnoreCase);

        // The shared "commonPercent" block.
        var common = Regex.Match(text, @"commonPercent\s*:\s*TunableRoll\[\]\s*=\s*\[(.*?)\]\.map",
            RegexOptions.Singleline);
        var commonValues = Regex.Matches(common.Groups[1].Value, @"\[\s*([\d.]+)\s*,")
            .Select(m => float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        // One entry per key inside the tunableRolls object.
        foreach (Match m in Regex.Matches(text, @"^\s*(\w+)\s*:\s*(\[\[.*?\]\]|commonPercent|commonPercent\s*,)",
                     RegexOptions.Multiline))
        {
            string key = m.Groups[1].Value;
            if (string.Equals(key, "commonPercent", StringComparison.OrdinalIgnoreCase)) continue;

            string rhs = m.Groups[2].Value;
            var values = rhs.Contains("commonPercent", StringComparison.OrdinalIgnoreCase)
                ? new List<float>(commonValues)
                : Regex.Matches(rhs, @"\[\s*([\d.]+)\s*,")
                      .Select(x => float.Parse(x.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
                      .ToList();

            if (values.Count > 0) result[key] = values;
        }

        return result;
    }

    [Fact]
    public void RollTables_MatchTacetLabSource()
    {
        string? path = FindTunableRollsTs();
        if (path == null)
        {
            _log.WriteLine("Sibling repo ../Tacet-Lab not found - parity check SKIPPED.");
            return;
        }

        var expected = ParseTs(File.ReadAllText(path));
        Assert.True(expected.Count >= 13, $"parsed only {expected.Count} keys from {path}");

        var mismatches = new List<string>();
        foreach (var (tsKey, tsValues) in expected)
        {
            if (!Enum.TryParse<StatKey>(tsKey, ignoreCase: true, out var key))
            {
                mismatches.Add($"{tsKey}: no matching StatKey in the port");
                continue;
            }

            var portValues = TunableRolls.RollsFor(key);
            string ts = string.Join(",", tsValues);
            string port = string.Join(",", portValues);
            if (ts != port) mismatches.Add($"{tsKey}: tacet=[{ts}] port=[{port}]");
        }

        Assert.True(mismatches.Count == 0,
            "Roll tables diverge from Tacet-Lab:\n  " + string.Join("\n  ", mismatches));
    }
}
