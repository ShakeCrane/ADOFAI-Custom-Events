#!/usr/bin/env python3
"""Build the colour grading shader bundle in an isolated Unity project.

The bundle contains the grading shader only. It is packaged into ColorGrading.event and
loaded by Core at runtime, so neither authors nor players need Unity installed.

Usage:
    python tools/build_color_grading_bundle.py \
        --unity <Unity 6000.3.25f1 Editor.exe> \
        --project <scratch project directory> \
        --output <bundle output directory>
"""
import argparse
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
SHADER = ROOT / "src/UnityShaders/ColorGrading/ColorGrading.shader"
BUNDLE = "color-grading.bundle"


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--unity", required=True, type=Path, help="Unity 6000.3.25f1 Editor executable")
    ap.add_argument("--project", required=True, type=Path, help="new scratch project directory")
    ap.add_argument("--output", required=True, type=Path, help="bundle output directory")
    args = ap.parse_args()
    project, output = args.project.resolve(), args.output.resolve()
    # Never wipe an existing project: an author may have opened it to debug the shader.
    if project.exists():
        raise SystemExit(f"Scratch project already exists: {project}")
    if not SHADER.is_file():
        raise SystemExit(f"Missing shader: {SHADER}")

    shutil.copytree(ROOT / "tools/unity-color-grading-bundle", project)
    generated = project / "Assets" / "Generated"
    generated.mkdir(parents=True)
    shutil.copy2(SHADER, generated / "ColorGrading.shader")

    output.mkdir(parents=True, exist_ok=True)
    import os
    env = os.environ.copy()
    env["PEV_GRADING_BUNDLE_OUTPUT"] = str(output)
    log = project / "build.log"
    result = subprocess.run(
        [str(args.unity.resolve()), "-batchmode", "-quit", "-noUpm", "-projectPath", str(project),
         "-executeMethod", "BuildColorGradingBundle.Build", "-logFile", str(log)],
        env=env)
    print(f"Unity exit code: {result.returncode}; log: {log}")
    if result.returncode:
        raise SystemExit(result.returncode)
    bundle = output / BUNDLE
    if not bundle.is_file() or not bundle.stat().st_size:
        raise SystemExit("Unity returned success without a bundle")
    print(f"Shader bundle built: {bundle} ({bundle.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
