#!/usr/bin/env python3
"""Build and run the offline hardware D3D11 mask probe against pinned fixtures."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def main():
    from PIL import Image

    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--cxx", type=Path, help="MinGW g++ executable (defaults to PATH)")
    ap.add_argument("--benchmark", action="store_true", help="also measure one mask-only GPU layer")
    args = ap.parse_args()
    cxx = args.cxx or (Path(shutil.which("g++")) if shutil.which("g++") else None)
    if cxx is None or not cxx.is_file():
        raise SystemExit("MinGW g++ was not found; pass --cxx")
    cxx = cxx.resolve()
    env = os.environ.copy()
    env["PATH"] = str(cxx.parent) + os.pathsep + env.get("PATH", "")
    with tempfile.TemporaryDirectory(prefix="pev-d3d11-") as directory:
        temp = Path(directory)
        exe = temp / "probe.exe"
        subprocess.run([str(cxx), "-std=c++17", "-O2", "-static", str(ROOT / "tools/d3d11_noise_probe.cpp"),
                        "-o", str(exe), "-ld3d11", "-ld3dcompiler", "-luuid"], env=env, check=True)
        fixtures = ROOT / "tests/fixtures/noise-field-coverage"
        for name, size in (("BlockNoise1", (256, 256)), ("TouchHover", (44, 44))):
            with Image.open(fixtures / (name + ".png")) as image:
                image = image.convert("RGBA").transpose(Image.Transpose.FLIP_TOP_BOTTOM)
                if image.size != size:
                    raise ValueError(f"Unexpected {name} texture dimensions: {image.size}")
                (temp / (name + ".rgba")).write_bytes(image.tobytes())
        for case in ("normal", "faded"):
            run_env = dict(env)
            if args.benchmark and case == "normal":
                run_env["PEV_BENCH_NOISE_GPU"] = "1"
            else:
                run_env.pop("PEV_BENCH_NOISE_GPU", None)
            subprocess.run([str(exe), str(ROOT / "src/UnityShaders/Draft/NoiseFieldCoverage.compute"),
                            str(fixtures), str(ROOT / "tests/fixtures/noise-field-colors"),
                            str(temp / "BlockNoise1.rgba"), str(temp / "TouchHover.rgba"), case], env=run_env, check=True)


if __name__ == "__main__":
    main()
