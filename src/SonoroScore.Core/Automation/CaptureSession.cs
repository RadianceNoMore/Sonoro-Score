// © 2026 RadianceNoMore (Sonoro-Score, MIT).
// One place that defines where a capture run lands: <root>/session_<timestamp>/.
// The scanner CLI, the review studio and the new GUI all discover sessions by this
// convention, so the naming lives here.

using System;
using System.IO;

namespace AlephalSonata.Automation;

public static class CaptureSession
{
    public static string CreateSessionDirectory(string imagesRoot)
    {
        string dir = Path.Combine(imagesRoot, $"session_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
