# SS GUI rework - the landing scanner window

Status: **implemented (Phase 1 + 2)** - live capture/OCR needs the game running.
Owner-specified; this doc is the frozen spec the code follows.

## Goal

One simple, friendly window that runs the whole flow: capture -> OCR -> export/review.
It replaces the AlephalSonata console menu as the entry point; the review studio and
the OCR/export engines are unchanged.

## Layout (as shipped)

    +----------------------------------------------------------------------+
    | [SS logo]         |  Scans your WuWa echo stats                      |
    |  SonoroScore      |  +-- YELLOW BOX (wider right side) -------------+|
    |  Ready /          |  | virtual-HID warning (Interception)           ||
    |  Need to open WuWa|  | backend: active / fallback + elevation state ||
    |                   |  +----------------------------------------------+|
    | [ START SCAN ]    |  Source: (o) Overworld  ( ) Character screen     |
    |                   |  Pages to scroll: [3]                            |
    +----------------------------------------------------------------------+
    | [==================== OCR bar =====================]                 |
    | OCR - idle / Capturing page 3 ... / OCR 24 % (343/1418)              |
    +----------------------------------------------------------------------+
    |            [ Export this scan -> Tacet-Lab ] [ Open Review Studio ]  |
    +----------------------------------------------------------------------+

## Behaviour (frozen)

- Left column: logo + status (`Ready` / `Need to open WuWa`, polled; `Capturing...`,
  `Reading panels...`, `Done`, `Stopped`, `Error` while running).
- Right column: one-line description + the yellow notice (virtual-HID warning,
  live backend + elevation status).
- Source modes:
  - **Overworld** (default): SS navigates - focus game -> character menu -> echo tab
    -> slot -> page/capture the picker grid.
  - **Character screen**: the game already shows the echo grid; only page + capture.
- **Pages to scroll** defaults to 3; capture ends early at the last page.
- ESC / the button stops after the current card. On capture end **or interrupt** the
  SS window snaps back to the foreground and OCR starts automatically on whatever
  was captured.
- OCR progress: percent bar + `OCR x % (done/total)`.
- Bottom actions enable when a scan produced results:
  - **Export this scan -> Tacet-Lab**: strict policy, writes
    `tacet-lab-backup_<ts>.json` into the session folder (+ quarantine file when
    something is refused).
  - **Open Review Studio**: opens `MainReviewForm`; it keeps its single rule -
    load the newest session under `<app dir>/sessions` (legacy fallback:
    `publish/AlephalSonata/aleph_images`), blank when none exists.
- Sessions are written to `<app dir>/sessions/session_<timestamp>/`.
- The window is resizable and remembers its size/position in `window-state.json`
  next to the exe. The version is shown bottom-right; SS carries its own version
  (`1.0.0`), separate from AS.

## Implementation map

- `src/SonoroScore/ScannerForm.cs` - the window (presentation + wiring only).
  Landing logo = `src/SS_Quaver.jpg` (copied next to the exe); the app icon is
  `assets/SS.ico` (built from `src/SS_icon.jpg`, wired via `<ApplicationIcon>`).
- `src/SonoroScore/Program.cs` - lands on `ScannerForm`.
- `SonoroScore.Core` additions: `CaptureNaming` (echo_pXX_rYY_cZZ_idxNNN.png),
  `CaptureSession` (session_<timestamp> folder), `CaptureProgress`/`CaptureMode`,
  and `AutoNavigator.CaptureEchoGridAsync` now reports `IProgress<CaptureProgress>`.
- `SonoroScore.Scanner` additions: `ScanProgress` + `InlineProgress<T>` +
  `EchoRecognizer.ScanDirectoryAsync` (same enumeration as the CLI, progress per image).
- Tests: `CaptureNamingTests`, `ScanProgressTests` (125 total green).

## Not in v1 (deliberate)

- Advanced timing calibration stays in the AlephalSonata console menu.
- Only the Tacet-Lab export is wired (per owner).
- Review/verify behaviour unchanged.
- Live capture loop needs a real game session to validate (HID input cannot be
  meaningfully automated in tests).