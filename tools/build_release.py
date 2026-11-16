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
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile

from pack_event import validate

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
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._+-]*", args.version):
        ap.error("version must contain only letters, digits, dots, underscores, plus or hyphens")

    if not args.skip_build:
        print("building core...")
        run(["dotnet", "build", "-c", "Release", "--nologo"], cwd=CORE)

    dll = os.path.join(CORE, "bin", "Release", "PEV.Core.dll")
    if not os.path.isfile(dll):
        raise SystemExit(f"missing build output: {dll}")

    for name in sorted(os.listdir(EVENTS)):
        src = os.path.join(EVENTS, name)
        if not os.path.isdir(src) or not name.endswith(".event"):
            continue
        with open(os.path.join(src, "event.json"), encoding="utf-8") as fh:
            manifest = json.load(fh)
        problems = validate(src, manifest)
        if problems:
            raise SystemExit(f"invalid event {name}: " + "; ".join(problems))

    zip_path = os.path.join(out_dir, f"{MOD_ID}-{args.version}.zip")
    os.makedirs(out_dir, exist_ok=True)
    if os.path.exists(zip_path):
        raise SystemExit(f"refusing to overwrite existing release: {zip_path}")
    print("staging...")
    with tempfile.TemporaryDirectory(prefix="pev-release-") as staging_root:
        stage = os.path.join(staging_root, MOD_ID)
        os.makedirs(os.path.join(stage, "Events"))
        shutil.copy2(dll, stage)
        shutil.copy2(os.path.join(ROOT, "LICENSE"), stage)
        shutil.copy2(os.path.join(ROOT, "docs", "INSTALL-zh-CN.md"), stage)
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
            shutil.copytree(src, os.path.join(stage, "Events", name))
            print(f"  + Events/{name}")
        print("packing...")
        with zipfile.ZipFile(zip_path, "x", zipfile.ZIP_DEFLATED) as zf:
            for dirpath, _dirnames, filenames in os.walk(stage):
                for fn in filenames:
                    full = os.path.join(dirpath, fn)
                    rel = os.path.relpath(full, staging_root).replace(os.sep, "/")
                    zf.write(full, rel)

    size = os.path.getsize(zip_path)
    print(f"\n{zip_path}  ({size / 1024 / 1024:.1f} MB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
