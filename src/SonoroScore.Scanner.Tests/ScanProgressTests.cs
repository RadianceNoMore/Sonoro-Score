// © 2026 RadianceNoMore (Sonoro-Score, MIT).
// ScanDirectoryAsync contract for the new GUI: one progress report per image, in
// enumeration order, ending with Done == Total. Recognition behaviour is the same
// pipeline the CLI drives, so the fixture panels give real output.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SonoroScore.Scanner;
using Xunit;

namespace SonoroScore.Scanner.Tests;

public sealed class ScanProgressTests
{
    [Fact]
    public async Task ScanDirectory_ReportsOncePerImage_EndingAtTotal()
    {
        string src = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures", "echoes", "english-1080p");
        string[] files = Directory.GetFiles(src, "*.png").OrderBy(f => f).Take(2).ToArray();
        Assert.Equal(2, files.Length);

        string tmp = Path.Combine(Path.GetTempPath(), "sonoro_progress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            foreach (string f in files)
                File.Copy(f, Path.Combine(tmp, Path.GetFileName(f)), overwrite: true);

            string catalogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "fixtures", "catalog", "echo_catalog.json");
            EchoCatalogEntry[] catalog = await GameDatabase.LoadFromFileAsync(catalogPath);
            var recognizer = new EchoRecognizer(catalog);

            var seen = new List<ScanProgress>();
            List<EchoScanResult> results = await recognizer.ScanDirectoryAsync(
                tmp, new InlineProgress<ScanProgress>(seen.Add));

            Assert.Equal(2, results.Count);
            Assert.Equal(2, seen.Count);
            Assert.All(seen, p => Assert.Equal(2, p.Total));
            Assert.Equal(1, seen[0].Done);
            Assert.Equal(2, seen[^1].Done);
            Assert.Equal(seen.Count, seen[^1].Done);
            Assert.All(seen, p => Assert.NotNull(p.Result));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
