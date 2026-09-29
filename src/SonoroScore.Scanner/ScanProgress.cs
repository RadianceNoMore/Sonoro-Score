// © 2026 RadianceNoMore (Sonoro-Score, MIT).
// Scan progress plumbing for front-ends (GUI/CLI). Additive: no existing API changed.

using System;

namespace SonoroScore.Scanner;

/// <summary>Reported once per processed image; Done ends equal to Total.</summary>
public sealed record ScanProgress(int Done, int Total, string ImageFile, EchoScanResult Result);

/// <summary>An IProgress that invokes synchronously on the reporting thread - what a
/// console front-end wants (Progress&lt;T&gt; would hop through the thread pool).</summary>
public sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;
    public InlineProgress(Action<T> handler) => _handler = handler;
    public void Report(T value) => _handler(value);
}
