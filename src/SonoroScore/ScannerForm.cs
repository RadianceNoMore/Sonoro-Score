// © 2026 RadianceNoMore (Sonoro-Score, MIT).
// The new SS landing window (docs/gui-rework.md): one friendly screen that drives
// capture -> OCR -> export/review. Engine work happens in SonoroScore.Core
// (AutoNavigator/CaptureSession) and SonoroScore.Scanner (ScanDirectoryAsync);
// this file is presentation + wiring only.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlephalSonata.Automation;
using AlephalSonata.Native;
using SonoroScore.Scanner;

namespace SonoroScore;

public sealed class ScannerForm : Form
{
    /// <summary>Where capture sessions live: &lt;app dir&gt;\sessions\session_&lt;ts&gt;.
    /// The review studio discovers the same folder (with a fallback to the legacy
    /// AlephalSonata images root).</summary>
    private static readonly string SessionRoot = Path.Combine(AppContext.BaseDirectory, "sessions");

    private readonly Label _status = new();
    private Label _pagesLabel = new();
    private readonly TextBox _log = new();
    private readonly Label _version = new();
    private readonly Label _desc = new();
    private readonly Panel _yellow = new();
    private readonly Label _warning = new();
    private readonly Label _backend = new();
    private readonly Button _start = new();
    private readonly RadioButton _modeOverworld = new();
    private readonly RadioButton _modeCharScreen = new();
    private readonly NumericUpDown _pages = new();
    private readonly ProgressBar _bar = new();
    private readonly Label _progressLabel = new();
    private readonly Button _export = new();
    private readonly Button _studio = new();
    private readonly System.Windows.Forms.Timer _poll = new();

    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _backendOk;      // Interception virtual-HID driver present
    private bool _tesseractOk;    // Tesseract engine + tessdata usable
    private List<EchoScanResult>? _results;
    private string? _sessionDir;

