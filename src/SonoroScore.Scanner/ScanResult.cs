using System.Text.Json.Serialization;

namespace SonoroScore.Scanner;

// ── Scan result types ─────────────────────────────────────────────────────────

/// <summary>A recognized field with its raw OCR evidence and a confidence score [0,1].</summary>
public record FieldResult(
    object? Value,
    float Confidence,
    string? Raw = null)
{
    /// Convenience typed accessor — returns null if Value is null or wrong type.
    public T? As<T>() => Value is T v ? v : default;
}

public record SubstatResult(
    string Key,
    float Value,
    float? SnappedValue,
    float Confidence,
    string? Raw = null);

/// <summary>
/// Full structured result from scanning one echo image.
/// Serialized to JSON in the test-suite output.
/// </summary>
public record EchoScanResult
{
    public required string ImageFile       { get; init; }
    public required string ImagePath       { get; init; }
    public required DateTime ScannedAt    { get; init; }

    // ── Identified fields ──────────────────────────────────────────────────
    public FieldResult?  EchoName     { get; init; }
    public FieldResult?  Cost         { get; init; }
    public FieldResult?  Rarity       { get; init; }
    public FieldResult?  Level        { get; init; }
    public FieldResult?  Sonata       { get; init; }
    public FieldResult?  EquippedBy   { get; init; }
    public FieldResult?  MainStatKey  { get; init; }
    public FieldResult?  MainStatValue{ get; init; }
    public FieldResult?  SecondMainStatKey  { get; init; }
    public FieldResult?  SecondMainStatValue{ get; init; }
    public List<SubstatResult> Substats { get; init; } = [];

    /// <summary>
    /// Structured gate flags (B-03/B-04 down-payment; formalised by D-06).
    /// e.g. <c>NameContradictsEvidence</c>, <c>CostNameMismatch</c>.
    /// </summary>
    public List<string> Flags { get; init; } = [];

    /// <summary>How many per-slot retries this scan spent (C-03 cost guard).</summary>
    public int SubstatRetries { get; init; }

    /// <summary>1-based slot indices that were retried (C-03).</summary>
    public List<int> SubstatRetrySlots { get; init; } = [];

    /// <summary>How many of those retries produced an accepted row.</summary>
    public int SubstatRetriesRecovered { get; init; }

    /// <summary>
    /// Per-field provenance (D-06): which engine, which page-segmentation mode,
    /// which preprocessing path, which attempt, the raw text it saw and the
    /// confidence it produced. Machine consumers read THIS, never <c>Warnings</c>.
    /// </summary>
    public sealed record FieldDiagnostics(
        string Field,
        string? Engine,
        string? Psm,
        string? Preprocess,
        int Attempt,
        string? RawText,
        double? Confidence,
        string? Note);

    public List<FieldDiagnostics> Diagnostics { get; init; } = [];

    /// <summary>
    /// D-06: how the sonata was determined - "Icon" | "OcrText" | "CatalogDefault"
    /// | "None". Replaces grepping the human-readable warnings.
    /// </summary>
    public string? SonataSource { get; init; }

    // ── Debug evidence ─────────────────────────────────────────────────────
    public string? RawNameOcr          { get; init; }
    public string? RawMainStatOcr      { get; init; }
    public string? RawSecondMainStatOcr{ get; init; }
    public string? RawSubstatsOcr      { get; init; }
    public string? RawLevelOcr         { get; init; }
    public string? RawSonataOcr        { get; init; }

    // ── Errors / warnings ─────────────────────────────────────────────────
    public List<string> Errors         { get; init; } = [];
    public List<string> Warnings       { get; init; } = [];

    /// <summary>Substat count the echo's level calls for (C-01).</summary>
    [JsonIgnore]
    public int ExpectedSubstats => EchoRules.ExpectedSubstatCount(Level?.Value is int lvl ? lvl : 0);

    /// <summary>True when the substat count matches the level rule (C-01/C-02).</summary>
    [JsonIgnore]
    public bool SubstatsSatisfyLevel => Substats.Count == ExpectedSubstats;

    /// <summary>
    /// Aggregate review gate (C-02, expanded by D-06): any error, any blocking
    /// flag, a missing core field, or a substat count that contradicts the level
    /// rule. Replaces the old "&gt;= 4" heuristic that hid bugs.
    /// </summary>
    [JsonIgnore]
    public bool NeedsReview
        => Errors.Count > 0
           || EchoName?.Value == null
           || MainStatKey?.Value == null
           || Flags.Count > 0
           || !SubstatsSatisfyLevel
           || HasFieldBelowReviewThreshold;

    /// <summary>
    /// D-02: true when any present field's composed confidence is below
    /// <see cref="ScannerConfig.ReviewBelow"/>. Absent fields are not counted here
    /// (missing fields are handled by the checks above).
    /// </summary>
    [JsonIgnore]
    public bool HasFieldBelowReviewThreshold
        => Below(EchoName) || Below(Cost) || Below(Level) || Below(Sonata)
           || Below(MainStatValue) || Below(SecondMainStatValue)
           || Substats.Any(s => s.Confidence < ScannerConfig.ReviewBelow);

    private static bool Below(FieldResult? field)
        => field?.Value != null && field.Confidence < ScannerConfig.ReviewBelow;
}


/// <summary>
/// Aggregated summary across all images in a test session.
/// </summary>
public record ScanSessionResult
{
    public required string SessionPath  { get; init; }
    public required DateTime RunAt      { get; init; }
    public required string DatabaseVersion { get; init; }
    public int TotalImages              { get; init; }
    public int SuccessfulScans         { get; init; }
    public int CompleteEchoes          { get; init; }
    public float NameDetectionRate     { get; init; }
    public float MainStatDetectionRate { get; init; }
    public float SecondMainStatDetectionRate { get; init; }
    public float SubstatAvg            { get; init; }
    public required List<EchoScanResult> Results { get; init; }
}
