// © 2026 RadianceNoMore (Sonoro-Score, MIT).
// F-38: pin the adaptive-threshold retry. On these three captures from the 300-image
// session the GLOBAL Otsu pass swallowed the substat labels ("ckDMG Bonus 9.4%", bare
// "12.8%"), leaving 1 of 5 rows. The local-threshold retry must recover the full set.
//
// Expectations are the fields the OWNER verified (stats + sonata). Name / cost / level
// were never verified for these captures and are deliberately NOT asserted (TODO A-03).

using System.Drawing;
using SonoroScore.Scanner;
using Xunit;

namespace SonoroScore.Scanner.Tests;

public sealed class F38BlockRetryTests
{
    private sealed record Case(
        string File,
        string Sonata,
        string MainKey,
        float MainValue,
        string SecondKey,
        float SecondValue,
        (string Key, float Value)[] Substats);

    private static readonly Case[] Cases =
    [
        new("f38_idx042.png", "Thread of Severed Fate", "HavocDamage", 30f, "Atk", 100f,
            [("HeavyDamage", 9.4f), ("DefPercent", 12.8f), ("LiberationDamage", 9.4f), ("Hp", 430f), ("CritRate", 9.3f)]),
        new("f38_idx043.png", "Thread of Severed Fate", "HavocDamage", 30f, "Atk", 100f,
            [("SkillDamage", 8.6f), ("CritRate", 8.1f), ("HeavyDamage", 7.1f), ("Hp", 470f), ("LiberationDamage", 8.6f)]),
        // idx085's main stat was recorded as FusionDamage in the owner's file, but the
        // panel reads "Havoc DMG Bonus" (two OCR passes + a labelled side-by-side against
        // the owner-verified Fusion capture idx091 at 6x). Reported as a file-side slip.
        new("f38_idx085.png", "Havoc Eclipse", "HavocDamage", 25.2f, "Atk", 84f,
            [("DefPercent", 10f), ("BasicDamage", 7.1f), ("CritDamage", 12.6f), ("EnergyRegen", 10f)]),
    ];

    [Fact]
    public async Task AdaptiveRetry_RecoversSubstatBlocksTheGlobalPassLoses()
    {
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures", "f38");
        Assert.True(Directory.Exists(dir), $"F-38 fixture dir missing: {dir}");
        string catalogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "fixtures", "catalog", "echo_catalog.json");
        EchoCatalogEntry[] catalog = await GameDatabase.LoadFromFileAsync(catalogPath);
        var recognizer = new EchoRecognizer(catalog);

        var failures = new List<string>();

        foreach (var c in Cases)
        {
            Bitmap panel = new Bitmap(Path.Combine(dir, c.File));
            EchoScanResult scan = await recognizer.RecognizePanelAsync(panel, c.File);

            if (scan.Sonata?.Value as string != c.Sonata)
                failures.Add($"{c.File}: sonata expected '{c.Sonata}' got '{scan.Sonata?.Value}'");

            if (scan.MainStatKey?.Value as string != c.MainKey)
                failures.Add($"{c.File}: main key expected '{c.MainKey}' got '{scan.MainStatKey?.Value}'");
            else if (Math.Abs(Convert.ToSingle(scan.MainStatValue!.Value) - c.MainValue) > 0.051f)
                failures.Add($"{c.File}: main value expected {c.MainValue} got {scan.MainStatValue!.Value}");

            if (scan.SecondMainStatKey?.Value as string != c.SecondKey)
                failures.Add($"{c.File}: second key expected '{c.SecondKey}' got '{scan.SecondMainStatKey?.Value}'");
            else if (Math.Abs(Convert.ToSingle(scan.SecondMainStatValue!.Value) - c.SecondValue) > 0.51f)
                failures.Add($"{c.File}: second value expected {c.SecondValue} got {scan.SecondMainStatValue!.Value}");

            var got = scan.Substats
                .Where(s => s.SnappedValue != null)
                .Select(s => (s.Key, Value: s.SnappedValue!.Value))
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .ToList();
            var want = c.Substats.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();

            if (got.Count != want.Count)
            {
                failures.Add($"{c.File}: {want.Count} accepted substat(s) expected, got {got.Count}: " +
                             string.Join(", ", got.Select(g => $"{g.Key}={g.Value}")));
                continue;
            }
            for (int i = 0; i < want.Count; i++)
            {
                if (want[i].Key != got[i].Key || Math.Abs(want[i].Value - got[i].Value) > 0.051f)
                    failures.Add($"{c.File}: substat[{i}] expected {want[i].Key}={want[i].Value} got {got[i].Key}={got[i].Value}");
            }
        }

        Assert.True(failures.Count == 0, "F-38 recovery failures:\n" + string.Join("\n", failures));
    }
}
