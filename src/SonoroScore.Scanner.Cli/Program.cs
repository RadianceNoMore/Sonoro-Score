using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SonoroScore.Scanner;

namespace SonoroScore.Scanner.Cli;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== SONORO-SCORE ECHO SCANNER & TEST SUITE RUNNER ===");
        ScannerConfig.ApplyEnvironment();

        // Parse arguments
        string? targetDir = null;
        int limit = int.MaxValue;
        bool forceRefresh = false;
        string? customOut = null;
        string? exportTacet = null;
        string? exportGood = null;
        bool updateSignatures = false;
        string? signatureUrl = null;
        string? truthDir = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--dir" && i + 1 < args.Length) targetDir = args[++i];
            else if (args[i] == "--limit" && i + 1 < args.Length && int.TryParse(args[++i], out int l)) limit = l;
            else if (args[i] == "--refresh") forceRefresh = true;
            else if (args[i] == "--out" && i + 1 < args.Length) customOut = args[++i];
            else if (args[i] == "--export-tacet" && i + 1 < args.Length) exportTacet = args[++i];
            else if (args[i] == "--export-tacet-auto") exportTacet = "__auto__";
            else if (args[i] == "--export-good" && i + 1 < args.Length) exportGood = args[++i];
            else if (args[i] == "--export-good-auto") exportGood = "__auto__";
            else if (args[i] == "--no-winocr-fallback") ScannerConfig.UseWindowsOcrFallback = false;
            else if (args[i] == "--name-engine" && i + 1 < args.Length)
                ScannerConfig.NameEngine = args[++i].ToLowerInvariant() switch
                {
                    "tesseract" => ScannerConfig.OcrEnginePreference.TesseractOnly,
                    "windows" => ScannerConfig.OcrEnginePreference.WindowsOnly,
                    _ => ScannerConfig.OcrEnginePreference.Auto,
                };
            else if (args[i] == "--update-signatures") updateSignatures = true;
            else if (args[i] == "--signature-url" && i + 1 < args.Length) signatureUrl = args[++i];
            else if (args[i] == "--truth" && i + 1 < args.Length) truthDir = args[++i];
            else if (args[i] == "--extract-sonata-templates" && i + 3 < args.Length)
                return await ExtractSonataTemplates(args[i + 1], args[i + 2], args[i + 3],
                    i + 4 < args.Length && int.TryParse(args[i + 4], out int m) ? m : 3);
            else if (args[i] == "--diag-sonata" && i + 1 < args.Length)
                return await DiagSonata(args[++i], i + 1 < args.Length && args[i + 1] == "--panel");
            else if (args[i] == "--diag-tess") return await DiagTesseract();
            else if (args[i] == "--dump-lines" && i + 1 < args.Length) return await DumpLines(args[++i]);
            else if (args[i] == "--dump-panel" && i + 2 < args.Length) return DumpPanel(args[++i], args[++i]);
            else if (args[i] == "--diag-name-collisions")
                return await DiagNameCollisions(i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null);
        }

        // Default test suite location
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
        {
            string defaultSession = @"C:\Users\Tina_\Documents\PlayingWithRepo\Sonoro-Score\publish\AlephalSonata\aleph_images\session_20260927_204453";
            if (Directory.Exists(defaultSession))
            {
                targetDir = defaultSession;
            }
            else
            {
                string baseImages = @"C:\Users\Tina_\Documents\PlayingWithRepo\Sonoro-Score\publish\AlephalSonata\aleph_images";
                if (Directory.Exists(baseImages))
                {
                    var dirs = Directory.GetDirectories(baseImages);
                    if (dirs.Length > 0) targetDir = dirs[0];
                }
            }
        }

        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Target directory not found: {targetDir}");
            Console.ResetColor();
            return 1;
        }

        Console.WriteLine($"Target directory: {targetDir}");
        Console.WriteLine($"OCR mode: {(ScannerConfig.UseWindowsOcrFallback ? "Tesseract+WinOcr-fallback" : "Tesseract-only (QA)")}");

        // 0. Sonata signature version check (+ optional refresh, Priority 4)
        if (updateSignatures)
        {
            bool ok = await SonataSignatureMatcher.EnsureUpdatedAsync(
                url: signatureUrl, forceRefresh: true, log: m => Console.WriteLine(m));
            Console.WriteLine(ok ? "[SIG] Signatures ready." : "[SIG] Signature update failed; using local file.");
        }
        SonataSignatureMatcher.CheckVersion(m => Console.WriteLine(m));

        // 1. Load database (fetch from nanoka.cc or cache)
        EchoCatalogEntry[] catalog;
        try
        {
            Console.WriteLine("[DB] Checking/updating echo catalog...");
            catalog = await GameDatabase.LoadAsync(forceRefresh: forceRefresh, log: m => Console.WriteLine(m));
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[DB] Catalog active: {catalog.Length} echoes ready.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[DB] Failed to load catalog: {ex.Message}");
            Console.ResetColor();
            return 2;
        }

        // A-06: hand-truth mode scores field-level accuracy instead of coverage.
        if (!string.IsNullOrEmpty(truthDir))
            return await RunAccuracyModeAsync(truthDir, catalog, customOut);

        // 2. Discover images
        var images = Directory.GetFiles(targetDir, "echo_*.png")
            .OrderBy(f => f)
            .Take(limit)
            .ToArray();

        if (images.Length == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"No 'echo_*.png' images found in {targetDir}.");
            Console.ResetColor();
            return 0;
        }

        Console.WriteLine($"Discovered {images.Length} echo capture images. Starting OCR evaluation...");
        Console.WriteLine(new string('-', 70));

        var recognizer = new EchoRecognizer(catalog);
        var results = new System.Collections.Generic.List<EchoScanResult>();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int idx = 0;

        foreach (var imgPath in images)
        {
            idx++;
            var scan = await recognizer.RecognizeAsync(imgPath);
            results.Add(scan);

            string name = scan.EchoName?.Value as string ?? "UNKNOWN";
            string stat = scan.MainStatKey?.Value as string ?? "UNKNOWN";
            object? val = scan.MainStatValue?.Value;
            string stat2 = scan.SecondMainStatKey?.Value as string ?? "—";
            object? val2 = scan.SecondMainStatValue?.Value;
            string sonata = scan.Sonata?.Value as string ?? "UNKNOWN";
            string conf = scan.EchoName?.Confidence.ToString("F2") ?? "0.00";
            int subs = scan.Substats.Count;

            string status = scan.Errors.Count > 0 ? "FAIL" : (scan.NeedsReview ? "REVIEW" : "OK");
            var color = status switch
            {
                "OK" => ConsoleColor.Green,
                "REVIEW" => ConsoleColor.Yellow,
                _ => ConsoleColor.Red
            };

            Console.ForegroundColor = color;
            Console.Write($"[{idx,3}/{images.Length}] [{status,-7}] ");
            Console.ResetColor();
            Console.WriteLine($"{Path.GetFileName(imgPath)} -> {name} | Sonata: {sonata} | Main: {stat} {val} | Main2: {stat2} {val2} | Subs: {subs}");
        }

        stopwatch.Stop();

        // 3. Aggregate statistics
        int total = results.Count;
        int namesFound = results.Count(r => r.EchoName?.Value != null);
        int statsFound = results.Count(r => r.MainStatKey?.Value != null);
        int stats2Found = results.Count(r => r.SecondMainStatKey?.Value != null);
        int sonatasFound = results.Count(r => r.Sonata?.Value != null);
        int complete = results.Count(r => !r.NeedsReview);
        float avgSubs = total > 0 ? (float)results.Average(r => r.Substats.Count) : 0;

        Console.WriteLine(new string('=', 70));
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("SCAN EVALUATION SUMMARY (coverage / detected - not accuracy):");
        Console.ResetColor();
        Console.WriteLine($"  Total images processed  : {total}");
        Console.WriteLine($"  Elapsed time            : {stopwatch.Elapsed.TotalSeconds:F2}s ({(stopwatch.Elapsed.TotalMilliseconds / Math.Max(1, total)):F0}ms/image)");
        Console.WriteLine($"  Echo names identified   : {namesFound}/{total} ({((float)namesFound / total):P1})");
        Console.WriteLine($"  Main stats detected     : {statsFound}/{total} ({((float)statsFound / total):P1})");
        Console.WriteLine($"  2nd main stats found  : {stats2Found}/{total} ({((float)stats2Found / total):P1})");
        Console.WriteLine($"  Sonata sets detected    : {sonatasFound}/{total} ({((float)sonatasFound / total):P1})");
        Console.WriteLine($"  Average substats / echo : {avgSubs:F2}");

        // Sonata source breakdown (Priority 3 re-run target: icon match should dominate).
        int sonataIcon = results.Count(r => r.SonataSource == "Icon");
        int sonataOcr = results.Count(r => r.SonataSource == "OcrText");
        Console.WriteLine($"  Sonata via icon match  : {sonataIcon}/{total} ({((float)sonataIcon / Math.Max(1, total)):P1})");
        Console.WriteLine($"  Sonata via OCR fallback : {sonataOcr}/{total} ({((float)sonataOcr / Math.Max(1, total)):P1})");

        // 4. Save JSON results
        var sessionSummary = new ScanSessionResult
        {
            SessionPath = targetDir,
            RunAt = DateTime.UtcNow,
            DatabaseVersion = GameDatabase.DataVersion,
            TotalImages = total,
            SuccessfulScans = namesFound,
            CompleteEchoes = complete,
            NameDetectionRate = (float)namesFound / total,
            MainStatDetectionRate = (float)statsFound / total,
            SecondMainStatDetectionRate = (float)stats2Found / total,
            SubstatAvg = avgSubs,
            Results = results
        };

        string outPath = customOut ?? Path.Combine(targetDir, $"test_scan_results_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        var opts = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(sessionSummary, opts));

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n[OK] Results successfully saved to:\n  {outPath}");
        Console.ResetColor();

        // 5. 1-click exports (Priority 4 / README roadmap)
        // E-03: collect refusals so nothing is dropped silently, then account for
        // every input image (exported + quarantined + skipped == total).
        var exportRejections = new List<ExportRejection>();
        int exportedCount = results.Count;
        int schemaSkipped = 0;

        void CollectRejections(List<ExportRejection> rejected)
        {
            foreach (var r in rejected)
                if (!exportRejections.Any(e => e.ImageFile == r.ImageFile))
                    exportRejections.Add(r);
        }

        if (exportTacet != null)
        {
            string tacetPath = exportTacet == "__auto__"
                ? Path.Combine(targetDir, $"tacet-lab-backup_{DateTime.Now:yyyyMMdd_HHmmss}.json")
                : exportTacet;
            try
            {
                string json = TacetLabExporter.ExportScans(results, ExportPolicy.Strict, out int skipped, out var rejected);
                CollectRejections(rejected);
                schemaSkipped = skipped - rejected.Count;
                exportedCount = results.Count - skipped;
                await File.WriteAllTextAsync(tacetPath, json);
                Console.WriteLine($"[OK] Tacet-Lab backup saved to:\n  {tacetPath} " +
                                  $"({exportedCount} exported, {rejected.Count} quarantined, {schemaSkipped} skipped)");
            }
            catch (ExportSchemaException ex)
            {
                // E-04: a payload that fails its own schema must not be written at all.
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[FAIL] Tacet-Lab export ABORTED - payload failed its schema self-check; nothing written:");
                foreach (var problem in ex.Problems.Take(5)) Console.WriteLine($"   - {problem}");
                Console.ResetColor();
                exportedCount = 0;
                schemaSkipped = results.Count;
            }
        }
        if (exportGood != null)
        {
            string goodPath = exportGood == "__auto__"
                ? Path.Combine(targetDir, $"sonoro-good_{DateTime.Now:yyyyMMdd_HHmmss}.json")
                : exportGood;
            try
            {
                string json = GoodExporter.ExportScans(results, ExportPolicy.Strict, out int skipped, out var rejected);
                CollectRejections(rejected);
                schemaSkipped = skipped - rejected.Count;
                exportedCount = results.Count - skipped;
                await File.WriteAllTextAsync(goodPath, json);
                Console.WriteLine($"[OK] GOOD file saved to:\n  {goodPath} " +
                                  $"({exportedCount} exported, {rejected.Count} quarantined, {schemaSkipped} skipped)");
            }
            catch (ExportSchemaException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[FAIL] GOOD export ABORTED - payload failed its schema self-check; nothing written:");
                foreach (var problem in ex.Problems.Take(5)) Console.WriteLine($"   - {problem}");
                Console.ResetColor();
                exportedCount = 0;
                schemaSkipped = results.Count;
            }
        }

        if (exportTacet != null || exportGood != null)
        {
            int quarantined = exportRejections.Count;
            int totalScans = results.Count;
            int accounted = exportedCount + quarantined + schemaSkipped;
            Console.WriteLine($"Export accounting (E-03): {exportedCount} exported + {quarantined} quarantined " +
                              $"+ {schemaSkipped} skipped = {accounted} of {totalScans}");
            if (accounted != totalScans)
                Console.WriteLine("  WARNING: accounting does not add up - investigate before trusting this export.");

            if (quarantined > 0)
            {
                string qPath = await Quarantine.SaveAsync(targetDir, exportRejections,
                    "Strict export refusals (E-01/E-02): missing required fields or unusable substat rolls.",
                    totalScans, exportedCount);
                Console.WriteLine($"[OK] Quarantine written to:\n  {qPath}");
            }

            int reviewFlagged = results.Count(r => r.NeedsReview);
            Console.WriteLine($"Flagged for review (still exported): {reviewFlagged} echo(es) - see per-scan flags/Diagnostics.");
        }

        return 0;
    }

    /// <summary>Hidden diagnostic: why won't the Tesseract engine initialise here?</summary>
    static async Task<int> DiagTesseract()
    {
        await Task.Yield();
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        Console.WriteLine($"bitness={(Environment.Is64BitProcess ? "x64" : "x86")}");
        Console.WriteLine($"baseDir={baseDir}");
        Console.WriteLine($"psm: name={ScannerConfig.NameRegionPsm} substats={ScannerConfig.SubstatBlockPsm} strips=SingleLine zones:sonata=SingleBlock,owner=SingleLine");
        Console.WriteLine($"preprocessing: tesseract=EchoFieldPreprocessor(gray,p2-98norm,polarity,Otsu,x3,black-on-white) winocr=legacy Enhance+Upscale2x");
        Console.WriteLine($"TESSDATA_PREFIX={Environment.GetEnvironmentVariable("TESSDATA_PREFIX") ?? "(unset)"}");

        string[] candidates =
        [
            Path.Combine(baseDir, "tessdata"),
            @"C:\tessshort",
        ];
        foreach (string dp in candidates)
        {
            string file = Path.Combine(dp, "eng.traineddata");
            Console.WriteLine($"--- datapath={dp}");
            try
            {
                var fi = new FileInfo(file);
                Console.WriteLine($"  exists={fi.Exists} len={(fi.Exists ? fi.Length : -1)} attrs={(fi.Exists ? fi.Attributes.ToString() : "-")}");
                using var fs = File.OpenRead(file);
                byte[] magic = new byte[8];
                fs.ReadExactly(magic);
                Console.WriteLine($"  managed-read-ok magic={BitConverter.ToString(magic)}");
            }
            catch (Exception ex) { Console.WriteLine($"  managed-read-FAIL: {ex.GetType().Name}: {ex.Message}"); }

            try
            {
                using var engine = new Tesseract.TesseractEngine(dp, "eng", Tesseract.EngineMode.Default);
                Console.WriteLine("  ENGINE OK");
            }
            catch (Exception ex) { Console.WriteLine($"  ENGINE FAIL: {ex}"); }
        }

        foreach (var mod in System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
            .Where(m => m.ModuleName.Contains("tesseract", StringComparison.OrdinalIgnoreCase)
                     || m.ModuleName.Contains("leptonica", StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"native: {mod.ModuleName} <- {mod.FileName}");
        return 0;
    }

    /// <summary>
    /// Hidden diagnostic: write the pipeline's own panel crop for byte-compare
    /// against externally cropped fixture panels.
    /// </summary>
    static int DumpPanel(string fullPng, string outPng)
    {
        if (!File.Exists(fullPng)) { Console.WriteLine($"Not found: {fullPng}"); return 1; }
        using var full = new System.Drawing.Bitmap(fullPng);
        Console.WriteLine($"full={full.Width}x{full.Height}");
        using var panel = EchoRegions.ExtractPanel(full);
        Console.WriteLine($"panel={panel.Width}x{panel.Height}");
        panel.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"wrote {outPng}");
        return 0;
    }

    /// <summary>
    /// Hidden diagnostic: crop the substats block of one image exactly like the
    /// pipeline does, then print what each OCR engine sees (text + Y/X bounds).
    /// </summary>
    static async Task<int> DumpLines(string imagePath)
    {
        if (!File.Exists(imagePath)) { Console.WriteLine($"Not found: {imagePath}"); return 1; }
        using var full = new System.Drawing.Bitmap(imagePath);
        using var panel = EchoRegions.ExtractPanel(full);
        Console.WriteLine($"panel={panel.Width}x{panel.Height} block={EchoRegions.SubstatsBlock}");
        // WinOcr legacy path input:
        using var crop = EchoRegions.CropRegion(panel, EchoRegions.SubstatsBlock);
        using var up = ImagePreprocessor.Upscale2x(ImagePreprocessor.EnhanceForOcr(crop));
        Console.WriteLine($"legacy-upscaled={up.Width}x{up.Height} tesseractAvailable={TesseractOcr.IsAvailable}");

        if (TesseractOcr.IsAvailable)
        {
            // New field-preprocessed path (what the pipeline actually feeds Tesseract):
            using var tessBmp = EchoFieldPreprocessor.Process(panel, EchoRegions.SubstatsBlock, FieldStrategy.Substat);
            Console.WriteLine($"tess-preprocessed={tessBmp.Width}x{tessBmp.Height} psm={ScannerConfig.SubstatBlockPsm}");
            foreach (var mode in new[] { ScannerConfig.SubstatBlockPsm, Tesseract.PageSegMode.Auto })
            {
                var lines = await TesseractOcr.RecognizeLinesWithBoundsAsync(tessBmp, mode, TesseractOcr.TextWhitelist);
                Console.WriteLine($"-- Tesseract {mode}: {lines.Count} lines");
                foreach (var l in lines)
                    Console.WriteLine($"   [y={l.Y:F0} x={l.X:F0} w={l.Width:F0} h={l.Height:F0}] \"{l.Text}\"");
            }
        }
        var wlines = await WinOcr.RecognizeLinesWithBoundsAsync(up);
        Console.WriteLine($"-- WinOcr: {wlines.Count} lines");
        foreach (var l in wlines)
            Console.WriteLine($"   [y={l.Y:F0} x={l.X:F0} w={l.Width:F0} h={l.Height:F0}] \"{l.Text}\"");
        return 0;
    }

    /// <summary>
    /// A-06 / A-07: score the pipeline against hand-truth fixture sidecars.
    /// Prints COVERAGE (detected - never accuracy) alongside ACCURACY (verified
    /// fixtures only), substat recall/precision, complete-echo exact match, the
    /// top confusion pairs, and writes accuracy_report.json.
    /// </summary>
    static async Task<int> RunAccuracyModeAsync(string truthDir, EchoCatalogEntry[] catalog, string? customOut)
    {
        if (!Directory.Exists(truthDir))
        {
            Console.WriteLine($"Truth directory not found: {truthDir}");
            return 1;
        }

        var sidecars = Directory.GetFiles(truthDir, "*.json").OrderBy(f => f).ToArray();
        if (sidecars.Length == 0)
        {
            Console.WriteLine($"No fixture sidecars (*.json) in {truthDir}");
            return 1;
        }

        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var recognizer = new EchoRecognizer(catalog, m => Console.WriteLine(m));
        var scored = new System.Collections.Generic.List<(EchoFixture Fx, EchoScanResult Scan, EchoAccuracyReport Rep)>();
        var entries = new System.Collections.Generic.List<AccuracyReport.FieldEntry>();
        var retryStats = new System.Collections.Generic.List<AccuracyReport.RetryStat>();

        Console.WriteLine($"Truth mode: {sidecars.Length} fixture sidecars in {truthDir}");
        Console.WriteLine(new string('-', 70));

        foreach (var sidecarPath in sidecars)
        {
            EchoFixture fx;
            try { fx = JsonSerializer.Deserialize<EchoFixture>(await File.ReadAllTextAsync(sidecarPath), jsonOpts)!; }
            catch (Exception ex) { Console.WriteLine($"  [skip] {Path.GetFileName(sidecarPath)}: {ex.Message}"); continue; }

            string png = Path.Combine(truthDir, fx.SourceImage);
            if (!File.Exists(png)) { Console.WriteLine($"  [skip] image missing: {fx.SourceImage}"); continue; }

            // Fixture images are already panel crops (mirrors EchoFixtureTests).
            var scan = await recognizer.RecognizePanelFileAsync(png);
            var rep = EchoAccuracy.Score(scan, fx);
            scored.Add((fx, scan, rep));
            entries.AddRange(AccuracyReport.Entries(fx, scan, rep));

            retryStats.Add(new AccuracyReport.RetryStat(fx.SourceImage, scan.SubstatRetries,
                scan.SubstatRetriesRecovered, string.Join(",", scan.SubstatRetrySlots)));

            string miss = string.Join(",", rep.Fields.Where(f => !f.Match).Select(f => f.Field));
            Console.WriteLine($"  {fx.SourceImage,-44} verified={fx.Verified,-5} rate={rep.Rate,6:P1} " +
                              $"retries={scan.SubstatRetries}/{scan.SubstatRetriesRecovered} miss=[{miss}]");
        }

        if (scored.Count == 0) { Console.WriteLine("No fixtures were scored."); return 1; }

        Console.WriteLine(new string('=', 70));

        int n = scored.Count;
        int names = scored.Count(s => s.Scan.EchoName?.Value != null);
        int mains = scored.Count(s => s.Scan.MainStatKey?.Value != null);
        int seconds = scored.Count(s => s.Scan.SecondMainStatKey?.Value != null);
        int sonatas = scored.Count(s => s.Scan.Sonata?.Value != null);
        Console.WriteLine($"COVERAGE (detected - NOT accuracy) over {n} fixtures:");
        Console.WriteLine($"  names        : {names}/{n} ({(float)names / n:P1})");
        Console.WriteLine($"  main stat    : {mains}/{n} ({(float)mains / n:P1})");
        Console.WriteLine($"  second main  : {seconds}/{n} ({(float)seconds / n:P1})");
        Console.WriteLine($"  sonata       : {sonatas}/{n} ({(float)sonatas / n:P1})");

        var verified = scored.Where(s => s.Fx.Verified).ToList();
        var vReports = verified.Select(s => s.Rep).ToList();
        int vMatched = vReports.Sum(r => r.Matched);
        int vTotal = vReports.Sum(r => r.Total);

        Console.WriteLine();
        Console.WriteLine($"ACCURACY (verified only) over {verified.Count} verified fixtures:");
        if (vTotal == 0)
        {
            Console.WriteLine("  n/a - no verified fixtures yet (TODO A-02: hand-verify sidecars).");
        }
        else
        {
            Console.WriteLine($"  fields matched   : {vMatched}/{vTotal} ({(float)vMatched / vTotal:P1})");
            int subMatched = vReports.Sum(r => AccuracyReport.SubstatCounts(r).Matched);
            int subExpected = vReports.Sum(r => AccuracyReport.SubstatCounts(r).Expected);
            int subExtra = vReports.Sum(r => AccuracyReport.SubstatCounts(r).Extra);
            float recall = subExpected == 0 ? float.NaN : (float)subMatched / subExpected;
            float precision = (subMatched + subExtra) == 0 ? float.NaN : (float)subMatched / (subMatched + subExtra);
            Console.WriteLine($"  substat recall   : {(float.IsNaN(recall) ? "n/a" : recall.ToString("P1"))}");
            Console.WriteLine($"  substat precision: {(float.IsNaN(precision) ? "n/a" : precision.ToString("P1"))}");
            Console.WriteLine($"  complete-echo exact match: {vReports.Count(AccuracyReport.IsExactMatch)}/{verified.Count}");
        }

        Console.WriteLine($"Substat retries (C-03): {retryStats.Sum(r => r.Retries)} slot(s) retried, " +
                          $"{retryStats.Sum(r => r.Recovered)} recovered.");

        string outPath = customOut ?? Path.Combine(truthDir, "accuracy_report.json");
        await File.WriteAllTextAsync(outPath,
            AccuracyReport.ToJson(entries, "generated by Scanner.Cli --truth", retryStats));

        Console.WriteLine();
        Console.WriteLine("CONFIDENCE CALIBRATION (D-02; preview - no verified fixtures yet):");
        Console.WriteLine("  bucket      fields  matched   rate");
        foreach (var b in AccuracyReport.Calibration(entries))
            Console.WriteLine($"  {b.Lower:0.0}-{b.Upper:0.0}    {b.Count,6}  {b.Matched,7}   {(b.Count == 0 ? "n/a" : b.Rate.ToString("P1"))}");

        Console.WriteLine();
        Console.WriteLine("TOP CONFUSIONS (expected -> actual):");
        var confusions = AccuracyReport.TopConfusions(entries, 5);
        if (confusions.Length == 0) Console.WriteLine("  (none)");
        foreach (var (exp, act, count) in confusions)
            Console.WriteLine($"  {exp} -> {act}  x{count}");

        Console.WriteLine($"\n[OK] accuracy report written to:\n  {outPath}");
        return 0;
    }

    /// <summary>
    /// F-40: build per-set sonata templates from OUR OWN verified captures, so the
    /// matcher compares like with like instead of against foreign reference icons.
    /// Truth comes from the review studio's verified export: only the sonata column is
    /// used, and only for echoes the user actually verified.
    /// </summary>
    static async Task<int> ExtractSonataTemplates(string sessionDir, string truthJson, string outPath, int minSamples)
    {
        if (!Directory.Exists(sessionDir)) { Console.WriteLine($"Session dir not found: {sessionDir}"); return 1; }
        if (!File.Exists(truthJson)) { Console.WriteLine($"Truth JSON not found: {truthJson}"); return 1; }
        await Task.Yield();

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(truthJson));
        if (!doc.RootElement.TryGetProperty("Echoes", out var echoes)) { Console.WriteLine("Truth JSON has no Echoes array."); return 1; }

        var bySet = new Dictionary<string, List<double[]>>(StringComparer.Ordinal);
        int used = 0, missing = 0;

        foreach (var e in echoes.EnumerateArray())
        {
            if (!e.TryGetProperty("ImageFileName", out var imgEl) || imgEl.ValueKind != JsonValueKind.String) continue;
            if (!e.TryGetProperty("Sonata", out var setEl) || setEl.ValueKind != JsonValueKind.String) continue;
            if (e.TryGetProperty("IsVerified", out var vEl) && vEl.ValueKind == JsonValueKind.False) continue;

            string set = setEl.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(set)) continue;

            string path = Path.Combine(sessionDir, imgEl.GetString()!);
            if (!File.Exists(path)) { missing++; continue; }

            using var full = new System.Drawing.Bitmap(path);
            using var panel = EchoRegions.ExtractPanel(full);
            using var icon = EchoRegions.CropRegion(panel, EchoRegions.SonataIcon);

            // Average the scale-1.0 samples (the fullest view of the icon) into one
            // representative sample for this capture, then average those per set.
            var samples = SonataSignatureMatcher.CapturedSignatures(icon);
            if (samples.Count < 75) continue;

            var rep256 = new double[256];
            int n = 0;
            for (int k = 60; k < samples.Count; k++)
            {
                var s = samples[k];
                if (s.Length != 256) continue;
                for (int j = 0; j < 256; j++) rep256[j] += s[j];
                n++;
            }
            if (n == 0) continue;
            for (int j = 0; j < 256; j++) rep256[j] /= n;

            if (!bySet.TryGetValue(set, out var acc)) bySet[set] = acc = [];
            acc.Add(rep256);
            used++;
        }

        var templates = new List<(string Name, double[] Signature)>();
        var skipped = new List<string>();
        foreach (var (set, samples) in bySet.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (samples.Count < minSamples) { skipped.Add($"{set}({samples.Count})"); continue; }
            var tpl = new double[256];
            foreach (var s in samples)
                for (int j = 0; j < 256; j++) tpl[j] += s[j];
            for (int j = 0; j < 256; j++) tpl[j] /= samples.Count;
            templates.Add((set, tpl));
        }

        SonataSignatureMatcher.SaveTemplates(outPath, templates, "local-1");

        Console.WriteLine($"Captures used: {used} (missing files: {missing})");
        Console.WriteLine($"Templates written: {templates.Count} -> {outPath}");
        foreach (var (name, _) in templates)
            Console.WriteLine($"   {name,-30} n={bySet[name].Count}");
        if (skipped.Count > 0)
            Console.WriteLine($"Skipped (fewer than {minSamples} samples, shipped template still used): {string.Join(", ", skipped)}");
        return 0;
    }

    /// <summary>
    /// F-33: score every sonata template for one capture, and report the crop's own
    /// colour, so the confusion can be inspected on real data instead of guessed at.
    /// </summary>
    static async Task<int> DiagSonata(string imagePath, bool isPanel)
    {
        if (!File.Exists(imagePath)) { Console.WriteLine($"Image not found: {imagePath}"); return 1; }
        await Task.Yield();

        using var loaded = new System.Drawing.Bitmap(imagePath);
        using var panel = isPanel ? new System.Drawing.Bitmap(loaded) : EchoRegions.ExtractPanel(loaded);
        using var icon = EchoRegions.CropRegion(panel, EchoRegions.SonataIcon);

        Console.WriteLine($"Image : {Path.GetFileName(imagePath)}  (panel {panel.Width}x{panel.Height}, icon {icon.Width}x{icon.Height})");

        double rs = 0, gs = 0, bs = 0; int n = 0;
        for (int y = 0; y < icon.Height; y++)
            for (int x = 0; x < icon.Width; x++)
            {
                var c = icon.GetPixel(x, y);
                if (c.A < 40) continue;
                rs += c.R; gs += c.G; bs += c.B; n++;
            }
        if (n > 0)
            Console.WriteLine($"Icon mean RGB: R={rs / n:F0} G={gs / n:F0} B={bs / n:F0}  ({n} opaque px)");

        Console.WriteLine($"Signature version: {SonataSignatureMatcher.LoadedVersion ?? "none"}  templates: {SonataSignatureMatcher.TemplateCount}");
        var ranked = SonataSignatureMatcher.RankAll(icon);
        Console.WriteLine("Rank  set                              score");
        for (int i = 0; i < Math.Min(8, ranked.Count); i++)
            Console.WriteLine($"  {i + 1,2}. {ranked[i].Name,-32} {ranked[i].Score:F4}");
        if (ranked.Count >= 2)
            Console.WriteLine($"Margin 1st-2nd: {ranked[0].Score - ranked[1].Score:F4}");
        return 0;
    }

    /// <summary>
    /// B-02: enumerate every catalog pair whose normalized names contain one
    /// another - the collisions that made the old matcher unreliable (F-01).
    /// Prints markdown to stdout; commit the output as docs/name-collisions.md.
    /// </summary>
    static async Task<int> DiagNameCollisions(string? catalogPath)
    {
        EchoCatalogEntry[] catalog;
        if (!string.IsNullOrEmpty(catalogPath))
        {
            if (!File.Exists(catalogPath)) { Console.WriteLine($"Catalog not found: {catalogPath}"); return 1; }
            catalog = await GameDatabase.LoadFromFileAsync(catalogPath);
        }
        else
        {
            string local = Path.Combine(AppContext.BaseDirectory, "echo_catalog.json");
            catalog = File.Exists(local)
                ? await GameDatabase.LoadFromFileAsync(local)
                : await GameDatabase.LoadAsync(forceRefresh: true, log: m => Console.WriteLine(m));
        }

        var entries = catalog
            .Select(e => (Name: e.Name, Norm: FuzzyMatcher.Normalize(e.Name)))
            .OrderBy(x => x.Name)
            .ToArray();

        Console.WriteLine("# Name collisions (normalized substring containment)");
        Console.WriteLine();
        Console.WriteLine($"Catalog entries: {entries.Length} (GameDatabase {GameDatabase.DataVersion})");
        Console.WriteLine();
        Console.WriteLine("A name that contains another becomes a false 1.0 match candidate unless the");
        Console.WriteLine("matcher prefers the exact form (B-01). These pairs are mandatory fixtures.");
        Console.WriteLine();
        Console.WriteLine("| shorter name (normalized) | contained in | normalized forms |");
        Console.WriteLine("|---|---|---|");

        int pairs = 0;
        foreach (var a in entries)
        {
            if (a.Norm.Length < 5) continue;
            foreach (var b in entries)
            {
                if (a.Norm == b.Norm || !b.Norm.Contains(a.Norm)) continue;
                pairs++;
                Console.WriteLine($"| {a.Name} | {b.Name} | `{a.Norm}` in `{b.Norm}` |");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Total collision pairs: {pairs}");
        return 0;
    }
}
