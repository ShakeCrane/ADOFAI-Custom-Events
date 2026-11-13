#!/usr/bin/env python3
"""Build the release zip.

Produces a drop-in Unity Mod Manager package:

    PEV.Core/
    ├── Info.json
    ├── PEV.Core.dll
    ├── Jint.dll
    ├── Acornima.dll
    └── Events/
        ├── CameraWarp.event
        ├── Pulse.event
        └── ScreenShake.event

Usage:
    python tools/build_release.py [--version 0.1.0] [--out dist]
"""

import argparse
import json
import os
import shutil
import subprocess
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CORE = os.path.join(ROOT, "src", "PEV.Core")
EVENTS = os.path.join(ROOT, "src", "Events")
PACKAGES = os.path.join(ROOT, "packages", "jint-lib")

MOD_ID = "PEV.Core"
ENTRY = "PEV.Core.Plugin.Load"


def run(cmd, cwd=None):
    print("  $ " + " ".join(cmd))
    result = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True,
                            encoding="utf-8", errors="replace")
    if result.returncode != 0:
        print(result.stdout)
        print(result.stderr, file=sys.stderr)
        raise SystemExit(f"command failed: {cmd[0]}")
    return result.stdout


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", default="0.1.0")
    ap.add_argument("--out", default="dist")
    ap.add_argument("--skip-build", action="store_true")
    args = ap.parse_args()

    out_dir = os.path.join(ROOT, args.out)
    stage = os.path.join(out_dir, MOD_ID)

    if not args.skip_build:
        print("building core...")
        run(["dotnet", "build", "-c", "Release", "--nologo"], cwd=CORE)

    dll = os.path.join(CORE, "bin", "Release", "PEV.Core.dll")
    if not os.path.isfile(dll):
        raise SystemExit(f"missing build output: {dll}")

    print("staging...")
    if os.path.isdir(stage):
        shutil.rmtree(stage)
    os.makedirs(stage)
    os.makedirs(os.path.join(stage, "Events"))

    shutil.copy2(dll, stage)
    for dep in ("Jint.dll", "Acornima.dll"):
        src = os.path.join(PACKAGES, dep)
        if not os.path.isfile(src):
            raise SystemExit(f"missing dependency: {src}")
        shutil.copy2(src, stage)

    with open(os.path.join(stage, "Info.json"), "w", encoding="utf-8") as fh:
        json.dump({
            "Id": MOD_ID,
            "DisplayName": "ADOFAI Programmable Events",
            "Author": "ShakeCrane",
            "Version": args.version,
            "AssemblyName": "PEV.Core.dll",
            "EntryMethod": ENTRY,
        }, fh, indent=2)
        fh.write("\n")

    for name in sorted(os.listdir(EVENTS)):
        src = os.path.join(EVENTS, name)
        if not os.path.isdir(src) or not name.endswith(".event"):
            continue
        dst = os.path.join(stage, "Events", name)
        shutil.copytree(src, dst)
        print(f"  + Events/{name}")

    zip_path = os.path.join(out_dir, f"{MOD_ID}-{args.version}.zip")
    if os.path.isfile(zip_path):
        os.remove(zip_path)

    print("packing...")
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for dirpath, _dirnames, filenames in os.walk(stage):
            for fn in filenames:
                full = os.path.join(dirpath, fn)
                rel = os.path.relpath(full, out_dir).replace(os.sep, "/")
                zf.write(full, rel)

    size = os.path.getsize(zip_path)
    print(f"\n{zip_path}  ({size / 1024 / 1024:.1f} MB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
