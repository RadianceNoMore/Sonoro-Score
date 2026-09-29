# Sonoro-Score 🎵

> **C# (.NET 8) automation & echo scanner suite for Wuthering Waves.**
> Point it at the game and it captures your echo inventory, reads every panel with
> OCR, and hands you a Tacet-Lab-ready backup — with a review studio to verify
> and correct every read.

[![Platform](https://img.shields.io/badge/Platform-Windows-0078D6?logo=windows)](https://github.com/RadianceNoMore/Sonoro-Score)
[![Framework](https://img.shields.io/badge/.NET-8.0_Desktop-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Release](https://img.shields.io/badge/release-ss--v1.0.0-2ea44f)](https://github.com/RadianceNoMore/Sonoro-Score/releases)

---

## 🚀 How to run it

**You need:** Windows 10/11 (x64) · Wuthering Waves at **1080p** (16:9) ·
the Interception driver (one-time install, bundled) · Administrator rights
(SS asks for them — required to send input to the game) · the free
**.NET Desktop Runtime 8 (x64)** — only for the regular zip (the `-bundled`
zip has it inside). It is a ~60 MB one-time install
(`winget install Microsoft.DotNet.DesktopRuntime.8` or the link in the zip's
`README-FIRST.txt`).

### 1 · Get the app
Download from
[Releases](https://github.com/RadianceNoMore/Sonoro-Score/releases) and extract it
anywhere. There are two flavors: the regular zip is a **~22 MB** download and needs
the one-time .NET Desktop Runtime install above; the **`-bundled`** zip is bigger
(~85 MB) but carries the runtime inside — zero installs, ideal for sharing.

### 2 · Scan
1. Launch **`SonoroScore.exe`** and accept the Administrator prompt.
2. In the game, open the **echo inventory** screen.
3. In SS press **START SCAN** — it captures and reads every echo panel by itself.
4. **Leave the PC alone while it scans.** Synthetic input to the game can be
   blocked otherwise; moving the mouse/keyboard cancels it, and **Alt+Tab always
   stops it safely** (whatever is already captured still gets OCR'd).
5. When it's done: open the **review studio** to check the readings, then
   **Export to Tacet Lab**.

The yellow box in SS tells you the state at a glance — it must read
*Interception virtual-HID active* with both gates (Interception + Tesseract) green
before START SCAN unlocks.

### Where things go
- Captures + results: `<app folder>\sessions\session_<timestamp>\`
- Tacet-Lab export: `tacet-lab-backup_<timestamp>.json` in that session folder —
  import it in Tacet-Lab (top-bar Export/Restore).

### If something's off
| Symptom | Fix |
| :--- | :--- |
| START SCAN disabled, yellow box says **Interception required** | Install the bundled Interception driver and reboot (exact command in the zip's `README-FIRST.txt`). |
| Window says **Tesseract required** | The extract is incomplete — re-extract the zip without deleting files. |
| Scan starts, nothing moves in the game | Run SS as Administrator; keep the game focused during the scan. It's calibrated for **1080p, 16:9** (Borderless or Fullscreen). |
| Studio says "No scan results found" | Point it at the session's `scan_results_*.json`, or reopen it from SS's **Open Review Studio** (it loads the newest session automatically). |

---

## ✦ What it is

| Component | Executable | Role |
| :--- | :--- | :--- |
| **`SonoroScore` (SS)** | `SonoroScore.exe` | The app: live scan (auto-navigation + capture + OCR), review studio, Tacet-Lab / GOOD exports. |
| **`AlephalSonata` (AS)** | `AlephalSonata.exe` | Diagnostic console: window inspection, verbose auto-navigation tracing, raw dataset capture, OCR test-suite runs (`logs/aleph_trace_*.log`). |
| `SonoroScore.Scanner` | *(library)* | The recognition pipeline — panel extraction, hybrid OCR, icon-signature matching, roll snapping, exporters. |
| `SonoroScore.Scanner.Cli` | *(dev tool)* | Batch runner over an image folder: detection rates, scan + export JSONs. |
| `SonoroScore.Core` | *(library)* | Automation primitives — window focus tracking, Interception virtual-HID input, click jitter, scroll bursts, Alt+Tab safety guard. |

---

## ✦ Scanner pipeline

Per echo screenshot (1080p echo-detail panel):

1. **Panel extraction** — crops the right-side detail strip (`EchoRegions`, all rects
   panel-relative and tunable in-app).
2. **Hybrid OCR** — Tesseract 5 (LSTM-only, DPI 300, per-region page-segmentation
   modes) for stats; echo names route to the Windows OCR engine (measured better on
   the stylized name font). Neither engine is optional in shipped builds.
3. **Field preprocessing** — per-region pipeline ported from Tacet-Lab: ×3 enlarge,
   grayscale, percentile normalization, polarity correction, Otsu threshold — plus an
   adaptive-threshold retry that only fires for slots that failed to resolve.
4. **Sonata** — 16×16 pixel-signature matching over 34 sets, with panel-text
   arbitration for families the templates cannot separate.
5. **Rarity & cost** — direct pixel classification, no OCR.
6. **Parsing** — fuzzy catalog matching against live Nanoka data (with local cache),
   stat-label parsing with OCR-artifact recovery, substat values snapped to legal
   tunable rolls, row slotting by Y position.

Every field that cannot be resolved is **flagged for review — never invented**.

### Verified accuracy

| Set | Result |
| :--- | :--- |
| 300-echo owner-verified corpus | sonata / main / second-main / substats **100 %**, names 300/300, **zero corrections** needed in the final scoring loop |
| 1500-image generalisation set | names **1500/1500**, 0 sonata outside catalog pools; remaining flags are genuine data-quality items |
| Random-100 & random-200 audits vs. independent vision reads | every field-level disagreement adjudicated in the pipeline's favour except one real gap, which became the F-47 fix |
| Tacet-Lab import validation | passes Tacet-Lab's own `validateAccount` — 0/300 rejected |
| Automated tests | 125/125 green |


---

## ✦ Review & export

**Review studio** (opens from SS — it auto-loads the newest session):

- **📁 Open Folder** — loads echo images together with the folder's scan results.
- **📄 Load Scan JSON** — relinks screenshots by file name if paths went stale.
- **💾 Save Verified JSON** — persists your verified/corrected fields next to the session.
- **Editor** — identity, main + second-main stat, five tuned substat rows with roll
  validation, raw OCR evidence per field, search/filter, keyboard flow
  (`A`/`D` navigate, `Space` verify, `Ctrl+S` save).
- **⚡ Re-Scan Current** — re-runs the pipeline on one echo with the live catalog.
- **◈ Areas** — draggable region config over the current panel; saves to
  `regions.override.json` next to the exe (delete or Reset to restore defaults).

**Exports:**

- **Tacet-Lab (lossless, primary):** `tacet-lab-backup.json` (schema v7) — restore
  in Tacet-Lab via the top bar. Exports are validated against Tacet-Lab's import
  rules before they leave the app.
- **Export to Tacet Lab (SS landing):** one click for the whole just-finished scan.
- **GOOD (best-effort bridge):** `sonoro-good.json` (GOOD v3 envelope + `wuwa*`
  lossless fields). The WuWa→GOOD mapping is approximate — prefer Tacet-Lab.

---

## ✦ Build from source (developers)

**Prereqs:** Windows x64 + [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
The Tesseract model is pinned in-repo (`assets/tessdata/eng.traineddata`, copied
automatically into every build output) and native leptonica/tesseract DLLs come from
the `Tesseract` NuGet package — dev and release run the **same** OCR stack.

```powershell
# run from source
dotnet run --project src/SonoroScore/SonoroScore.csproj

# tests (unit + 25-fixture 1080p regression corpus)
dotnet test src/SonoroScore.Scanner.Tests/SonoroScore.Scanner.Tests.csproj -c Release

# batch scanner over a capture folder (+ exports)
dotnet run --project src/SonoroScore.Scanner.Cli/SonoroScore.Scanner.Cli.csproj -c Release -- `
  --dir <folder> --export-tacet-auto --export-good-auto

# self-contained single-file release builds
dotnet publish src/SonoroScore/SonoroScore.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ./publish/SonoroScore
dotnet publish src/AlephalSonata/AlephalSonata.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ./publish/AlephalSonata

# release zip (self-checks that the OCR stack + catalog are inside)
python scripts/make-release-zip.py --version 1.0.0 --publish ./publish/SonoroScore
```

**CLI QA flags:** `--limit N`, `--refresh`, `--out <path>`, `--no-winocr-fallback`,
`--name-engine auto|tesseract|windows`, `--diag-tess`, `--dump-lines <img>`.
**Emergency halt:** `Ctrl+C` in console, or Alt+Tab — automation stops on focus loss.

---

## ✦ Repository layout

```
Sonoro-Score/
├── SonoroScore.sln
├── Directory.Build.props            # shared version (1.7.0; SS carries its own 1.0.0)
├── NOTICES.md                       # third-party licenses (see Credits below)
├── assets/                          # pinned OCR model, icons, Interception package
├── dist/                            # release zips + notes (git-ignored)
├── src/
│   ├── SonoroScore.Core/            # window, Interception HID, jitter, navigator
│   ├── SonoroScore.Scanner/         # pipeline: EchoRecognizer, regions, OCR engines,
│   │                                # sonata signatures, catalog, exporters,
│   │                                # echo_catalog.json (shipped cache snapshot)
│   ├── SonoroScore.Scanner.Cli/     # batch runner + diagnostics
│   ├── SonoroScore.Scanner.Tests/   # xUnit + 1080p fixture corpus
│   ├── SonoroScore/                 # the app (landing scanner + review studio)
│   └── AlephalSonata/               # diagnostic debugger + capture tool
├── publish/                         # built exes (git-ignored)
└── logs/                            # trace logs (git-ignored)
```

---

## ✦ Credits & licenses

Grateful thanks to the projects this one builds on:

- **[Tacet-Lab](https://github.com/DJ12421/Tacet-Lab)** by **DJ12421** — *GPL-3.0*.
  The scanner architecture this project ports: region layout, OCR pipeline shape
  (preprocessing strategy, engine parameters), sonata icon-signature matcher +
  signature data, stat alias / roll tables, fixture methodology, and the Tacet-Lab
  backup envelope (schema v7). Adapted portions are GPL-3.0 — details in `NOTICES.md`.
- **[Nanoka](https://ww.nanoka.cc/)** — *fan datamine site*. Live echo catalog
  (names, costs, sonata sets) and roll data. A cache snapshot ships for offline
  first launch; the app refreshes it from Nanoka's API when possible.
- **[Interception](https://github.com/oblitum/Interception)** by **Francisco Lopes
  (oblitum)** — *LGPL-3.0 for non-commercial use*; commercial use needs a separate
  license from the author. The official package with its license texts is bundled
  verbatim under `Interception/` in the release zip; SS calls `interception.dll`
  at runtime to inject input through the driver.
- **[Tesseract OCR](https://github.com/tesseract-ocr/tesseract)** (*Apache-2.0*) and
  **[Leptonica](http://leptonica.org/)** (*BSD-2-Clause*) — the OCR engine and its
  native libraries (via the community `Tesseract` NuGet package).
- **[.NET 8 / Windows Forms](https://dotnet.microsoft.com/)** — Microsoft, *MIT*.

**License:** original code is **MIT** (`LICENSE`). Portions adapted from Tacet-Lab
are **GPL-3.0**; the combined scanner work that links them must be distributed under
GPL-compatible terms — see `NOTICES.md` before re-publishing binaries elsewhere.

*Sonoro-Score is a fan-made open-source tool, not affiliated with, endorsed by, or
sponsored by Kuro Games. Wuthering Waves and all associated assets, artwork, and
game titles are trademarks and copyrights of Kuro Games.*
