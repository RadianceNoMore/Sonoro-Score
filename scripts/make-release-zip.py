#!/usr/bin/env python3
"""Build the SonoroScore release zip.

Everything the app needs at runtime lives in publish/SonoroScore/ - including the
x64/ folder with tesseract50.dll + leptonica-1.82.0.dll (a previous build script
whitelisted files and silently dropped that folder, so the extracted app reported
"Tesseract unavailable"). This script takes the WHOLE publish folder minus known
non-shippable files, plus the bundled Interception driver package.

Usage:
    python scripts/make-release-zip.py [--version 1.0.0] [--publish <dir>]
"""
import argparse
import os
import sys
import zipfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Files never shipped: personal copies/state, and files the app regenerates.
EXCLUDE_FILES = {
    "SonoroScore_Copy.exe",   # manual backup copy, not ours to ship
    "SS.jpg",                 # legacy logo, superseded by SS_Quaver.jpg
    "window-state.json",      # per-user window size
    "regions.override.json",  # per-user region tuning (defaults are compiled in)
    "ss_layout.json",         # per-user UI layout
    "area_dialog_layout.json",
}
EXCLUDE_DIRS = {"sessions"}   # per-user capture sessions

README = """SonoroScore {version} - automatic echo scanner for Wuthering Waves

1. Install the Interception driver (REQUIRED - SS refuses to scan without it):
   - Open a Command Prompt as Administrator and run:
       "SonoroScore-{version}\\Interception\\command line installer\\install-interception.exe" /install
   - Then REBOOT once.
   Interception (oblitum/Interception) is LGPL-3.0 for non-commercial use - the full
   package with its license texts is included under Interception\\.
2. Run SonoroScore.exe. Windows asks for Administrator - SS needs it to send input
   to the game. Accept the prompt.
3. Open Wuthering Waves, then press START SCAN.
   - Overworld: SS navigates the menus itself.
   - Character screen: park the game on the echo grid; SS only pages + captures.
   While it scans, keep the game focused and don't touch mouse/keyboard.
4. When the OCR bar finishes, use "Export to Tacet Lab" and/or "Open Review Studio".

The Tesseract OCR engine and language data are bundled - no other installs needed.
Everything runs offline. Captures land in .\\sessions\\session_<timestamp>\\.
The window is resizable; its size/position are remembered in window-state.json.

Licensing: the scanner adapts code/data from Tacet-Lab (GPL-3.0) - see the project
NOTICES.md. The bundled Interception driver package is LGPL-3.0 for non-commercial
use (license texts included).
"""


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", default="1.0.0")
    ap.add_argument("--publish", default=os.path.join(REPO, "publish", "SonoroScore"))
    args = ap.parse_args()

    pub = args.publish
    interception = os.path.join(REPO, "assets", "interception", "package")
    if not os.path.isdir(pub):
        print("publish folder not found:", pub, file=sys.stderr)
        return 1
    if not os.path.isdir(interception):
        print("interception package not found:", interception, file=sys.stderr)
        return 1

    dist = os.path.join(REPO, "dist")
    os.makedirs(dist, exist_ok=True)
    zip_path = os.path.join(dist, f"SonoroScore-{args.version}-win64.zip")
    top = f"SonoroScore-{args.version}/"

    shipped, skipped = [], []
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        z.writestr(top + "README-FIRST.txt", README.replace("{version}", args.version))
        for root, dirs, files in os.walk(pub):
            dirs[:] = [d for d in dirs if d.lower() not in EXCLUDE_DIRS]
            for f in files:
                if f in EXCLUDE_FILES or f.lower().endswith(".pdb"):
                    skipped.append(os.path.relpath(os.path.join(root, f), pub))
                    continue
                fp = os.path.join(root, f)
                rel = os.path.relpath(fp, pub).replace("\\", "/")
                z.write(fp, top + rel)
                shipped.append(rel)
        for root, _, files in os.walk(interception):
            for f in files:
                fp = os.path.join(root, f)
                rel = os.path.relpath(fp, interception).replace("\\", "/")
                z.write(fp, top + "Interception/" + rel)
                shipped.append("Interception/" + rel)

    # sanity: the OCR engine must be inside, or the zip is broken by definition
    required = ["SonoroScore.exe", "tessdata/eng.traineddata",
                "x64/tesseract50.dll", "x64/leptonica-1.82.0.dll", "interception.dll",
                "echo_catalog.json", "sonata_signatures_local.json"]
    missing = [r for r in required if r not in shipped]
    if missing:
        print("REFUSING: required files missing from the zip:", missing, file=sys.stderr)
        return 2

    print("wrote", zip_path)
    print("  size:", os.path.getsize(zip_path), "bytes ; shipped:", len(shipped), "entries")
    print("  skipped (by design):", ", ".join(sorted(skipped)) or "none")
    print("  OCR stack: x64/tesseract50.dll + tessdata/eng.traineddata present")
    return 0


if __name__ == "__main__":
    sys.exit(main())
