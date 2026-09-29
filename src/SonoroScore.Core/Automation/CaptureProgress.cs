// © 2026 RadianceNoMore (Sonoro-Score, MIT).
// Structured capture progress for any front-end (console, WinForms): one report per
// captured card, plus the mode enum shared by the CLI menu and the new GUI.

namespace AlephalSonata.Automation;

/// <summary>How a capture run reaches the echo grid.</summary>
public enum CaptureMode
{
    /// <summary>Auto-navigation: focus the game, open the character menu -> echo tab ->
    /// slot, then page/capture the picker grid. ("Overworld" in the GUI.)</summary>
    Overworld,

    /// <summary>The game is already showing the echo grid; only page and capture.
    /// ("Character screen" in the GUI.)</summary>
    CharacterScreen,
}

/// <summary>One captured card. Page/Row/Col are 0-based; Captured is the running
/// 1-based count; FilePath is the saved PNG.</summary>
public readonly record struct CaptureProgress(
    int Page, int Row, int Col, int Captured, int MaxPages, string FilePath);
