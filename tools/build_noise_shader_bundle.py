#!/usr/bin/env python3
"""Build the noise field's Unity shader bundle in an isolated project."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--unity", required=True, type=Path, help="Unity 6000.3.25f1 Editor executable")
    ap.add_argument("--project", required=True, type=Path, help="new scratch project directory")
    ap.add_argument("--output", required=True, type=Path, help="bundle output directory")
    args = ap.parse_args()
    project, output = args.project.resolve(), args.output.resolve()
    # Never clear an existing project or stale generated assets on the user's behalf.
    if project.exists():
        raise SystemExit(f"Scratch project already exists: {project}")
    shutil.copytree(ROOT / "tools/unity-noise-bundle", project)
    assets = project / "Assets"
    shutil.copytree(ROOT / "src/UnityShaders/Runtime/Generated", assets / "Generated")
    shutil.copy2(ROOT / "src/UnityShaders/Runtime/NoiseFieldCoverage.compute", assets)
    output.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    env["PEV_NOISE_BUNDLE_OUTPUT"] = str(output)
    log = project / "build.log"
    # This isolated shader project has no package dependencies. Avoid a global UPM
    # installation or network failure preventing an otherwise self-contained build.
    result = subprocess.run([str(args.unity.resolve()), "-batchmode", "-quit", "-noUpm", "-projectPath", str(project),
                             "-executeMethod", "BuildNoiseBundle.Build", "-logFile", str(log)], env=env)
    print(f"Unity exit code: {result.returncode}; log: {log}")
    if result.returncode:
        raise SystemExit(result.returncode)
    bundle = output / "noise-field.bundle"
    if not bundle.is_file() or not bundle.stat().st_size:
        raise SystemExit("Unity returned success without a bundle")
    print(f"Shader bundle built: {bundle}")


if __name__ == "__main__":
    main()
