#!/usr/bin/env python3
"""Assemble an explicitly experimental noise .event around an already-built bundle.

This is a staging tool, not a release or validation of the shader bundle. Never
installs or overwrites anything and never modifies either source package.
"""
import argparse
import json
from pathlib import Path
import shutil
import tempfile

from pack_event import pack, validate

ROOT = Path(__file__).resolve().parents[1]
LEGACY = ROOT / "src/Events/NoiseField.event"
DRAFT = ROOT / "src/UnityShaders/Draft/NoiseField.event"
STATE = ROOT / "src/UnityShaders/Draft/NoiseFieldState.event"


def stage(bundle, output):
    bundle, output = Path(bundle).resolve(), Path(output).resolve()
    if not bundle.is_file() or bundle.stat().st_size == 0:
        raise ValueError("A nonempty Unity shader bundle is required")
    if not output.parent.is_dir():
        raise ValueError("Output directory does not exist")
    manifest = json.loads((DRAFT / "event.json").read_text(encoding="utf-8"))
    script = (LEGACY / "main.js").read_text(encoding="utf-8")
    marker = "    fx.field(key, {\n"
    if script.count(marker) != 1:
        raise ValueError("Unexpected legacy noise driver; cannot safely add inversion")
    script = script.replace(marker, marker + "      invert: !!params.invert,\n", 1)
    with tempfile.TemporaryDirectory(prefix="pev-noise-draft-") as temp:
        temp = Path(temp)
        package = temp / "NoiseField.event"
        assets = package / "assets"
        assets.mkdir(parents=True)
        (package / "event.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        (package / "main.js").write_text(script, encoding="utf-8")
        shutil.copy2(LEGACY / "icon.png", package / "icon.png")
        for name in ("BlockNoise1.png", "PointNoise.png", "FD_Noise_00000.png", "TouchHover.png", "README.md"):
            shutil.copy2(LEGACY / "assets" / name, assets / name)
        shutil.copytree(LEGACY / "assets" / "legacy", assets / "legacy")
        shutil.copy2(bundle, assets / "noise-field.bundle")
        problems = validate(package, manifest)
        if problems:
            raise ValueError("Invalid staged draft: " + "; ".join(problems))
        archive = temp / "staged.event"
        pack(package, archive)
        with output.open("xb") as dst, archive.open("rb") as src:
            shutil.copyfileobj(src, dst)
    return output


def stage_state(output):
    output = Path(output).resolve()
    if not output.parent.is_dir():
        raise ValueError("Output directory does not exist")
    manifest = json.loads((STATE / "event.json").read_text(encoding="utf-8"))
    problems = validate(STATE, manifest)
    if problems:
        raise ValueError("Invalid staged state event: " + "; ".join(problems))
    with tempfile.TemporaryDirectory(prefix="pev-noise-state-") as temp:
        archive = Path(temp) / "state.event"
        pack(STATE, archive)
        with output.open("xb") as dst, archive.open("rb") as src:
            shutil.copyfileobj(src, dst)
    return output


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--bundle", required=True, type=Path)
    ap.add_argument("--output", required=True, type=Path)
    ap.add_argument("--state-output", type=Path, help="also stage the separate state event")
    args = ap.parse_args()
    if args.state_output and (args.output.resolve() == args.state_output.resolve() or args.state_output.exists()):
        ap.error("state output must differ from the main output and must not already exist")
    print("Experimental draft staged:", stage(args.bundle, args.output))
    if args.state_output:
        print("Experimental state event staged:", stage_state(args.state_output))
    print("Not release-ready: Unity runtime loading, depth, capture and visual equivalence remain unverified.")


if __name__ == "__main__":
    main()
