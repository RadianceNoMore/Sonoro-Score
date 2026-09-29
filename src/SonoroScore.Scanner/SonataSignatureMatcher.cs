// © 2026 RadianceNoMore (Sonoro-Score, MIT).
// Sonata matcher adapted from Tacet-Lab (https://github.com/DJ12421/Tacet-Lab, GPL-3.0).
// Port of Tacet-Lab src/scanner/visual.ts (sonataSignaturesInBox,
// pixelSignature, classifySonataCandidates) to C# / System.Drawing.
// See NOTICES.md.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;

namespace SonoroScore.Scanner;

/// <summary>
/// Icon pixel-signature matcher for Sonata sets.
///
/// The sonata icon is cropped from the echo detail panel, then compared against
/// 256-element normalized signatures (one per sonata set) using an L1-distance
/// score. Mirrors Tacet-Lab's multi-scale / multi-offset "signatures in box"
/// sampling so slight calibration differences do not tank accuracy.
/// </summary>
public static class SonataSignatureMatcher
{
    /// <summary>Tacet-Lab <c>scannerSignatureVersion</c> this port was extracted from.</summary>
    public const string ExpectedSignatureVersion = "3.6";

    /// <summary>
    /// Best-effort hosted location for refreshed signatures, mirroring the
    /// <c>GameDatabase.ApiUrl</c> pattern (<c>static.nanoka.cc/ww/3.6/…</c>).
    /// Pass a custom URL to <see cref="EnsureUpdatedAsync"/> to override.
    /// </summary>
    public const string DefaultSignatureUrl =
        "https://static.nanoka.cc/ww/3.6/sonata_signatures.json";

    public const string SignatureFileName = "sonata_signatures.json";

    /// <summary>
    /// F-40: templates extracted from OUR OWN verified captures live here and take
    /// precedence when present, because the shipped reference icons are cropped and
    /// rendered differently and land every set inside a narrow, coin-flip score band.
    /// </summary>
    public const string LocalSignatureFileName = "sonata_signatures_local.json";

    private sealed record Template(string Name, double[] Signature);

    private static readonly double[] Scales = [0.45, 0.60, 0.75, 0.90, 1.00];
    private static readonly double[] XSteps = [0.00, 0.25, 0.50, 0.75, 1.00];
    private static readonly double[] YSteps = [0.00, 0.50, 1.00];

    private static readonly object _lock = new();
    private static Template[]? _cachedTemplates;
    private static string? _cachedVersion;
    private static bool _loaded;

    public static bool IsLoaded
    {
        get
        {
            EnsureLoaded();
            return _cachedTemplates is { Length: > 0 };
        }
    }

    /// <summary>Version string stored in the signature JSON (<c>null</c> = legacy array file).</summary>
    public static string? LoadedVersion
    {
        get
        {
            EnsureLoaded();
            return _cachedVersion;
        }
    }

    public static int TemplateCount
    {
        get
        {
            EnsureLoaded();
            return _cachedTemplates?.Length ?? 0;
        }
    }