    public ScannerForm()
    {
        Text = "SonoroScore — automatic echo scanner";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimumSize = new Size(676, 507);          // design size incl. frame
        ClientSize = new Size(660, 468);            // window ~676x507 = exactly 4:3
        RestoreWindowState();
        BackColor = Color.White;
        KeyPreview = true;

        BuildLayout();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && _running) StopScan(); };

        _poll.Interval = 800;
        _poll.Tick += (_, _) => RefreshStatusLine();
        _poll.Start();
        RefreshStatusLine();
        LayoutControls();   // controls were built at the design size; apply the real one
    }

    // ── layout ────────────────────────────────────────────────────────────────

    private void BuildLayout()
    {
        // Left column: logo + status
        // Left column: the logo sits directly on the form (no frame, no wordmark);
        // the status line touches its bottom edge.
        void AddLogoFallback()
        {
            var mark = new Label
            {
                Text = "SS", ForeColor = Color.FromArgb(40, 90, 170), BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 28, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(31, 14), Size = new Size(134, 134),
            };
            Controls.Add(mark);
        }

        string logoPath = Path.Combine(AppContext.BaseDirectory, "SS_Quaver.jpg");
        bool logoShown = false;
        if (File.Exists(logoPath))
        {
            try
            {
                using var img = Image.FromFile(logoPath);
                var pic = new PictureBox
                {
                    Image = new Bitmap(img),   // detach from the file so it stays replaceable
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Location = new Point(31, 14), Size = new Size(134, 134),
                };
                Controls.Add(pic);
                logoShown = true;
            }
            catch { /* fall back to the text mark */ }
        }
        if (!logoShown) AddLogoFallback();

        // ProductVersion carries a "+<git sha>" informational suffix - show just the number.
        _version.Text = "v" + Application.ProductVersion.Split('+')[0];
        _version.ForeColor = Color.FromArgb(130, 130, 130);
        _version.Font = new Font("Segoe UI", 8.5f);
        _version.TextAlign = ContentAlignment.MiddleRight;
        _version.Size = new Size(60, 18);
        Controls.Add(_version);

        _status.Text = "Ready";
        _status.Location = new Point(18, 148);   // bottom edge (170) = START button top
        _status.Size = new Size(160, 22);
        _status.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _status.ForeColor = Color.FromArgb(40, 130, 60);
        Controls.Add(_status);

        // Right column: one-line description + the yellow notice
        _desc.Text = "Scans your WuWa echo stats";
        _desc.Location = new Point(198, 22);
        _desc.Size = new Size(444, 24);
        _desc.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        Controls.Add(_desc);

        _yellow.Location = new Point(198, 50);
        _yellow.Size = new Size(444, 100);
        _yellow.BackColor = Color.FromArgb(255, 246, 214);
        _yellow.BorderStyle = BorderStyle.FixedSingle;
        _warning.Text =
            "\u26a0  SS drives the game with a virtual-HID input driver (Interception).\r\n" +
            "    Keep Wuthering Waves focused while scanning \u2014 don't touch mouse or keyboard.\r\n" +
            "    Synthetic input to the game can be blocked unless SS runs as Administrator.";
        _warning.Location = new Point(8, 6);
        _warning.Size = new Size(428, 66);
        _warning.Font = new Font("Segoe UI", 8.5f);
        _backend.Location = new Point(8, 72);
        _backend.Size = new Size(428, 22);
        _backend.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        _yellow.Controls.Add(_backend);
        _yellow.Controls.Add(_warning);
        Controls.Add(_yellow);

        // Row: Start (left) | source + pages (right)
        _start.Text = "\u25b6  START SCAN";
        _start.Location = new Point(18, 170);
        _start.Size = new Size(170, 52);   // stays clear of the Source:/yellow column (x=198)
        _start.Font = new Font("Segoe UI", 12, FontStyle.Bold);
        _start.BackColor = Color.FromArgb(40, 130, 60);
        _start.ForeColor = Color.White;
        _start.FlatStyle = FlatStyle.Flat;
        _start.Click += async (_, _) => { if (_running) StopScan(); else await StartScanAsync(); };
        Controls.Add(_start);

        var srcLabel = new Label { Text = "Source:", Location = new Point(198, 160), Size = new Size(60, 20), Font = new Font("Segoe UI", 9) };
        _modeOverworld.Text = "Overworld \u2014 SS navigates";
        _modeOverworld.Location = new Point(258, 158);
        _modeOverworld.Size = new Size(200, 22);
        _modeOverworld.Checked = true;
        _modeCharScreen.Text = "Character screen \u2014 just scan";
        _modeCharScreen.Location = new Point(258, 182);
        _modeCharScreen.Size = new Size(200, 22);
        Controls.Add(srcLabel);
        Controls.Add(_modeOverworld);
        Controls.Add(_modeCharScreen);

        _pagesLabel = new Label { Text = "Pages to scroll:", Location = new Point(420, 160), Size = new Size(100, 20), Font = new Font("Segoe UI", 9) };
        _pages.Location = new Point(524, 157);
        _pages.Size = new Size(70, 24);
        _pages.Minimum = 1;
        _pages.Maximum = 500;
        _pages.Value = 3;
        Controls.Add(_pagesLabel);
        Controls.Add(_pages);

        // Live status log - fills the middle so there is no dead white space, and
        // shows the capture/OCR process while it runs.
        _log.Location = new Point(18, 230);
        _log.Size = new Size(624, 130);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.WordWrap = false;
        _log.BackColor = Color.FromArgb(250, 250, 250);
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.Font = new Font("Consolas", 8.5f);
        Controls.Add(_log);

        // OCR bar
        _bar.Location = new Point(18, 244);
        _bar.Size = new Size(700, 24);
        _bar.Minimum = 0;
        _bar.Maximum = 100;
        Controls.Add(_bar);

        _progressLabel.Text = "OCR \u2014 idle";
        _progressLabel.Location = new Point(18, 272);
        _progressLabel.Size = new Size(740, 20);
        _progressLabel.Font = new Font("Segoe UI", 9);
        Controls.Add(_progressLabel);

        // Bottom actions
        _export.Text = "\u2913  Export to Tacet Lab";
        _export.Location = new Point(172, 320);   // centered pair: 250 + 10 + 175 = 435
        _export.Size = new Size(250, 40);
        _export.Enabled = false;
        _export.Click += async (_, _) => await ExportAsync();
        _studio.Text = "\u25a3  Open Review Studio";
        _studio.Location = new Point(432, 320);
        _studio.Size = new Size(175, 40);
        _studio.Enabled = true;   // works any time: newest session or blank (same rule as always)
        _studio.Click += (_, _) => new MainReviewForm().Show();
        Controls.Add(_export);
        Controls.Add(_studio);
    }

    private void AppendLog(string line)
    {
        if (_log.TextLength > 20000) _log.Clear();   // keep it bounded on huge runs
        _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + "\r\n");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    // ── resizable layout + remembered window state ───────────────────────────

    private const int MarginX = 18;
    private static readonly string StatePath = Path.Combine(AppContext.BaseDirectory, "window-state.json");

    private sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized);

    /// <summary>Keep the design's edges: right column, progress bar and the bottom
    /// button pair follow the window when it is resized.</summary>
    private void LayoutControls()
    {
        int right = ClientSize.Width - MarginX;

        _desc.Width = right - 198;
        _yellow.Width = right - 198;
        _warning.Width = _yellow.Width - 16;
        _backend.Width = _yellow.Width - 16;

        _pagesLabel.Left = right - 170;
        _pages.Left = right - 70;

        _bar.Width = right - MarginX;
        _progressLabel.Width = right - MarginX;

        const int pairWidth = 250 + 10 + 175;
        _export.Left = (ClientSize.Width - pairWidth) / 2;
        _studio.Left = _export.Left + 260;

        // Footer hugs the bottom: no dead white space under the buttons.
        int bottom = ClientSize.Height;
        _export.Top = _studio.Top = bottom - 58;      // 18 margin + 40 button
        _progressLabel.Top = bottom - 86;
        _bar.Top = bottom - 110;

        _version.Left = right - _version.Width;
        _version.Top = bottom - 22;

        // log fills the middle between the options and the progress bar
        _log.Width = right - MarginX;
        _log.Height = Math.Max(40, (_bar.Top - 12) - _log.Top);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutControls();
    }

    private void RestoreWindowState()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var state = System.Text.Json.JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(StatePath));
            if (state == null || state.Width < MinimumSize.Width || state.Height < MinimumSize.Height) return;

            var rect = new Rectangle(state.X, state.Y, state.Width, state.Height);
            bool onScreen = false;
            foreach (var screen in Screen.AllScreens)
                if (screen.WorkingArea.IntersectsWith(rect)) { onScreen = true; break; }
            if (!onScreen) return;

            StartPosition = FormStartPosition.Manual;
            Location = new Point(state.X, state.Y);
            Size = new Size(state.Width, state.Height);
            if (state.Maximized) WindowState = FormWindowState.Maximized;
        }
        catch { /* a broken state file must never block the app */ }
    }

    private void SaveWindowState()
    {
        try
        {
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var state = new WindowPlacement(bounds.X, bounds.Y, bounds.Width, bounds.Height,
                                        WindowState == FormWindowState.Maximized);
            File.WriteAllText(StatePath, System.Text.Json.JsonSerializer.Serialize(state));
        }
        catch { /* best effort */ }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveWindowState();
        base.OnFormClosing(e);
    }

    // ── status polling (idle only) ────────────────────────────────────────────

    private void RefreshStatusLine()
    {
        bool game = WindowManager.FindGameWindow() != IntPtr.Zero;

        // Mandatory capabilities (owner requirement): the Interception virtual-HID
        // driver for input and the Tesseract OCR stack. The app refuses to scan
        // without either - there is no degraded input/engine mode.
        _backendOk = false;
        string backend;
        try
        {
            using var input = new InputSimulator();
            _backendOk = input.UsingVirtualHid;
            backend = _backendOk
                ? "\u25cf Interception virtual-HID active"
                : "\u25cf Interception driver NOT detected \u2014 required (install it, then relaunch)";
        }
        catch (Exception ex)
        {
            backend = "\u25cf input backend error: " + ex.Message;
        }

        _tesseractOk = TesseractOcr.IsAvailable;
        string ocr = _tesseractOk
            ? ""
            : "   |   \u25cf Tesseract unavailable (" + (TesseractOcr.UnavailableReason ?? "missing tessdata?") + ")";

        bool admin = IsAdmin();
        _backend.Text = backend + ocr + "   |   " + (admin ? "elevated \u2713" : "NOT elevated (UIPI may block input)");
        _backend.ForeColor = (_backendOk && _tesseractOk && admin)
            ? Color.FromArgb(30, 90, 30)
            : Color.FromArgb(180, 40, 40);

        if (!_running)
        {
            if (!_backendOk)
            {
                _status.Text = "Interception required";
                _status.ForeColor = Color.FromArgb(180, 40, 40);
            }
            else if (!_tesseractOk)
            {
                _status.Text = "Tesseract required";
                _status.ForeColor = Color.FromArgb(180, 40, 40);
            }
            else
            {
                _status.Text = game ? "Ready" : "Need to open WuWa";
                _status.ForeColor = game ? Color.FromArgb(40, 130, 60) : Color.FromArgb(200, 120, 0);
            }
            _start.Enabled = _backendOk && _tesseractOk;
        }
    }

    private static bool IsAdmin()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // ── the scan flow ─────────────────────────────────────────────────────────

    private async Task StartScanAsync()
    {
        RefreshStatusLine();
        if (!_backendOk || !_tesseractOk)
        {
            string missing = !_backendOk ? "the Interception driver is not active" : "the Tesseract engine is unavailable";
            MessageBox.Show(this,
                "SS cannot scan: " + missing + ".\n\nBoth are required - install/repair and relaunch.",
                "Missing requirement", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (WindowManager.FindGameWindow() == IntPtr.Zero)
        {
            _status.Text = "Need to open WuWa";
            _status.ForeColor = Color.FromArgb(200, 120, 0);
            return;
        }

        _running = true;
        _start.Text = "\u25a0  STOP (Esc)";
        _start.BackColor = Color.FromArgb(160, 60, 40);
        _export.Enabled = false;
        _studio.Enabled = false;
        _bar.Value = 0;
        _sessionDir = CaptureSession.CreateSessionDirectory(SessionRoot);
        _results = null;
        _cts = new CancellationTokenSource();
        _log.Clear();

        var captureProgress = new Progress<CaptureProgress>(p =>
        {
            _status.Text = "Capturing\u2026";
            _status.ForeColor = Color.FromArgb(40, 90, 170);
            _progressLabel.Text = $"Capturing page {p.Page + 1} \u2014 row {p.Row + 1} col {p.Col + 1} \u2014 {p.Captured} card(s) saved";
            AppendLog($"captured p{p.Page + 1} r{p.Row + 1} c{p.Col + 1}  ({p.Captured} total)");
        });
        var ocrProgress = new Progress<ScanProgress>(p =>
        {
            _bar.Value = p.Total == 0 ? 0 : (int)(100.0 * p.Done / p.Total);
            _status.Text = "Reading panels\u2026";
            _progressLabel.Text = $"OCR {(_bar.Value)} %  ({p.Done} / {p.Total})  \u2014 {p.ImageFile}";
            string nm = p.Result.EchoName?.Value as string ?? "?";
            AppendLog($"ocr {p.Done}/{p.Total}  {p.ImageFile}  \u2192  {nm}");
        });

        var pages = (int)_pages.Value;
        var overworld = _modeOverworld.Checked;
        var sessionDir = _sessionDir!;
        var ct = _cts.Token;

        AppendLog("session: " + sessionDir);
        AppendLog((overworld ? "mode: Overworld (SS navigates)" : "mode: Character screen (in place)")
                  + $"  \u00b7  pages: {pages}");

        try
        {
            await Task.Run(async () =>
            {
                // 1. capture
                using var input = new InputSimulator();
                var navigator = new AutoNavigator(input);

                if (!await WindowManager.EnsureForegroundAsync())
                    throw new InvalidOperationException("Could not focus the Wuthering Waves window.");

                if (overworld)
                {
                    bool ready = await navigator.NavigateToEchoPickerAsync(ct);
                    if (!ready)
                        throw new InvalidOperationException("Auto-navigation could not reach the echo grid (Alt+Tab safety?).");
                }

                await navigator.CaptureEchoGridAsync(
                    sessionDir, maxPages: pages, progress: captureProgress, ct: ct);
            }, CancellationToken.None).ConfigureAwait(true);

            // 2. snap back + OCR (only counts cards actually saved)
            await SnapBackAsync();
            _bar.Value = 0;
            _progressLabel.Text = "OCR \u2014 starting\u2026";

            await Task.Run(async () =>
            {
                var catalog = await GameDatabase.LoadAsync();
                var recognizer = new EchoRecognizer(catalog);
                _results = await recognizer.ScanDirectoryAsync(sessionDir, ocrProgress, ct: CancellationToken.None);
            }, CancellationToken.None).ConfigureAwait(true);

            int total = _results?.Count ?? 0;
            int flagged = _results?.Count(r => r.NeedsReview) ?? 0;
            _bar.Value = 100;
            _status.Text = "Done";
            _status.ForeColor = Color.FromArgb(40, 130, 60);
            _progressLabel.Text = $"OCR 100 % \u2014 {total} echoes read, {flagged} flagged for review \u2014 {sessionDir}";
            AppendLog($"done: {total} echoes read, {flagged} flagged");
            _export.Enabled = total > 0;
        }
        catch (OperationCanceledException)
        {
            // Spec: capture complete OR interrupted -> snap back AND run OCR on what we got.
            await SnapBackAsync();
            _status.Text = "Stopped \u2014 reading captured pages\u2026";
            _status.ForeColor = Color.FromArgb(200, 120, 0);
            try
            {
                await Task.Run(async () =>
                {
                    var catalog = await GameDatabase.LoadAsync();
                    var recognizer = new EchoRecognizer(catalog);
                    _results = await recognizer.ScanDirectoryAsync(sessionDir, ocrProgress, ct: CancellationToken.None);
                }, CancellationToken.None).ConfigureAwait(true);
                int read = _results?.Count ?? 0;
                _progressLabel.Text = $"Stopped \u2014 OCR finished on the captured pages ({read} echoes).";
            }
            catch (Exception ex)
            {
                _progressLabel.Text = "Stopped \u2014 OCR after interrupt failed: " + ex.Message;
            }
        }
        catch (Exception ex)
        {
            await SnapBackAsync();
            _status.Text = "Error";
            _status.ForeColor = Color.FromArgb(180, 40, 40);
            _progressLabel.Text = ex.Message;
        }
        finally
        {
            _running = false;
            _cts?.Dispose();
            _cts = null;
            _start.Text = "\u25b6  START SCAN";
            _start.BackColor = Color.FromArgb(40, 130, 60);
            _studio.Enabled = true;                                    // always usable when idle
            _export.Enabled = (_results?.Count ?? 0) > 0;              // export needs scan results
        }
    }

    private void StopScan()
    {
        _progressLabel.Text = "Stopping after the current card\u2026";
        _cts?.Cancel();
    }

    /// <summary>Bring the SS window back to the foreground when capture ends or is
    /// interrupted, before OCR starts (spec: "snapped the windows back").</summary>
    private Task SnapBackAsync()
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
            {
                Win32.SetForegroundWindow(Handle);
                Activate();
            }
        }
        catch { /* best effort - never fail the scan over window focus */ }
        return Task.CompletedTask;
    }

    // ── export ────────────────────────────────────────────────────────────────

    private async Task ExportAsync()
    {
        if (_results == null || _results.Count == 0 || _sessionDir == null) return;
        try
        {
            string json = TacetLabExporter.ExportScans(_results, ExportPolicy.Strict, out int skipped, out var rejected);
            string path = Path.Combine(_sessionDir, $"tacet-lab-backup_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            await File.WriteAllTextAsync(path, json);

            string note = "";
            if (rejected.Count > 0)
            {
                string qPath = await Quarantine.SaveAsync(_sessionDir, rejected, "ScannerForm export", _results.Count, _results.Count - skipped);
                note = $"\n\n{rejected.Count} echo(es) were refused (see {Path.GetFileName(qPath)}).";
            }
            MessageBox.Show(this, $"Exported {_results.Count - skipped} echoes to:\n{path}{note}",
                "Tacet-Lab export", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (ExportSchemaException ex)
        {
            MessageBox.Show(this, "Export aborted - the payload failed its schema self-check:\n\n" + ex.Message,
                "Tacet-Lab export", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Export failed:\n\n" + ex.Message,
                "Tacet-Lab export", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
