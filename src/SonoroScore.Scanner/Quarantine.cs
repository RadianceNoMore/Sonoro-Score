// Ac 2026 RadianceNoMore (Sonoro-Score, MIT).
// E-03: quarantined echoes are written next to the export so nothing is refused
// silently - every refusal carries its reasons and the image it came from.

using System.Text.Json;

namespace SonoroScore.Scanner;

public static class Quarantine
{
    public const string FilePrefix = "quarantine_";

    public static string ToJson(IEnumerable<ExportRejection> rejections, string note,
                                int totalScans, int exported)
    {
        var list = rejections.ToList();
        var payload = new
        {
            GeneratedAt = DateTime.UtcNow,
            Note = note,
            TotalScans = totalScans,
            Exported = exported,
            Quarantined = list.Count,
            Entries = list
                .Select(r => new { r.ImageFile, Reasons = r.Reasons })
                .ToList(),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Write quarantine_&lt;timestamp&gt;.json into <paramref name="dir"/> and return its path.</summary>
    public static async Task<string> SaveAsync(string dir, IEnumerable<ExportRejection> rejections,
                                               string note, int totalScans, int exported)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{FilePrefix}{DateTime.Now:yyyyMMdd_HHmmss}.json");
        await File.WriteAllTextAsync(path, ToJson(rejections, note, totalScans, exported));
        return path;
    }
}