    /// <summary>The file a <see cref="CheckVersion"/>-style report should name as primary.</summary>
    private static string SignaturePath
    {
        get
        {
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LocalSignatureFileName);
            return File.Exists(local) ? local : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SignatureFileName);
        }
    }

    /// <summary>F-40: the raw captured samples for one icon crop (scale-major, 75 of them).</summary>
    public static List<double[]> CapturedSignatures(Bitmap iconCrop)
    {
        EnsureLoaded();
        return iconCrop.Width < 2 || iconCrop.Height < 2 ? [] : SignaturesInBox(iconCrop);
    }

    /// <summary>
    /// F-40: write templates in the SAME wire format as the shipped file (values are
    /// the [-2,2] signature scaled by 32, because the loader divides by 32).
    /// </summary>
    public static void SaveTemplates(string path, IEnumerable<(string Name, double[] Signature)> templates, string version)
    {
        var payload = new
        {
            version,
            scannerSignatureVersion = version,
            signatures = templates
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .Select(t => new
                {
                    name = t.Name,
                    signature = t.Signature
                        .Select(v => (int)Math.Round(Math.Clamp(v, -2.0, 2.0) * 32.0))
                        .ToArray(),
                })
                .ToArray(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload));
    }

    private static void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded) return;

            // F-40/F-41: the local file SUPPLEMENTS the shipped reference set rather than
            // replacing it - a locally extracted template wins for its set, and every set
            // we have no local samples for keeps the shipped template. (Replacing the whole
            // set would silently delete coverage for uncaptured sets.)
            var shipped = LoadFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SignatureFileName));
            var local = LoadFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LocalSignatureFileName));

            var merged = new Dictionary<string, Template>(StringComparer.OrdinalIgnoreCase);
            foreach (var tpl in shipped.Templates) merged[tpl.Name] = tpl;
            foreach (var tpl in local.Templates) merged[tpl.Name] = tpl;

            _cachedTemplates = merged.Values.ToArray();
            _cachedVersion = local.Templates.Length > 0
                ? $"{local.Version ?? "local"}+{shipped.Version ?? "shipped"} ({local.Templates.Length} local / {shipped.Templates.Length} shipped)"
                : shipped.Version;
            _loaded = true;
        }
    }

    /// <summary>Forget cached templates so the next <see cref="Match"/> re-reads the file.</summary>
    public static void Reload()
    {
        lock (_lock)
        {
            _loaded = false;
            _cachedTemplates = null;
            _cachedVersion = null;
        }
    }

    /// <summary>
    /// Priority 4 – signature version check. Logs a warning when the loaded
    /// signature version does not match <see cref="ExpectedSignatureVersion"/>
    /// or the game version in <c>GameDatabase.DataVersion</c>.
    /// Call once at scanner startup; returns false on mismatch.
    /// </summary>
    public static bool CheckVersion(Action<string>? log = null)
    {
        EnsureLoaded();
        string? ver = _cachedVersion;
        if (ver == null)
        {
            log?.Invoke("[SIG] sonata_signatures.json has no version field (legacy array format); " +
                        $"expected '{ExpectedSignatureVersion}'. Consider re-exporting with a version.");
            return false;
        }

        if (ver.StartsWith("local", StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke($"[SIG] Locally extracted templates in use: '{ver}' ({TemplateCount} sets) - " +
                        "preferred over the shipped reference set (F-40).");
            return true;
        }

        bool ok = ver == ExpectedSignatureVersion &&
                  GameDatabase.DataVersion.Contains(ver, StringComparison.Ordinal);
        if (!ok)
        {
            log?.Invoke($"[SIG] WARNING: signature version '{ver}' does not match " +
                        $"expected '{ExpectedSignatureVersion}' (GameDatabase.DataVersion={GameDatabase.DataVersion}). " +
                        $"Sonata icon matches may be stale; run EnsureUpdatedAsync or re-extract from Tacet-Lab.");
            return false;
        }

        log?.Invoke($"[SIG] Signature version OK: '{ver}' ({TemplateCount} sets).");
        return true;
    }

    /// <summary>
    /// Priority 4 – auto-update signatures (same pattern as <c>GameDatabase.LoadAsync</c>).
    /// Downloads the versioned signature JSON to the exe directory when
    /// <paramref name="forceRefresh"/> is set or the local file is missing.
    /// Returns true when a usable local file exists afterwards.
    /// </summary>
    public static async Task<bool> EnsureUpdatedAsync(
        string? url = null,
        bool forceRefresh = false,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        string path = SignaturePath;
        if (!forceRefresh && File.Exists(path))
            return true;

        string signatureUrl = string.IsNullOrWhiteSpace(url) ? DefaultSignatureUrl : url;
        try
        {
            log?.Invoke($"[SIG] Fetching sonata signatures from {signatureUrl} ...");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            string json = await http.GetStringAsync(signatureUrl, ct);
            // Validate shape before overwriting the good local copy.
            var (templates, version) = ParseJson(json);
            if (templates.Length == 0)
            {
                log?.Invoke("[SIG] Downloaded signature JSON contained 0 templates; keeping local file.");
                return File.Exists(path);
            }

            await File.WriteAllTextAsync(path, json, ct);
            log?.Invoke($"[SIG] Saved {templates.Length} signatures" +
                        (version != null ? $" (version {version})" : "") + $" to {path}.");
            Reload();
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[SIG] Signature update failed ({ex.Message}); using local file.");
            return File.Exists(path);
        }
    }

    private static (Template[] Templates, string? Version) LoadFile(string path)
    {
        if (!File.Exists(path))
            return ([], null);

        try
        {
            string json = File.ReadAllText(path);
            return ParseJson(json);
        }
        catch
        {
            return ([], null);
        }
    }

    /// <summary>
    /// Accepts both the versioned object
    /// <c>{ "version": "3.6", "signatures": [...] }</c> (also
    /// <c>scannerSignatureVersion</c>) and the legacy bare array
    /// <c>[{ "name", "signature" }, …]</c>.
    /// </summary>
    private static (Template[] Templates, string? Version) ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        string? version = null;
        JsonElement array = root;

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("version", out var v) ||
                root.TryGetProperty("scannerSignatureVersion", out v))
            {
                version = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
            }

            if (root.TryGetProperty("signatures", out var s) && s.ValueKind == JsonValueKind.Array)
                array = s;
            else
                return ([], version); // object without a signatures array
        }

        if (array.ValueKind != JsonValueKind.Array)
            return ([], version);

        var list = new List<Template>();
        foreach (var el in array.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (!el.TryGetProperty("name", out var n)) continue;
            if (!el.TryGetProperty("signature", out var raw)) continue;
            string name = n.GetString() ?? "";
            if (string.IsNullOrEmpty(name) || raw.ValueKind != JsonValueKind.Array) continue;
            var sig = new double[raw.GetArrayLength()];
            int i = 0;
            foreach (var v in raw.EnumerateArray())
            {
                // Tacet-Lab loadSonataSignatures() divides stored int8 by 32.
                sig[i++] = v.GetDouble() / 32.0;
            }
            if (sig.Length == 256)
                list.Add(new Template(name, sig));
        }

        return (list.ToArray(), version);
    }

    /// <summary>
    /// Match the sonata icon crop against the loaded signature templates.
    /// Returns the best set name and a confidence in [0,1]; Name is null when
    /// the signature file is missing or the icon crop is unusable.
    /// </summary>
    /// <summary>
    /// F-33 diagnostic: score EVERY template for this icon crop, best first. Lets a
    /// real capture be inspected with the real metric instead of re-implementing it.
    /// </summary>
    public static List<(string Name, double Score)> RankAll(Bitmap iconCrop)
    {
        var ranked = new List<(string Name, double Score)>();
        EnsureLoaded();
        var templates = _cachedTemplates ?? [];
        if (templates.Length == 0 || iconCrop.Width < 2 || iconCrop.Height < 2) return ranked;

        var captured = SignaturesInBox(iconCrop);
        if (captured.Count == 0) return ranked;

        foreach (var tpl in templates)
        {
            double score = double.NegativeInfinity;
            foreach (var sig in captured)
            {
                double s = 1.0 - L1(tpl.Signature, sig) / (sig.Length * 4.0);
                if (s > score) score = s;
            }
            ranked.Add((tpl.Name, score));
        }

        ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
        return ranked;
    }

    public static (string? Name, double Confidence) Match(Bitmap iconCrop)
    {
        var m = MatchDetailed(iconCrop);
        return (m.Name, m.Confidence);
    }

    /// <summary>
    /// F-33: the match plus the runner-up margin. The whole template set scores inside a
    /// narrow band, so the MARGIN - not the absolute score - carries the discriminating
    /// information; a thin margin means the winner is a coin flip.
    /// </summary>
    public static (string? Name, double Confidence, double Margin, string? RunnerUpName) MatchDetailed(Bitmap iconCrop)
    {
        EnsureLoaded();
        var templates = _cachedTemplates ?? [];
        if (templates.Length == 0 || iconCrop.Width < 2 || iconCrop.Height < 2)
            return (null, 0.0, 0.0, null);

        var captured = SignaturesInBox(iconCrop);
        if (captured.Count == 0)
            return (null, 0.0, 0.0, null);

        string? bestName = null, runnerUpName = null;
        double bestScore = double.NegativeInfinity;
        double runnerUp = double.NegativeInfinity;

        foreach (var tpl in templates)
        {
            double score = double.NegativeInfinity;
            foreach (var sig in captured)
            {
                double s = 1.0 - L1(tpl.Signature, sig) / (sig.Length * 4.0);
                if (s > score) score = s;
            }

            if (score > bestScore)
            {
                runnerUp = bestScore;
                runnerUpName = bestName;
                bestScore = score;
                bestName = tpl.Name;
            }
            else if (score > runnerUp)
            {
                runnerUp = score;
                runnerUpName = tpl.Name;
            }
        }

        double minimumScore = templates.Length == 1 ? 0.42 : 0.48;
        double margin = double.IsNegativeInfinity(runnerUp) ? 1.0 : bestScore - runnerUp;
        if (bestScore < minimumScore) return (null, 0.0, margin, runnerUpName);

        double confidence = Math.Min(0.96, 0.55 + bestScore * 0.4);
        return (bestName, confidence, margin, runnerUpName);
    }

    private static double L1(double[] a, double[] b)
    {
        double sum = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) sum += Math.Abs(a[i] - b[i]);
        return sum;
    }

    // ── Port of sonataSignaturesInBox ───────────────────────────────────────

    private static List<double[]> SignaturesInBox(Bitmap image)
    {
        int boxX = 0, boxY = 0;
        int boxWidth = image.Width, boxHeight = image.Height;
        double shortestSide = Math.Max(1, Math.Min(boxWidth, boxHeight));

        var signatures = new List<double[]>();

        foreach (double scale in Scales)
        {
            double size = shortestSide * scale;
            double xTravel = Math.Max(0, boxWidth - size);
            double yTravel = Math.Max(0, boxHeight - size);

            foreach (double xStep in XSteps)
            {
                foreach (double yStep in YSteps)
                {
                    int sx = (int)Math.Round(boxX + xTravel * xStep);
                    int sy = (int)Math.Round(boxY + yTravel * yStep);
                    int sw = Math.Max(1, (int)Math.Round(size));
                    int sh = Math.Max(1, (int)Math.Round(size));

                    sx = Math.Clamp(sx, 0, Math.Max(0, image.Width - 1));
                    sy = Math.Clamp(sy, 0, Math.Max(0, image.Height - 1));
                    sw = Math.Clamp(sw, 1, image.Width - sx);
                    sh = Math.Clamp(sh, 1, image.Height - sy);

                    var resized = Resize16(image, sx, sy, sw, sh);
                    signatures.Add(PixelSignature(resized));
                    resized.Dispose();
                }
            }
        }

        return signatures;
    }

    private static Bitmap Resize16(Bitmap src, int sx, int sy, int sw, int sh)
    {
        var dst = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.Clear(Color.Transparent);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, new Rectangle(0, 0, 16, 16), sx, sy, sw, sh, GraphicsUnit.Pixel);
        return dst;
    }

    // ── Port of pixelSignature ──────────────────────────────────────────────

    private static double[] PixelSignature(Bitmap bmp16)
    {
        var values = new double[256];
        int i = 0;
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                var c = bmp16.GetPixel(x, y);
                values[i++] = c.A < 40
                    ? 0.0
                    : (c.R * 0.2126 + c.G * 0.7152 + c.B * 0.0722) / 255.0;
            }
        }

        double mean = 0;
        foreach (var v in values) mean += v;
        mean /= values.Length;

        double variance = 0;
        foreach (var v in values) variance += (v - mean) * (v - mean);
        variance /= values.Length;
        double deviation = Math.Sqrt(variance);
        if (deviation == 0) deviation = 1;

        for (int j = 0; j < values.Length; j++)
            values[j] = Math.Max(-2, Math.Min(2, (values[j] - mean) / deviation));

        return values;
    }
}