#!/usr/bin/env python3
"""Quantify the draft fragment on D3D11 against reference OpenGL frame fixtures.

The probe has a simple full-screen vertex transform and constant gray scene, not
Unity's camera, GrabPass, render targets or color pipeline. A low difference is
not final visual acceptance; changed pixel counts are printed for investigation.
"""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

from generate_noise_unity_shaders import ROOT, TARGET, VERTEX, PROBE_TRANSFORM


def main():
    import numpy as np
    from PIL import Image

    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--cxx", type=Path, help="MinGW g++ executable (defaults to PATH)")
    ap.add_argument("--output-dir", type=Path, help="existing directory for diagnostic PNGs")
    args = ap.parse_args()
    cxx = args.cxx or (Path(shutil.which("g++")) if shutil.which("g++") else None)
    if cxx is None or not cxx.is_file():
        raise SystemExit("MinGW g++ was not found; pass --cxx")
    if args.output_dir and not args.output_dir.is_dir():
        raise SystemExit("Output directory must already exist")
    cxx = cxx.resolve()
    env = os.environ.copy()
    env["PATH"] = str(cxx.parent) + os.pathsep + env.get("PATH", "")
    env.pop("PEV_FRAME_DEBUG", None)
    env.pop("PEV_FRAME_SIGNATURE", None)
    env.pop("PEV_FRAME_SIGNATURE_ONLY", None)
    env.pop("PEV_FRAME_CASE", None)
    with tempfile.TemporaryDirectory(prefix="pev-d3d11-frame-") as directory:
        temp = Path(directory)
        exe = temp / "probe.exe"
        subprocess.run([str(cxx), "-std=c++17", "-O2", "-static", str(ROOT / "tools/d3d11_frame_probe.cpp"),
                        "-o", str(exe), "-ld3d11", "-ld3dcompiler", "-luuid"], env=env, check=True)
        fixtures = ROOT / "tests/fixtures/noise-field-frame"
        assets = ROOT / "src/Events/NoiseField.event/assets"
        for name, size in (("BlockNoise1", (256, 256)), ("PointNoise", (128, 128)), ("FD_Noise_00000", (256, 256))):
            with Image.open(assets / (name + ".png")) as image:
                image = image.convert("RGBA").transpose(Image.Transpose.FLIP_TOP_BOTTOM)
                if image.size != size:
                    raise ValueError(f"Unexpected {name} texture dimensions: {image.size}")
                (temp / (name + ".rgba")).write_bytes(image.tobytes())
        with Image.open(fixtures / "256x144-palette-source.png") as image:
            image = image.convert("RGBA")
            if image.size != (256, 144):
                raise ValueError("Unexpected palette underlay dimensions")
            (temp / "palette-source.rgba").write_bytes(image.tobytes())
        variants = sorted(TARGET.glob("*.hlsl"))
        if len(variants) != 10:
            raise ValueError(f"Expected ten generated shader variants, got {len(variants)}")
        for variant in variants:
            source = variant.read_text(encoding="utf-8")
            combined = temp / (variant.stem + ".hlsl")
            combined.write_text("#define UNITY_UV_STARTS_AT_TOP 1\n" + PROBE_TRANSFORM + source + VERTEX,
                                encoding="utf-8")
            test_env = dict(env, PEV_FRAME_SIGNATURE_ONLY="1")
            subprocess.run([str(exe), str(combined), str(temp), str(fixtures), str(temp)],
                           env=test_env, check=True, stdout=subprocess.DEVNULL)
        print("All 10 D3D11 vertex/fragment signatures match", flush=True)
        for case in ("normal", "shifted", "disabled", "ready", "ready-full", "green", "hover", "palette", "nocapture"):
            variant = ("PEVNoise_100_disabled" if case == "disabled" else "PEVNoise_001_capture" if case == "green"
                       else "PEVNoise_010_capture" if case == "hover" else "PEVNoise_000_nocapture" if case == "nocapture"
                       else "PEVNoise_000_capture")
            combined = temp / (variant + ".hlsl")
            subprocess.run([str(exe), str(combined), str(temp), str(fixtures), str(temp)],
                           env=dict(env, PEV_FRAME_CASE=case), check=True)
            for time in (0, 1, 9):
                actual = np.frombuffer((temp / f"frame-{time}.rgba").read_bytes(), dtype=np.uint8).reshape(144, 256, 4)
                prefix = "" if case == "normal" else "palette-" if case == "nocapture" else case + "-"
                with Image.open(fixtures / f"256x144-{prefix}frame-{time}.png") as image:
                    expected = np.asarray(image.convert("RGBA"))
                diff = np.abs(actual[:, :, :3].astype(np.int16) - expected[:, :, :3].astype(np.int16))
                changed = int(np.count_nonzero(np.any(diff, axis=2)))
                mean, maximum = float(diff.mean()), int(diff.max())
                print(f"{case} t={time}: {changed}/36864 RGB pixels differ; mean channel error={mean:.6f}/255; max={maximum}/255")
                if args.output_dir:
                    Image.fromarray(actual).save(args.output_dir / f"d3d11-{case}-frame-{time}.png")
                if not np.any(actual[:, :, :3] != 25):
                    raise AssertionError("D3D11 frame remained entirely background")
                # Coarse cross-API regression guard, not a visual acceptance tolerance.
                # The pinned AMD driver measured at most 108 changed pixels and 0.015
                # mean error; other devices may require investigation if this trips.
                if case == "nocapture":
                    underlay = np.frombuffer((temp / "palette-source.rgba").read_bytes(), dtype=np.uint8).reshape(144, 256, 4)
                    delta = np.any(actual[:, :, :3] != underlay[:, :, :3], axis=2)
                    # Glow extends well past the nominal rectangle; check only the
                    # known-clear outer border in this fixed reference scene.
                    if not np.any(delta) or np.any(delta[:4]) or np.any(delta[-4:]) or np.any(delta[:, :16]) or np.any(delta[:, -16:]):
                        raise AssertionError("No-capture overlay failed to preserve the surrounding scene")
                elif changed > 512 or mean > 0.1:
                    raise AssertionError(f"Large cross-API frame deviation for {case} at t={time}")
                if case == "disabled":
                    shutil.copy2(temp / f"frame-{time}.rgba", temp / f"disabled-frame-{time}.rgba")
        print("Diagnostic only: cross-API differences, Unity import, camera/depth and final visuals remain to be verified.")


if __name__ == "__main__":
    main()
