using System.Drawing;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Tesseract;

namespace SonoroScore.Scanner;

/// <summary>
/// Main echo recognition pipeline — adapted from Tacet-Lab's recognize.ts + parser.ts.
///
/// Pipeline per image:
///   1. Extract panel strip from full screenshot.
///   2. Crop named regions (name, level, cost, rarity-band, main-stat-label/value, substats).
///   3. Enhance + OCR each text crop via Windows.Media.Ocr.
///   4. Pixel-classify rarity from star band.
///   5. Fuzzy-match echo name against catalog.
///   6. Parse stat lines; snap substat values to tunable rolls.
///   7. Return EchoScanResult with per-field confidence.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public class EchoRecognizer
{
    private readonly EchoCatalogEntry[] _catalog;
    private readonly Action<string>? _log;

    public EchoRecognizer(EchoCatalogEntry[] catalog, Action<string>? log = null)
    {
        _catalog = catalog;
        _log = log;
    }

    public async Task<EchoScanResult> RecognizeAsync(string imagePath)
    {
        _log?.Invoke($"  [OCR] Scanning {Path.GetFileName(imagePath)}");

        Bitmap full;
        try { full = new Bitmap(imagePath); }
        catch (Exception ex)
        {
            return Error(imagePath, $"Cannot open image: {ex.Message}");
        }

        using (full)
        {
            return await RecognizeBitmapAsync(imagePath, full);
        }
    }

    /// <summary>
    /// Recognize an image that is ALREADY a cropped echo panel (fixture corpus
    /// entry point). Mirrors <see cref="RecognizePanelAsync"/> ownership rules.
    /// </summary>
    public async Task<EchoScanResult> RecognizePanelFileAsync(string panelPath)
    {
        Bitmap panel;
        try { panel = new Bitmap(panelPath); }
        catch (Exception ex) { return Error(panelPath, $"Cannot open image: {ex.Message}"); }
        return await RecognizePanelAsync(panel, Path.GetFileName(panelPath));
    }

    private async Task<EchoScanResult> RecognizeBitmapAsync(string imagePath, Bitmap full)
    {
        // Our captured images ARE already the full WuWa window frame.
        // The echo detail panel is at the right side (~78% x, 12% y).
        Bitmap panel = EchoRegions.ExtractPanel(full);
        return await RecognizePanelAsync(panel, imagePath);
    }

    /// <summary>
    /// Recognize an already-cropped echo detail panel. Takes ownership of
    /// <paramref name="panel"/> (disposed before return). Fixture-test entry point.
    /// </summary>
    public async Task<EchoScanResult> RecognizePanelAsync(Bitmap panel, string imagePath)
    {
        // ── 1. Extract panel ────────────────────────────────────────────────
        // (done by caller — panel IS the right-side echo strip here)

        var errors   = new List<string>();
        var warnings = new List<string>();

        // ── 0. Signature version check (Priority 4) ─────────────────────────
        // Warns when sonata_signatures.json is stale vs. GameDatabase.DataVersion.
        if (!SonataSignatureMatcher.CheckVersion(_log))
            warnings.Add($"Sonata signature version mismatch (loaded='{SonataSignatureMatcher.LoadedVersion ?? "none"}', " +
                         $"expected='{SonataSignatureMatcher.ExpectedSignatureVersion}', db='{GameDatabase.DataVersion}').");

        // ── 0b. OCR engine mode (Priority 3/4 QA gate) ──────────────────────
        string ocrMode = TesseractOcr.IsAvailable
            ? (ScannerConfig.UseWindowsOcrFallback ? "Tesseract+WinOcr-fallback" : "Tesseract-only")
            : (ScannerConfig.UseWindowsOcrFallback
                ? $"WinOcr-only (Tesseract unavailable: {TesseractOcr.UnavailableReason ?? "unknown reason"})"
                : "NO-OCR-ENGINE");
        _log?.Invoke($"  [OCR] Engine mode: {ocrMode}");
        if (!TesseractOcr.IsAvailable && ScannerConfig.UseWindowsOcrFallback)
            warnings.Add($"Tesseract unavailable ({TesseractOcr.UnavailableReason ?? "unknown"}) - degraded to Windows OCR; readings may be wrong.");
        if (ocrMode == "NO-OCR-ENGINE")
            warnings.Add("No OCR engine available (Tesseract missing and Windows fallback disabled).");

        try
        {
            // ── 2. OCR individual regions ───────────────────────────────────
            // Strategy/PSM/whitelist per region shape (Tacet ocr-pool mapping):
            // name→Name/SINGLE_BLOCK, level→number/SINGLE_LINE, strips→Label/
            // SINGLE_LINE, zones→Text, substats→Substat/SINGLE_BLOCK.
            var nameRead = await OcrNameAsync(panel);
            string nameOcr = nameRead.Text;
            var levelRead = await OcrRegionAsync(panel, EchoRegions.Level, FieldStrategy.Text,
                PageSegMode.SingleLine, TesseractOcr.NumberWhitelist,
                s => ParseLevel(s) != null);
            var costRead = await OcrRegionAsync(panel, EchoRegions.Cost, FieldStrategy.Text,
                PageSegMode.SingleLine, TesseractOcr.TextWhitelist,
                s => ParseCost(s) != null);
            var mainRead = await OcrRegionAsync(panel, EchoRegions.MainStatStrip, FieldStrategy.Text,
                PageSegMode.SingleLine, TesseractOcr.TextWhitelist,
                s => StatParser.ParseLine(StatParser.NormalizeOcrArtifacts(s)) != null);
            var secondMainRead = await OcrRegionAsync(panel, EchoRegions.SecondMainStat, FieldStrategy.Text,
                PageSegMode.SingleLine, TesseractOcr.TextWhitelist,
                s => StatParser.ParseLine(StatParser.NormalizeOcrArtifacts(s)) != null);
            // Zone B: Sonata Effect region (starts below skill text)
            var sonataZoneRead = await OcrRegionAsync(panel, EchoRegions.SonataZone, FieldStrategy.Text,
                PageSegMode.SingleBlock, TesseractOcr.TextWhitelist);
            // Zone C: Owner strip
            var ownerRead = await OcrRegionAsync(panel, EchoRegions.OwnerZone, FieldStrategy.Text,
                PageSegMode.SingleLine, TesseractOcr.TextWhitelist);

            // D-06: per-field provenance is recorded as DATA, so nothing downstream
            // has to parse the human-readable warnings.
            var diagnostics = new List<EchoScanResult.FieldDiagnostics>();
            void Note(string field, RegionRead r, string? raw, string? note = null)
                => diagnostics.Add(new EchoScanResult.FieldDiagnostics(
                    field, r.Engine, r.Psm, r.Preprocess, r.Attempt, raw ?? r.Text, r.Confidence, note));

            Note("identity", nameRead, nameRead.Text);
            Note("level", levelRead, levelRead.Text);
            Note("cost", costRead, costRead.Text);
            Note("mainStat", mainRead, mainRead.Text);
            Note("secondMainStat", secondMainRead, secondMainRead.Text);
            Note("sonata", sonataZoneRead, sonataZoneRead.Text);
            Note("owner", ownerRead, ownerRead.Text);

            // D-01: the engine's own confidence travels with every read; 0 means
            // "the engine could not tell us" (Windows OCR), never "perfect".
            string levelOcr = levelRead.Text;
            string costOcr = costRead.Text;
            string mainStatLine = mainRead.Text;
            string secondMainStatLine = secondMainRead.Text;
            string sonataZoneOcr = sonataZoneRead.Text;
            string ownerZoneOcr = ownerRead.Text;

            _log?.Invoke($"    Name OCR: \"{nameOcr.Replace('\n',' ')}\"");

            // ── 3. Rarity (pixel, no OCR) ───────────────────────────────────
            // Raw color crop: the hue classifier needs color, never binarized text.
            // Rarity is NOT shown on the character-select echo panel. It is a
            // legacy field inherited from the Genshin-style export formats, and
            // every echo here is 5-star, so it is a documented CONSTANT - never a
            // read value, and never usable as evidence (see B-03).
            const int rarity = 5;
            const string rarityProvenance = "constant 5* (panel shows no rarity; legacy export field)";

            // 3b. Independent evidence BEFORE name matching (B-03, closes F-06):
            // the sonata icon, the OCR'd cost and the rarity each restrict which
            // catalog entries the name could possibly be.
            string? sonataIconName = null;
            float sonataIconConf = 0f;
            using (var iconCrop = EchoRegions.CropRegion(panel, EchoRegions.SonataIcon))
            {
                // Raw color crop: the signature matcher needs color, never binarized text.
                var (sigName, sigConf, sigMargin, sigRunnerUp) = SonataSignatureMatcher.MatchDetailed(iconCrop);
                if (sigName != null && sigConf > ScannerConfig.SonataIconMinConfidence)
                {
                    sonataIconName = sigName;
                    sonataIconConf = (float)sigConf;
                }

                // F-33: all 34 sets score within ~0.05 of each other, so a thin margin
                // means a coin flip - say so rather than reporting it as a reading.
                if (sigName != null && sigMargin < ScannerConfig.SonataIconMinMargin)
                {
                    var top = SonataSignatureMatcher.RankAll(iconCrop);
                    var names = new List<string>();
                    for (int k = 0; k < top.Count && k < 3; k++) names.Add(top[k].Name);
                    warnings.Add($"SonataIconAmbiguous: '{sigName}' beat '{sigRunnerUp}' by only {sigMargin:F4} " +
                                 $"(margin floor {ScannerConfig.SonataIconMinMargin:F2}); top candidates: {string.Join(", ", names)}.");
                }
            }

            int? costOcrParsed = ParseCost(costOcr);

            // F-45: the panel TEXT names the set. When it parses cleanly it beats the icon
            // for constraining the name (the icon cannot separate some families), so the
            // contradiction flag below stops firing for real, readable panels.
            var (sonataTextName, sonataTextConf) = SonataFromZoneText(
                sonataZoneOcr, Math.Max(ScannerConfig.SonataTextMinConfidence, 0.70f));

            var flags = new List<string>();
            EchoCatalogEntry[] nameCandidates = _catalog;
            bool nameConstrained = false;

            string? constraintSonata = sonataTextName ?? sonataIconName;
            if (constraintSonata != null)
            {
                var con = nameCandidates
                    .Where(c => c.Sonatas.Contains(constraintSonata, StringComparer.OrdinalIgnoreCase)).ToArray();

                // A text set with no catalog entry at all would strand the constraint;
                // fall back to the icon set (and keep the reason on record).
                if (con.Length == 0 && sonataTextName != null && sonataIconName != null &&
                    !string.Equals(sonataTextName, sonataIconName, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"SonataTextConstraintEmpty: text set '{sonataTextName}' has no catalog entry; " +
                                 $"using the icon set '{sonataIconName}' for the name constraint.");
                    constraintSonata = sonataIconName;
                    con = nameCandidates
                        .Where(c => c.Sonatas.Contains(constraintSonata, StringComparer.OrdinalIgnoreCase)).ToArray();
                }

                nameCandidates = con;
                nameConstrained = true;
            }
            if (costOcrParsed.HasValue)
            {
                nameCandidates = nameCandidates.Where(c => c.Cost == costOcrParsed.Value).ToArray();
                nameConstrained = true;
            }
            // Rarity is deliberately NOT used to filter candidates: the panel does
            // not show it, the value is a constant, and a bogus low value would
            // exclude every catalog entry. Its catalog field `Rarities` stays unused.

            // Fallback is strict: widen AND flag, never widen silently (D-06).
            if (nameConstrained && nameCandidates.Length == 0)
            {
                nameCandidates = _catalog;
                flags.Add("NameContradictsEvidence");
                warnings.Add("NameContradictsEvidence: sonata icon / cost / rarity filtered the catalog " +
                             "to zero entries; fell back to the full catalog and flagged it.");
            }

            _log?.Invoke($"    Name candidates: {nameCandidates.Length}/{_catalog.Length}" +
                         (nameConstrained ? " (constrained)" : " (unconstrained)"));

            // ── 4. Echo name (fuzzy catalog match) ──────────────────────────
            // B-03: the whole name cascade runs against a candidate POOL so it can be
            // retried unconstrained when the independent evidence contradicts it.
            async Task<(EchoCatalogEntry? Entry, float Score, string? AutoOcr, string Stage, bool Phantom)> MatchNameAsync(EchoCatalogEntry[] pool)
            {
                string cleaned = CleanOcrText(nameOcr);

                // F-36: "Phantom:" variants exist on the panel but NOT in the catalog; the
                // prefix is stripped for MATCHING only and kept in the reported name.
                bool phantomVariant = Regex.IsMatch(cleaned, @"^\s*phantom\b[:\s]", RegexOptions.IgnoreCase);
                if (phantomVariant)
                    cleaned = Regex.Replace(cleaned, @"^\s*phantom\b[:\s]*", "", RegexOptions.IgnoreCase).Trim();

                string? autoOcr = null;
                string stage = "single";   // B-05: which fallback produced the winner
                var (entry, score) = FuzzyMatcher.ClosestMatch(
                    cleaned, pool, e => e.Name, threshold: ScannerConfig.EchoNameMinConfidence);

                // Try 2-line windows if score is low
                if (score < ScannerConfig.NameAcceptScore)
                {
                    var lines = cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < lines.Length - 1 && score < ScannerConfig.NameRescueScore; i++)
                    {
                        string window = lines[i] + " " + lines[i + 1];
                        var (e2, s2) = FuzzyMatcher.ClosestMatch(window, pool, e => e.Name,
                            ScannerConfig.EchoNameMinConfidence);
                        if (s2 > score) { entry = e2; score = s2; stage = "window2"; }
                    }
                }

                // SingleLine PSM can clip wrapped (2-line) names: re-OCR with Auto
                // segmentation as a second chance when the score is still low.
                if (score < ScannerConfig.NameAcceptScore && TesseractOcr.IsAvailable)
                {
                    try
                    {
                        string candidateOcr = (await OcrRegionAsync(panel, EchoRegions.EchoName,
                            FieldStrategy.Name, PageSegMode.Auto, TesseractOcr.TextWhitelist)).Text;
                        string cleanedAuto = CleanOcrText(candidateOcr);
                        if (Regex.IsMatch(cleanedAuto, @"^\s*phantom\b[:\s]", RegexOptions.IgnoreCase))
                            cleanedAuto = Regex.Replace(cleanedAuto, @"^\s*phantom\b[:\s]*", "", RegexOptions.IgnoreCase).Trim();
                        if (!string.IsNullOrWhiteSpace(cleanedAuto))
                        {
                            var (e3, s3) = FuzzyMatcher.ClosestMatch(
                                cleanedAuto, pool, e => e.Name, threshold: ScannerConfig.EchoNameMinConfidence);
                            // Guard: below-threshold matches return a null entry -
                            // never let them overwrite (or lock out) a real candidate.
                            if (e3 != null && s3 > score)
                            {
                                entry = e3; score = s3; cleaned = cleanedAuto; autoOcr = candidateOcr;
                                stage = "auto";
                            }
                            // Per-line rescue always runs while the score is still low:
                            // a wrapped name often matches on one clean line even when
                            // the whole text doesn't.
                            if (score < ScannerConfig.NameRescueScore)
                            {
                                foreach (var line in cleanedAuto.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                                {
                                    var (e4, s4) = FuzzyMatcher.ClosestMatch(line, pool, e => e.Name, ScannerConfig.EchoNameMinConfidence);
                                    if (e4 != null && s4 > score) { entry = e4; score = s4; stage = "per-line"; }
                                }
                            }
                        }
                    }
                    catch { /* keep SingleLine result */ }
                }

                return (entry, score, autoOcr, stage, phantomVariant);
            }

            bool namePhantom = false;
            var (nameEntry, nameScore, nameAutoOcr, nameStage, namePhantomFirst) = await MatchNameAsync(nameCandidates);
            namePhantom = namePhantomFirst;
            if (nameAutoOcr != null) nameOcr = nameAutoOcr;   // keep evidence with the winner

            // Strict widening: a constrained pool that yields NO name means the
            // evidence contradicts the catalog. Retry the FULL catalog and flag it -
            // never return nothing silently (B-03 / D-06).
            if (nameEntry == null && nameConstrained)
            {
                flags.Add("NameContradictsEvidence");
                warnings.Add("NameContradictsEvidence: no name matched inside the sonata/cost/rarity-" +
                             "constrained set; retried the full catalog and flagged it.");
                (nameEntry, nameScore, nameAutoOcr, nameStage, namePhantom) = await MatchNameAsync(_catalog);
                nameStage += " (unconstrained retry)";
                if (nameAutoOcr != null) nameOcr = nameAutoOcr;
            }

            // F-38 extension: the name strip suffers the same gradient clipping as the stat
            // strips ("Nightmar . oses", or nothing at all). If no name matched, re-read the
            // strip with the local-threshold pass before giving up.
            if (nameEntry == null && TesseractOcr.IsAvailable)
            {
                try
                {
                    using var altName = EchoFieldPreprocessor.ProcessAdaptive(panel, EchoRegions.EchoName);
                    var (altText, _) = await TesseractOcr.RecognizeWithConfidenceAsync(
                        altName, ScannerConfig.NameRegionPsm, TesseractOcr.TextWhitelist);
                    string cleanedAlt = CleanOcrText(altText);
                    if (Regex.IsMatch(cleanedAlt, @"^\s*phantom\b[:\s]", RegexOptions.IgnoreCase))
                    {
                        cleanedAlt = Regex.Replace(cleanedAlt, @"^\s*phantom\b[:\s]*", "", RegexOptions.IgnoreCase).Trim();
                        namePhantom = true;
                    }
                    if (!string.IsNullOrWhiteSpace(cleanedAlt))
                    {
                        var pools = nameConstrained
                            ? new[] { nameCandidates, _catalog }
                            : new[] { _catalog };
                        foreach (var pool in pools)
                        {
                            var (eAlt, sAlt) = FuzzyMatcher.ClosestMatch(
                                cleanedAlt, pool, e => e.Name, ScannerConfig.EchoNameMinConfidence);
                            if (eAlt != null)
                            {
                                warnings.Add($"NameAdaptiveRetry: the primary name read did not match; a " +
                                             $"local-threshold re-read gave '{eAlt.Name}' ({sAlt:F2}).");
                                nameEntry = eAlt;
                                nameScore = sAlt;
                                nameOcr = altText;
                                nameStage += " (adaptive name retry)";
                                // If the re-read matched inside the evidence-constrained pool, the
                                // contradiction was an OCR failure, not a catalog conflict - clear it.
                                if (nameConstrained && !ReferenceEquals(pool, _catalog) &&
                                    flags.Contains("NameContradictsEvidence"))
                                {
                                    flags.Remove("NameContradictsEvidence");
                                    warnings.Add("NameAdaptiveRetry: matched inside the evidence-constrained pool, " +
                                                 "so the contradiction flag was cleared.");
                                }
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add("NameAdaptiveRetry failed: " + ex.Message);
                }
            }

            if (nameEntry != null && namePhantom)
                warnings.Add($"PhantomVariant: the panel shows a \"Phantom:\" variant; matched the base catalog " +
                             $"entry '{nameEntry.Name}' and kept the prefix in the reported name.");

            if (nameEntry == null)
                warnings.Add($"Echo name not matched (best score {nameScore:F2}): \"{CleanOcrText(nameOcr).Replace('\n', ' ')}\"");

            // B-05: which fallback won is provenance, not prose - record it as data.
            int identityIdx = diagnostics.FindIndex(d => d.Field == "identity");
            if (identityIdx >= 0)
                diagnostics[identityIdx] = diagnostics[identityIdx] with
                {
                    Note = $"stage={nameStage}; candidates={(nameConstrained ? nameCandidates.Length : _catalog.Length)}",
                };

            // ── 5. Level ────────────────────────────────────────────────────
            int? level = ParseLevel(levelOcr);
            if (level == null && TesseractOcr.IsAvailable)
            {
                // D-03: one 4x-scale retry before giving up on the level strip.
                try
                {
                    using var levelTess = EchoFieldPreprocessor.Process(panel, EchoRegions.Level, FieldStrategy.Text);
                    using var levelBig = new Bitmap(levelTess.Width * 4, levelTess.Height * 4,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(levelBig))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(levelTess, 0, 0, levelBig.Width, levelBig.Height);
                    }
                    string retry = await TesseractOcr.RecognizeAsync(
                        levelBig, PageSegMode.SingleLine, TesseractOcr.NumberWhitelist);
                    level = ParseLevel(retry);
                    if (level != null)
                        warnings.Add($"Level recovered by 4x retry: '{retry.Trim()}' (first read: '{levelOcr.Trim()}').");
                }
                catch { /* keep the null - the flag below records it */ }
            }
            if (level == null)
            {
                flags.Add("LevelUnparsed");
                warnings.Add($"LevelUnparsed: no full-string number in \"{levelOcr}\".");
            }

            // ── 6. Cost (from catalog or parse) ────────────────────────────
            // B-04: the OCR'd cost is primary evidence; the catalog cost is only a
            // second opinion. The old flat 0.95 came from a name match alone, so a
            // wrong name produced a confidently wrong cost.
            int? cost = costOcrParsed ?? nameEntry?.Cost;
            float costConfidence = costOcrParsed.HasValue
                ? (float)(costRead.Confidence ?? 0.5)
                : (nameEntry != null ? 0.50f : 0f);
            if (costOcrParsed.HasValue && nameEntry != null && nameEntry.Cost != costOcrParsed.Value)
            {
                flags.Add("CostNameMismatch");
                warnings.Add($"CostNameMismatch: OCR cost {costOcrParsed.Value} vs name '{nameEntry.Name}' " +
                             $"cost {nameEntry.Cost} - kept the OCR value and lowered name confidence.");
                nameScore *= 0.8f;
            }

            // ── 7. Main stat ────────────────────────────────────────────────
            var mainStat = StatParser.ParseLine(mainStatLine);
            if (mainStat == null)
            {
                // Fallback: line might be split across newlines
                foreach (var line in mainStatLine.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    mainStat = StatParser.ParseLine(line);
                    if (mainStat != null) break;
                }
            }

            // F-38: the same gradient that defeats the substat block also clips these
            // strips ("Ha oc Bonus", "ATK" with the value dropped). Retry the strip with
            // the local-threshold pass before declaring the field unreadable.
            if (mainStat == null && TesseractOcr.IsAvailable)
            {
                try
                {
                    using var altMain = EchoFieldPreprocessor.ProcessAdaptive(panel, EchoRegions.MainStatStrip);
                    string altText = await TesseractOcr.RecognizeAsync(altMain, PageSegMode.SingleLine, TesseractOcr.TextWhitelist);
                    mainStat = StatParser.ParseLine(StatParser.NormalizeOcrArtifacts(altText));
                    if (mainStat != null)
                        warnings.Add($"MainStatAdaptiveRetry: primary read \"{mainStatLine.Replace('\n', ' ')}\" did not " +
                                     $"parse; a local-threshold re-read gave '{mainStat.Key} {mainStat.Value}'.");
                }
                catch (Exception ex)
                {
                    warnings.Add("MainStatAdaptiveRetry failed: " + ex.Message);
                }
            }

            if (mainStat == null)
                errors.Add($"Main stat not parsed from: \"{mainStatLine.Replace('\n', ' ')}\"");

            // ── 7b. Second main stat (own region under the primary strip) ───
            // Lenient: warns instead of erroring, and stays silent when the
            // region is empty (not every echo shows a second line).
            var secondMainStat = StatParser.ParseLine(secondMainStatLine);
            if (secondMainStat == null && !string.IsNullOrWhiteSpace(secondMainStatLine))
            {
                foreach (var line in secondMainStatLine.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    secondMainStat = StatParser.ParseLine(line);
                    if (secondMainStat != null) break;
                }
                if (secondMainStat == null)
                    warnings.Add($"Second main stat not parsed from: \"{secondMainStatLine.Replace('\n', ' ')}\"");
            }

            // F-38: same local-threshold retry for the secondary strip.
            if (secondMainStat == null && TesseractOcr.IsAvailable)
            {
                try
                {
                    using var altSecond = EchoFieldPreprocessor.ProcessAdaptive(panel, EchoRegions.SecondMainStat);
                    string altText = await TesseractOcr.RecognizeAsync(altSecond, PageSegMode.SingleLine, TesseractOcr.TextWhitelist);
                    secondMainStat = StatParser.ParseLine(StatParser.NormalizeOcrArtifacts(altText));
                    if (secondMainStat != null)
                        warnings.Add($"SecondMainStatAdaptiveRetry: primary read \"{secondMainStatLine.Replace('\n', ' ')}\"" +
                                     $" did not parse; a local-threshold re-read gave '{secondMainStat.Key} {secondMainStat.Value}'.");
                }
                catch (Exception ex)
                {
                    warnings.Add("SecondMainStatAdaptiveRetry failed: " + ex.Message);
                }
            }

            // D-04: cross-field consistency. For a fixed (cost, key) the main and
            // secondary values are deterministic in level, so a mismatch means one of
            // the three is wrong. Rules ported from Tacet-Lab's echo-main-stats.ts.
            if (cost.HasValue && level.HasValue)
            {
                int mainCost = cost.Value;

                if (mainStat != null)
                {
                    if (!EchoMainStats.IsMainStatAllowed(mainCost, mainStat.Key))
                    {
                        flags.Add("MainStatInvalidForCost");
                        warnings.Add($"MainStatInvalidForCost: a cost {mainCost} echo cannot have " +
                                     $"{mainStat.Key} as its primary main stat.");
                    }
                    else
                    {
                        var expectedMain = EchoMainStats.PrimaryValue(mainCost, rarity, level.Value, mainStat.Key);
                        if (expectedMain.HasValue && Math.Abs(mainStat.Value - expectedMain.Value) > 0.051f)
                        {
                            int? consistent = EchoMainStats.FindConsistentLevel(mainCost, rarity, mainStat.Key, mainStat.Value);
                            if (consistent.HasValue && consistent.Value != level.Value)
                            {
                                warnings.Add($"LevelCorrectedByStats: main {mainStat.Key}={mainStat.Value} is only " +
                                             $"legal at level {consistent} for cost {mainCost} (OCR read {level}); level corrected.");
                                level = consistent.Value;
                                flags.Add("LevelCorrectedByStats");
                            }
                            else if (!consistent.HasValue)
                            {
                                flags.Add("MainValueImpossible");
                                warnings.Add($"MainValueImpossible: main {mainStat.Key}={mainStat.Value} cannot be produced " +
                                             $"at any level for cost {mainCost} (expected {expectedMain} at level {level}).");
                            }
                        }
                    }
                }

                if (secondMainStat != null)
                {
                    StatKey wantSecondary = EchoMainStats.SecondaryKey(mainCost);
                    if (secondMainStat.Key != wantSecondary)
                    {
                        flags.Add("SecondMainStatInvalid");
                        warnings.Add($"SecondMainStatInvalid: a cost {mainCost} echo's secondary main stat is " +
                                     $"{wantSecondary}, not {secondMainStat.Key}.");
                    }
                    else
                    {
                        var expectedSecond = EchoMainStats.SecondaryValue(mainCost, rarity, level.Value);
                        if (expectedSecond.HasValue && Math.Abs(secondMainStat.Value - expectedSecond.Value) > 0.51f)
                            warnings.Add($"Second main stat {secondMainStat.Key}={secondMainStat.Value} differs from the " +
                                         $"deterministic {expectedSecond} at level {level} (informational).");
                    }
                }
            }

            // ── 8. Substats (Y-clustered slots + pixel fallback) ────────────
            // substatsBmp is a RAW color crop: StatPixelMatcher needs color,
            // never the binarized text path.
            using var substatsBmp = EchoRegions.CropRegion(panel, EchoRegions.SubstatsBlock);
            var (ocrLines, slotRefH) = await OcrLinesAsync(panel);

            // Y-position slotting: each OCR line goes to the nearest tuned slot
            // center (in block-fraction space, scaled to this crop's pixels), so
            // variable row pitch doesn't break assignment like fixed division would.
            var block = EchoRegions.SubstatsBlock;
            double[] slotCenters = Enumerable.Range(1, 5).Select(i =>
            {
                var r = EchoRegions.SubstatSlot(i);
                return (r.Y + r.Height / 2 - block.Y) / block.Height * slotRefH;
            }).ToArray();
            // C-04: a line may only be slotted within 0.6 x the slot pitch of a
            // slot centre; anything farther is an orphan, never a forced guess.
            double slotMaxDistance = SubstatSlotter.MaxSlotDistance(slotCenters);
            var orphanLines = new List<string>();
            var slotCollisions = new List<int>();

            var slotFilled = new bool[5];   // C-03: which slots produced an accepted row
            var slotConf = new double?[5];  // D-01: engine confidence of the line that filled the slot
            var slotEngine = new string?[5]; // D-06: which engine produced that line
            var slotLabels = new string?[5];
            var slotValues = new float?[5];
            var slotPercents = new bool[5];
            var slotValStrs = new string?[5];
            var slotWhole = new ParsedStat?[5];

            // A slot's row counts as RESOLVED only when its roll is legal; an
            // unresolved row (NotARoll / Ambiguous) stays a replaceable proposal.
            bool SlotResolved(int slot) =>
                slotWhole[slot] is { } r && TunableRolls.ResolveDetailed(r.Key, r.Value).Value != null;

            // Assigns OCR lines to slots. Primary pass: first full row wins (C-04).
            // F-38 retry pass: may also REPLACE an unresolved proposal with a row whose
            // roll is legal - never the other way round.
            void AssignLines(IEnumerable<OcrLineInfo> lines, bool replaceUnresolved)
            {
                foreach (var line in lines)
                {
                    string text = line.Text.Trim();

                    // C-04: headers/footers are excluded by pattern, not by luck.
                    if (SubstatSlotter.IsHeaderLine(text)) continue;

                    var (slot, slotDist) = SubstatSlotter.NearestSlot(line.Y + line.Height / 2, slotCenters);
                    if (slotDist > slotMaxDistance)
                    {
                        orphanLines.Add(text);
                        continue;
                    }

                    string normalized = StatParser.NormalizeOcrArtifacts(text);

                    // Tesseract emits merged "Label Value" rows (e.g. "ATK 7.9%");
                    // WinOcr emits split label/value lines. Try the merged parse first.
                    var whole = StatParser.ParseLine(normalized);
                    if (whole != null)
                    {
                        if (slotWhole[slot] != null)
                        {
                            bool replaceable = replaceUnresolved && !SlotResolved(slot);
                            bool better = TunableRolls.ResolveDetailed(whole.Key, whole.Value).Value != null;
                            if (!replaceable || !better)
                            {
                                // C-04: two full rows never overwrite each other silently.
                                slotCollisions.Add(slot + 1);
                                continue;
                            }
                        }
                        slotWhole[slot] = whole;
                        slotConf[slot] = line.Confidence;
                        slotEngine[slot] = line.Engine;
                        continue;
                    }

                    // A split line whose slot already holds a full row is redundant.
                    if (slotWhole[slot] != null)
                    {
                        slotCollisions.Add(slot + 1);
                        continue;
                    }

                    // If line is a value (e.g. "8.4%", "150", "10.1%")
                    var valMatch = Regex.Match(normalized, @"^[-+]?\s*(\d+(?:[.,]\d+)?)\s*(%)?$");
                    if (valMatch.Success)
                    {
                        if (float.TryParse(valMatch.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                        {
                            slotValues[slot] = val;
                            slotPercents[slot] = valMatch.Groups[2].Success || normalized.Contains('%');
                            slotValStrs[slot] = normalized;
                            slotConf[slot] = line.Confidence;
                            slotEngine[slot] = line.Engine;
                        }
                    }
                    else
                    {
                        slotLabels[slot] = text;
                        slotConf[slot] = line.Confidence;
                        slotEngine[slot] = line.Engine;
                    }
                }
            }

            AssignLines(ocrLines, replaceUnresolved: false);

            int expectedSubs = EchoRules.ExpectedSubstatCount(level ?? 0);

            // F-38: when the primary pass leaves too few RESOLVED rows, the global Otsu
            // pass has eaten the labels (measured: idx042/043/085, where a local
            // threshold keeps them separable). Retry the same block with the adaptive
            // pass; retry rows still face every downstream check (key validity,
            // uniqueness, main-stat duplicate, legal roll) in the row-building pass.
            int resolvedPrimary = Enumerable.Range(0, 5).Count(SlotResolved);
            if (TesseractOcr.IsAvailable && resolvedPrimary < expectedSubs)
            {
                try
                {
                    using var altBmp = EchoFieldPreprocessor.ProcessAdaptive(panel, EchoRegions.SubstatsBlock);
                    var altLines = await TesseractOcr.RecognizeLinesWithBoundsAsync(
                        altBmp, ScannerConfig.SubstatBlockPsm, TesseractOcr.TextWhitelist);
                    if (altLines.Count > 0)
                    {
                        AssignLines(altLines, replaceUnresolved: true);
                        int resolvedAfter = Enumerable.Range(0, 5).Count(SlotResolved);
                        if (resolvedAfter > resolvedPrimary)
                            warnings.Add($"SubstatBlockRetry: primary pass resolved {resolvedPrimary}/{expectedSubs} " +
                                         $"substat slot(s); a local-threshold re-read recovered {resolvedAfter - resolvedPrimary}.");
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add("SubstatBlockRetry failed: " + ex.Message);
                }
            }

            // C-04: orphans and collisions are surfaced, never swallowed.
            if (orphanLines.Count > 0)
                warnings.Add($"OrphanLines: {orphanLines.Count} OCR line(s) fell outside every substat slot " +
                             "and were not assigned: " + string.Join(" | ", orphanLines.Take(6)));

            if (slotCollisions.Count > 0)
            {
                flags.Add("SlotCollision");
                warnings.Add($"SlotCollision: substat slot(s) {string.Join(",", slotCollisions.Distinct().OrderBy(x => x))} " +
                             "received competing OCR lines; kept the first full row.");
            }

            // C-07: legal substat keys only (data-driven from the roll tables), and
            // no key may repeat within one echo.
            // D-01: substat confidence comes from the engine's confidence for that row.
            float LineConf(int s) => slotConf[s] is double c ? (float)c : 0.5f;

            var seenSubstatKeys = new HashSet<StatKey>();
            bool TryAcceptSubstat(StatKey candidate)
            {
                if (!TunableRolls.IsValidSubstatKey(candidate))
                {
                    flags.Add("InvalidSubstatKey");
                    warnings.Add($"InvalidSubstatKey: '{candidate}' has no roll table, so it cannot be a " +
                                 "substat; the row was rejected.");
                    return false;
                }
                if (!seenSubstatKeys.Add(candidate))
                {
                    flags.Add("SubstatDuplicate");
                    warnings.Add($"SubstatDuplicate: '{candidate}' appeared more than once in this echo; " +
                                 "kept the first occurrence.");
                    return false;
                }
                return true;
            }

            // C-05: an unresolvable roll stays a PROPOSAL (flagged), never a silently
            // snapped value; E-02 will keep these out of exports.
            void NoteRollState(TunableRolls.RollResolution resolution, StatKey statKey)
            {
                switch (resolution.State)
                {
                    // F-43: EXACT is the BEST outcome - a proven legal roll. It used to
                    // fall into the default branch and be reported as "NotARoll: value is
                    // not a legal roll", so nearly every substat in every scan carried a
                    // false NotARoll flag/warning.
                    case TunableRolls.RollState.Exact:
                    case TunableRolls.RollState.Corrected:
                        break;   // proven / uniquely recoverable - not a review case
                    case TunableRolls.RollState.Ambiguous:
                        flags.Add("AmbiguousRoll");
                        warnings.Add($"AmbiguousRoll: '{statKey}' had {resolution.Candidates.Count} legal-roll " +
                                     $"candidates ({string.Join("/", resolution.Candidates)}); kept as a proposal.");
                        break;
                    default:
                        flags.Add("NotARoll");
                        warnings.Add($"NotARoll: '{statKey}' value is not a legal roll and has no unique " +
                                     "correction; kept as a proposal for review.");
                        break;
                }
            }

            // D-02 validation confidence: an exact roll is proven, a uniquely
            // corrected one is inferred from OCR confusion, anything else is not
            // acceptable evidence at all (the row stays a proposal).
            static float RollConfidence(TunableRolls.RollResolution r) => r.State switch
            {
                TunableRolls.RollState.Exact => 1.00f,
                TunableRolls.RollState.Corrected => 0.70f,
                _ => 0.00f,
            };

            var substats = new List<SubstatResult>();
            for (int s = 0; s < 5; s++)
            {
                // Merged "Label Value" row (Tesseract style): key+value in one hit.
                if (slotWhole[s] is { } whole)
                {
                    if (SubstatSlotter.IsMainStatDuplicate(mainStat, whole.Key, whole.Value)) continue; // drop only an exact main-stat duplicate
                    if (!TryAcceptSubstat(whole.Key)) continue;
                    if (whole.Key != StatKey.Hp && whole.Key != StatKey.HpPercent &&
                        StatPixelMatcher.DetectHp(substatsBmp, s, 5))
                    {
                        warnings.Add($"Substat slot {s + 1}: OCR={whole.Key} but pixel matcher sees HP — kept OCR.");
                    }
                    var rollW = TunableRolls.ResolveDetailed(whole.Key, whole.Value);
                    NoteRollState(rollW, whole.Key);
                    substats.Add(new SubstatResult(whole.Key.ToString(), whole.Value, rollW.Value,
                        Math.Min(LineConf(s), RollConfidence(rollW)), $"{whole.RawLabel} {whole.RawValue}"));
                    slotFilled[s] = true;
                    continue;
                }

                if (slotValues[s] == null) continue; // slot is empty (unleveled echo)
                float numVal = slotValues[s]!.Value;
                bool isPercent = slotPercents[s];
                string? lbl = slotLabels[s];

                StatKey? key = null;
                float conf = 0.88f;

                if (!string.IsNullOrWhiteSpace(lbl))
                {
                    string combined = isPercent ? $"{lbl} {numVal}%" : $"{lbl} {numVal}";
                    var parsed = StatParser.ParseLine(combined);
                    if (parsed != null) key = parsed.Key;
                }

                // Pixel fallback: OCR commonly misses short 2-letter "HP" labels without context
                if (key == null)
                {
                    if (StatPixelMatcher.DetectHp(substatsBmp, s, 5))
                    {
                        key = isPercent ? StatKey.HpPercent : StatKey.Hp;
                        lbl = isPercent ? "HP %" : "HP";
                        conf = 0.90f;
                    }
                }

                if (key != null)
                {
                    // Prevent main stat duplication in substats
                    if (SubstatSlotter.IsMainStatDuplicate(mainStat, key.Value, numVal)) continue;
                    if (!TryAcceptSubstat(key.Value)) continue;

                    // Pixel cross-check (§7): HP matcher runs on every slot in parallel
                    // with OCR. Disagreement is logged, never auto-overridden.
                    if (key != StatKey.Hp && key != StatKey.HpPercent &&
                        StatPixelMatcher.DetectHp(substatsBmp, s, 5))
                    {
                        warnings.Add($"Substat slot {s + 1}: OCR={key} but pixel matcher sees HP — kept OCR.");
                    }

                    var roll = TunableRolls.ResolveDetailed(key.Value, numVal);
                    NoteRollState(roll, key.Value);
                    var sub = new SubstatResult(key.Value.ToString(), numVal, roll.Value,
                        Math.Min(Math.Min(conf, LineConf(s)), RollConfidence(roll)), $"{lbl} {numVal}{(isPercent ? "%" : "")}");
                    substats.Add(sub);
                    slotFilled[s] = true;
                }
            }

            string rawSubstatsOcr = string.Join("\n", ocrLines.Select(l => $"[y={l.Y:F0}, x={l.X:F0}] {l.Text}"));


            // C-03: per-slot retry. A slot the union pass left empty gets its own
            // tuned rect re-read (pad 2px, SingleLine) - full line first, then a
            // value-only right crop (numbers) plus a label-only left crop (text).
            // Accepted only if the label is a valid substat key (C-07) and the
            // value is a legal roll (C-05). Cost guard: at most one retry per
            // missing slot, and only when Tesseract is actually available.
            int retryCount = 0, retryRecovered = 0;
            var retrySlots = new List<int>();
            if (TesseractOcr.IsAvailable)
            {
                static Bitmap CropFraction(Bitmap src, double x0, double x1)
                {
                    int left = (int)(src.Width * x0);
                    int w = Math.Max(1, (int)(src.Width * x1) - left);
                    var bmp = new Bitmap(w, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using var g = Graphics.FromImage(bmp);
                    g.DrawImage(src, new Rectangle(0, 0, w, src.Height),
                        new Rectangle(left, 0, w, src.Height), GraphicsUnit.Pixel);
                    return bmp;
                }

                for (int s = 0; s < expectedSubs && s < 5; s++)
                {
                    if (slotFilled[s]) continue;

                    retryCount++;
                    retrySlots.Add(s + 1);

                    using var slotBmp = EchoFieldPreprocessor.Process(
                        panel, EchoRegions.SubstatSlot(s + 1), FieldStrategy.Substat);

                    ParsedStat? candidate = null;

                    // (a) whole line, closed vocabulary
                    string lineText = await TesseractOcr.RecognizeAsync(
                        slotBmp, PageSegMode.SingleLine, TesseractOcr.TextWhitelist);
                    candidate = StatParser.ParseLine(StatParser.NormalizeOcrArtifacts(lineText));
                    if (candidate != null && !TunableRolls.IsValidSubstatKey(candidate.Key))
                        candidate = null;

                    // (b) label-only / value-only crops
                    if (candidate == null)
                    {
                        string labelText, valueText;
                        using (var labelBmp = CropFraction(slotBmp, 0.00, 0.65))
                            labelText = await TesseractOcr.RecognizeAsync(
                                labelBmp, PageSegMode.SingleLine, TesseractOcr.TextWhitelist);
                        using (var valueBmp = CropFraction(slotBmp, 0.70, 1.00))
                            valueText = await TesseractOcr.RecognizeAsync(
                                valueBmp, PageSegMode.SingleLine, TesseractOcr.NumberWhitelist);

                        string combined = (labelText.Trim() + " " + valueText.Trim()).Trim();
                        if (combined.Length > 0)
                        {
                            candidate = StatParser.ParseLine(StatParser.NormalizeOcrArtifacts(combined));
                            if (candidate != null && !TunableRolls.IsValidSubstatKey(candidate.Key))
                                candidate = null;
                        }
                    }

                    if (candidate == null) continue;
                    if (SubstatSlotter.IsMainStatDuplicate(mainStat, candidate.Key, candidate.Value)) continue;
                    if (seenSubstatKeys.Contains(candidate.Key)) continue;

                    var retryRoll = TunableRolls.ResolveDetailed(candidate.Key, candidate.Value);
                    if (!retryRoll.IsUsable) continue;   // C-05: only a legal roll is accepted

                    seenSubstatKeys.Add(candidate.Key);
                    slotFilled[s] = true;
                    retryRecovered++;
                    substats.Add(new SubstatResult(candidate.Key.ToString(), candidate.Value, retryRoll.Value,
                        Math.Min(0.80f, RollConfidence(retryRoll)),
                        $"retry: {candidate.RawLabel} {candidate.RawValue}"));
                    warnings.Add($"Substat slot {s + 1} recovered by C-03 retry: {candidate.RawLabel} {candidate.RawValue}.");
                }
            }

            if (retryCount > 0)
                _log?.Invoke($"    [OCR] Substat retries: {retryCount} slot(s) retried, {retryRecovered} recovered.");

            // D-06: substat provenance (engine + attempt).
            for (int s = 0; s < 5 && s < substats.Count; s++)
                diagnostics.Add(new EchoScanResult.FieldDiagnostics(
                    $"substat{s + 1}", slotEngine[s] ?? "?", "SingleBlock", "Substat",
                    retrySlots.Contains(s + 1) ? 2 : 1, rawSubstatsOcr, slotConf[s] ?? null, null));

            // C-01: the substat count must match the level rule min(level/5, 5).
            if (substats.Count < expectedSubs)
            {
                flags.Add("SubstatShort");
                warnings.Add($"SubstatShort: level {level} expects {expectedSubs} substats, found {substats.Count} " +
                             "(per-slot retry is TODO C-03).");
            }
            else if (substats.Count > expectedSubs)
            {
                // Excess: drop only entries that are invalid or duplicates - never blindly.
                int removed = substats.RemoveAll(s =>
                    !Enum.TryParse<StatKey>(s.Key, out var k) || k == StatKey.Unknown);
                var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                removed += substats.RemoveAll(s => !seenKeys.Add(s.Key));

                if (substats.Count > expectedSubs)
                {
                    flags.Add("SubstatExcess");
                    warnings.Add($"SubstatExcess: level {level} expects {expectedSubs} substats, found {substats.Count}" +
                                 (removed > 0 ? $" (removed {removed} invalid/duplicate entries)" : ""));
                }
            }


            // ── 9. Sonata detection ─────────────────────────────────────────
            // Primary: icon pixel-signature match (Tacet-Lab visual.ts port).
            // Fallback: Zone B OCR text parse of the "Sonata Effect" line.
            // Icon detection already ran before name matching (B-03); reuse it here.
            string? sonataName    = sonataIconName;
            float   sonataConf    = sonataIconConf;
            string? sonataSource  = sonataIconName != null ? "Icon" : null;
            string  rawSonataOcr  = sonataZoneOcr.Trim();

            var sonataLines = sonataZoneOcr
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

            int sonataHdrIdx = Array.FindIndex(sonataLines,
                l => l.Contains("Sonata", StringComparison.OrdinalIgnoreCase) &&
                     l.Contains("Effect", StringComparison.OrdinalIgnoreCase));

            // F-45: arbitrate the icon against the panel text. The icon matcher ALWAYS
            // returns a top-1 - including on families it cannot separate - so when the
            // panel text names a legal set, the text wins. When both are legal the game's
            // own label wins too; when only the icon is legal the icon stays.
            bool IconLegal() => nameEntry == null || sonataIconName == null ||
                nameEntry.Sonatas.Contains(sonataIconName, StringComparer.OrdinalIgnoreCase);
            bool TextLegal() => nameEntry == null || sonataTextName == null ||
                nameEntry.Sonatas.Contains(sonataTextName, StringComparer.OrdinalIgnoreCase);

            if (sonataName != null)
            {
                sonataSource = "Icon";
                warnings.Add($"Sonata from icon match: {sonataName} (conf {sonataConf:F2}).");

                if (sonataTextName != null && !string.Equals(sonataTextName, sonataName, StringComparison.OrdinalIgnoreCase))
                {
                    if (TextLegal() && !IconLegal())
                    {
                        warnings.Add($"SonataTextOverride: the icon said '{sonataName}', which is not a legal set " +
                                     $"for '{nameEntry?.Name}' (pool: {string.Join(", ", nameEntry?.Sonatas ?? [])}); " +
                                     $"the panel text says '{sonataTextName}' -> using the text.");
                        sonataName = sonataTextName;
                        sonataConf = (float)sonataTextConf;
                        sonataSource = "OcrText";
                        flags.Remove("NameContradictsEvidence");
                    }
                    else if (TextLegal())
                    {
                        warnings.Add($"SonataTextOverride: icon '{sonataName}' vs panel text '{sonataTextName}' " +
                                     $"(both legal for this echo); the game's own label is the text -> using '{sonataTextName}'.");
                        sonataName = sonataTextName;
                        sonataConf = (float)sonataTextConf;
                        sonataSource = "OcrText";
                    }
                    else
                    {
                        warnings.Add($"SonataTextDisagrees: panel text '{sonataTextName}' is not a legal set for " +
                                     $"'{nameEntry?.Name}'; kept the icon result '{sonataName}'.");
                    }
                }
            }
            else if (sonataHdrIdx >= 0 && sonataHdrIdx + 1 < sonataLines.Length)
            {
                // Take the line immediately after "Sonata Effect"
                string rawSonataCand = sonataLines[sonataHdrIdx + 1];
                // Strip OCR icon artifact patterns like "@ (2/2)", "@ (5/5)", "(2/2)" etc.
                string cleanedSonata = Regex.Replace(rawSonataCand, @"[@\(].*$", "").Trim();
                cleanedSonata = Regex.Replace(cleanedSonata, @"\s+", " ").Trim();

                if (!string.IsNullOrEmpty(cleanedSonata))
                {
                    // Fuzzy match against known sonata names
                    var (matched, score) = FuzzyMatcher.ClosestMatch(
                        cleanedSonata, GameDatabase.KnownSonatas, s => s, threshold: ScannerConfig.SonataTextMinConfidence);

                    if (matched != null)
                    {
                        sonataName = matched;
                        sonataConf = score;
                        sonataSource = "OcrText";
                        warnings.Add($"Sonata from OCR text: {sonataName} (conf {sonataConf:F2}).");
                    }
                    else
                    {
                        // Keep raw cleaned text as best guess with low confidence
                        sonataName = cleanedSonata;
                        sonataConf = 0.30f;
                        warnings.Add($"Sonata fuzzy match failed for: \"{cleanedSonata}\"");
                    }
                }
            }
            else if (sonataHdrIdx < 0)
            {
                // "Sonata Effect" header not found — try catalog fallback
                if (nameEntry?.Sonatas.Length > 0)
                {
                    sonataName = nameEntry.Sonatas[0];
                    sonataConf = 0.50f; // low confidence, catalog-inferred
                    sonataSource = "CatalogDefault";
                    warnings.Add("Sonata Effect header not found; using catalog default.");
                }
                else
                {
                    sonataSource = "None";
                    warnings.Add("Sonata Effect not detected.");
                }
            }

            // ── 10. Owner / Equipped By (Zone C OCR) ───────────────────────
            string? equippedBy = null;
            var ownerMatch = Regex.Match(ownerZoneOcr,
                @"Equipped\s+by\s+(.+)", RegexOptions.IgnoreCase);
            if (ownerMatch.Success)
                equippedBy = ownerMatch.Groups[1].Value.Trim();

            panel.Dispose();

            return new EchoScanResult
            {
                ImageFile   = Path.GetFileName(imagePath),
                ImagePath   = imagePath,
                ScannedAt   = DateTime.UtcNow,
                EchoName    = new FieldResult(
                                  nameEntry == null ? null
                                  : (namePhantom ? "Phantom: " + nameEntry.Name : nameEntry.Name),
                                  nameScore, nameOcr.Replace('\n',' ').Trim()),
                Cost        = new FieldResult(cost.HasValue ? (object?)cost.Value : null, costConfidence),
                Rarity      = new FieldResult(rarity, 1.0f, rarityProvenance),
                Level       = new FieldResult(level.HasValue ? (object?)level.Value : null,
                                              level.HasValue ? (float)(levelRead.Confidence ?? 0) : 0f, levelOcr.Trim()),
                Sonata      = new FieldResult(sonataName, sonataConf, rawSonataOcr),
                EquippedBy  = new FieldResult(equippedBy,
                                              equippedBy != null ? (float)(ownerRead.Confidence ?? 0) : 0f, ownerZoneOcr.Trim()),
                MainStatKey   = new FieldResult(mainStat?.Key.ToString(),
                                                mainStat != null ? (float)(mainRead.Confidence ?? 0) : 0f, mainStat?.RawLabel),
                MainStatValue = new FieldResult(mainStat != null ? (object?)mainStat.Value : null,
                                                mainStat != null ? (float)(mainRead.Confidence ?? 0) : 0f, mainStat?.RawValue),
                SecondMainStatKey   = new FieldResult(secondMainStat?.Key.ToString(),
                                                      secondMainStat != null ? (float)(secondMainRead.Confidence ?? 0) : 0f, secondMainStat?.RawLabel),
                SecondMainStatValue = new FieldResult(secondMainStat != null ? (object?)secondMainStat.Value : null,
                                                      secondMainStat != null ? (float)(secondMainRead.Confidence ?? 0) : 0f, secondMainStat?.RawValue),
                Substats    = substats,
                Flags       = flags,
                Diagnostics = diagnostics,
                SonataSource = sonataSource,
                SubstatRetries = retryCount,
                SubstatRetrySlots = retrySlots,
                SubstatRetriesRecovered = retryRecovered,
                RawNameOcr       = nameOcr.Trim(),
                RawMainStatOcr   = mainStatLine.Trim(),
                RawSecondMainStatOcr = secondMainStatLine.Trim(),
                RawSubstatsOcr   = rawSubstatsOcr,
                RawLevelOcr      = levelOcr.Trim(),
                RawSonataOcr     = rawSonataOcr,
                Errors   = errors,
                Warnings = warnings,
            };
        }
        catch (Exception ex)
        {
            panel.Dispose();
            return Error(imagePath, $"Unexpected error: {ex.Message}");
        }
    }


    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// OCR a region. Tesseract primary runs on field-preprocessed black-on-white
    /// input (<see cref="EchoFieldPreprocessor"/>); Windows OCR fallback runs on the
    /// legacy enhance+upscale path whose numbers are preserved as measured.
    /// Set <c>ScannerConfig.UseWindowsOcrFallback=false</c> for QA Tesseract-only runs.
    /// </summary>
    /// <summary>One region read together with its provenance (D-01/D-06).</summary>
    private sealed record RegionRead(
        string Text, double? Confidence, string Engine, string Psm, string Preprocess, int Attempt);

    private static async Task<RegionRead> OcrRegionAsync(
        Bitmap panel, RectangleF region, FieldStrategy strategy,
        PageSegMode mode, string? whitelist, Func<string, bool>? accept = null)
    {
        bool fallbackAllowed = ScannerConfig.UseWindowsOcrFallback;
        if (TesseractOcr.IsAvailable)
        {
            string t = "";
            double? tConf = null;
            bool attempted = false;
            try
            {
                using var tessBmp = EchoFieldPreprocessor.Process(panel, region, strategy);
                var (text, conf) = await TesseractOcr.RecognizeWithConfidenceAsync(tessBmp, mode, whitelist);
                t = text; tConf = conf;
                attempted = true;
                if (!fallbackAllowed)
                    return new RegionRead(t, tConf, "Tesseract", mode.ToString(), strategy.ToString(), 1);
                bool empty = ScannerConfig.FallbackOnEmptyTesseractResult &&
                    (string.IsNullOrWhiteSpace(t) || t.Trim().Length < ScannerConfig.OcrMinTextLength);
                if (!empty && (accept == null || accept(t)))
                    return new RegionRead(t, tConf, "Tesseract", mode.ToString(), strategy.ToString(), 1);
                // Empty or grammar-rejected: fall through to Windows OCR.
            }
            catch
            {
                if (!fallbackAllowed)
                    return new RegionRead(attempted ? t : string.Empty, tConf, "Tesseract", mode.ToString(), strategy.ToString(), 1);
                /* fall through to Windows OCR */
            }
            if (fallbackAllowed)
            {
                var w = await WinOcrRegionAsync(panel, region);
                // Prefer whichever attempt satisfies the grammar; Tesseract wins ties.
                if (accept == null) return w;
                if (accept(w.Text)) return w;
                if (accept(t)) return new RegionRead(t, tConf, "Tesseract", mode.ToString(), strategy.ToString(), 1);
                return w;
            }
            return new RegionRead(t, tConf, "Tesseract", mode.ToString(), strategy.ToString(), 1);
        }
        else if (!fallbackAllowed)
        {
            return new RegionRead(string.Empty, null, "none", mode.ToString(), strategy.ToString(), 1);
        }
        return await WinOcrRegionAsync(panel, region);
    }

    /// <summary>Legacy Windows-OCR path (EnhanceForOcr + Upscale2x).</summary>
    private static async Task<RegionRead> WinOcrRegionAsync(Bitmap panel, RectangleF region)
    {
        using var bmp = PreprocessForOcr(panel, region);
        // Windows OCR reports no confidence -> null, Engine=Win (D-01).
        return new RegionRead(await WinOcr.RecognizeAsync(bmp), null, "Win", "-", "WinOcr(EnhanceForOcr+Upscale2x)", 2);
    }

    /// <summary>
    /// Name-strip OCR with per-engine routing (<see cref="ScannerConfig.NameEngine"/>).
    /// Windows-only is the measured default for the stylized name font.
    /// </summary>
    private static async Task<RegionRead> OcrNameAsync(Bitmap panel)
    {
        switch (ScannerConfig.NameEngine)
        {
            case ScannerConfig.OcrEnginePreference.WindowsOnly:
                return await WinOcrRegionAsync(panel, EchoRegions.EchoName);
            case ScannerConfig.OcrEnginePreference.TesseractOnly:
                using (var tessBmp = EchoFieldPreprocessor.Process(panel, EchoRegions.EchoName, FieldStrategy.Name))
                {
                    var (text, conf) = await TesseractOcr.RecognizeWithConfidenceAsync(
                        tessBmp, ScannerConfig.NameRegionPsm, TesseractOcr.TextWhitelist);
                    return new RegionRead(text, conf, "Tesseract",
                        ScannerConfig.NameRegionPsm.ToString(), FieldStrategy.Name.ToString(), 1);
                }
            default:
                return await OcrRegionAsync(panel, EchoRegions.EchoName,
                    FieldStrategy.Name, ScannerConfig.NameRegionPsm, TesseractOcr.TextWhitelist);
        }
    }

    private static Bitmap PreprocessForOcr(Bitmap panel, System.Drawing.RectangleF region)
    {
        using var crop = EchoRegions.CropRegion(panel, region);
        using var enhanced = ImagePreprocessor.EnhanceForOcr(crop);
        return ImagePreprocessor.Upscale2x(enhanced);
    }

    private static async Task<(List<OcrLineInfo> Lines, double RefHeight)> OcrLinesAsync(Bitmap panel)
    {
        bool fallbackAllowed = ScannerConfig.UseWindowsOcrFallback;
        if (TesseractOcr.IsAvailable)
        {
            try
            {
                // Substats as one uniform multi-line block on the new text path.
                using var tessBmp = EchoFieldPreprocessor.Process(panel, EchoRegions.SubstatsBlock, FieldStrategy.Substat);
                var lines = await TesseractOcr.RecognizeLinesWithBoundsAsync(
                    tessBmp, ScannerConfig.SubstatBlockPsm, TesseractOcr.TextWhitelist);
                if (!fallbackAllowed)
                    return (lines, tessBmp.Height);
                // Quality gate: every substat row carries digits. A digit-free
                // result means Tesseract's segmentation failed on this crop —
                // fall through to Windows OCR instead of serving junk lines.
                bool usable = lines.Count > 0
                    && lines.Any(l => !string.IsNullOrWhiteSpace(l.Text))
                    && lines.Any(l => l.Text.Any(char.IsDigit));
                if (!ScannerConfig.FallbackOnEmptyTesseractResult || usable)
                    return (lines, tessBmp.Height);
                // else fall through to Windows OCR
            }
            catch
            {
                if (!fallbackAllowed)
                    return ([], 1);
                /* fall through to Windows OCR */
            }
        }
        else if (!fallbackAllowed)
        {
            return ([], 1);
        }
        using var crop = EchoRegions.CropRegion(panel, EchoRegions.SubstatsBlock);
        using var enhanced = ImagePreprocessor.EnhanceForOcr(crop);
        using var up = ImagePreprocessor.Upscale2x(enhanced);
        double h = up.Height;
        var wlines = await WinOcr.RecognizeLinesWithBoundsAsync(up);
        return (wlines, h);
    }

    /// <summary>
    /// F-45: read the set NAME out of the panel's "Sonata Effect" text. The icon alone
    /// cannot separate some families (measured: Halo of Starry Radiance vs Pact of
    /// Neonlight Leap, margin 0.0009 on captures the panel text proves are Pact), while
    /// the game prints the set name in this zone on every echo. Scans every OCR line
    /// (the header line itself often misses OCR) and returns the best known-set match.
    /// </summary>
    private static (string? Name, double Confidence) SonataFromZoneText(string zoneOcr, double threshold)
    {
        string? best = null;
        double bestScore = 0;
        foreach (string raw in (zoneOcr ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // strip icon-glyph prefixes ("DO ", "OD ", "D ") and anything from "&"/"("/
            // "@" onwards (the "(1/5)" counters and the effect prose).
            string cleaned = Regex.Replace(raw, @"^[^A-Za-z]*", "");
            cleaned = Regex.Replace(cleaned, @"^[A-Z]{1,3}\s+", "");
            cleaned = Regex.Replace(cleaned, @"[&@(].*$", "").Trim();
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            if (cleaned.Length < 4) continue;

            var (matched, score) = FuzzyMatcher.ClosestMatch(cleaned, GameDatabase.KnownSonatas, s => s, (float)threshold);
            if (matched != null && score > bestScore)
            {
                best = matched;
                bestScore = score;
            }
        }
        return (best, bestScore);
    }

    private static string CleanOcrText(string ocr)
        => Regex.Replace(ocr, @"[^\w\s%\.\-]", " ").Trim();

    /// <summary>
    /// D-03: the level strip must parse as a WHOLE. The old anywhere-match happily took
    /// the "1" out of "Lv.15 (+3)" or the "25" out of "25+7" - a partial match is not
    /// evidence. Accepts "+25" / "Lv.15" / "Level 3"; everything else is null.
    /// </summary>
    public static int? ParseLevel(string ocr)
    {
        string s = (ocr ?? "").Trim();
        s = Regex.Replace(s, @"^(?:Lv\.?|Level)\s*", "", RegexOptions.IgnoreCase).Trim();

        var m = Regex.Match(s, @"^\+?\s*(\d{1,2})$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out int v) && v is >= 0 and <= 25) return v;
        return null;
    }

    private static int? ParseCost(string ocr)
    {
        var m = Regex.Match(ocr, @"Cost\s*([134])", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int v)) return v;
        return null;
    }

    private static EchoScanResult Error(string path, string msg) => new()
    {
        ImageFile  = Path.GetFileName(path),
        ImagePath  = path,
        ScannedAt  = DateTime.UtcNow,
        Errors     = [msg],
    };
}
