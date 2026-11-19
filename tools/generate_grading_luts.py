#!/usr/bin/env python3
"""Generate the colour grading package's bundled 3D LUT looks.

The LUTs are generated rather than downloaded so the package has no third-party licensing
obligation and the looks are reproducible from this file. Each look is a pure function of
the normalised input colour, sampled on a 17^3 grid, which is the smallest size that keeps
smooth gradients after trilinear interpolation.

Usage:
    python tools/generate_grading_luts.py [--output <directory>]
"""
import argparse
import math
from pathlib import Path

SIZE = 17


def clamp(value, low=0.0, high=1.0):
    return low if value < low else high if value > high else value


def luma(rgb):
    return 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2]


def smoothstep(edge0, edge1, x):
    if edge1 <= edge0:
        return 0.0
    t = clamp((x - edge0) / (edge1 - edge0))
    return t * t * (3.0 - 2.0 * t)


def saturate(rgb, amount):
    grey = luma(rgb)
    return [clamp(grey + (channel - grey) * amount) for channel in rgb]


def lift_gamma_gain(rgb, lift, gain):
    """Standard lift/gain: lift raises the black end, gain scales the white end."""
    return [clamp(channel * gain[i] + lift[i] * (1.0 - channel)) for i, channel in enumerate(rgb)]


def teal_orange(rgb):
    """Cool shadows, warm highlights — the look most charts reach for first."""
    l = luma(rgb)
    shadow = 1.0 - smoothstep(0.0, 0.55, l)
    highlight = smoothstep(0.45, 1.0, l)
    out = [
        rgb[0] + 0.085 * highlight - 0.055 * shadow,
        rgb[1] + 0.015 * highlight + 0.010 * shadow,
        rgb[2] - 0.075 * highlight + 0.095 * shadow,
    ]
    out = saturate(out, 1.12)
    out = [(channel - 0.5) * 1.06 + 0.5 for channel in out]
    return [clamp(channel) for channel in out]


def bleach_bypass(rgb):
    """High contrast with desaturated highlights, as if silver were left in the print."""
    l = luma(rgb)
    grey = l
    amount = 0.35 + 0.55 * smoothstep(0.35, 1.0, l)
    out = [clamp(channel + (grey - channel) * amount) for channel in rgb]
    out = [(channel - 0.5) * 1.28 + 0.5 + 0.02 for channel in out]
    out = lift_gamma_gain(out, [0.012, 0.010, 0.018], [1.02, 1.0, 0.99])
    return [clamp(channel) for channel in out]


def warm_film(rgb):
    """A gentle warm print with lifted blacks and slightly muted greens."""
    out = [
        rgb[0] * 1.055 + 0.018,
        rgb[1] * 1.010 + 0.012,
        rgb[2] * 0.950 + 0.020,
    ]
    out = lift_gamma_gain(out, [0.020, 0.016, 0.026], [1.0, 1.0, 1.0])
    out = saturate(out, 0.94)
    # A touch of extra contrast in the upper midtones keeps the look from going flat.
    out = [clamp(0.5 + (channel - 0.5) * (1.0 + 0.10 * smoothstep(0.25, 1.0, channel))) for channel in out]
    return out


LOOKS = {
    "teal-orange.cube": ("Teal and Orange", teal_orange),
    "bleach-bypass.cube": ("Bleach Bypass", bleach_bypass),
    "warm-film.cube": ("Warm Film", warm_film),
}


def write_cube(path, title, fn):
    lines = [f'TITLE "{title}"', f"LUT_3D_SIZE {SIZE}", "DOMAIN_MIN 0.0 0.0 0.0", "DOMAIN_MAX 1.0 1.0 1.0", ""]
    step = 1.0 / (SIZE - 1)
    for b in range(SIZE):
        for g in range(SIZE):
            for r in range(SIZE):
                out = fn((r * step, g * step, b * step))
                lines.append(f"{out[0]:.6f} {out[1]:.6f} {out[2]:.6f}")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return len(lines)


def main():
    root = Path(__file__).resolve().parents[1]
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--output", type=Path,
                    default=root / "src/Events/ColorGrading.event/assets/luts")
    args = ap.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    for name, (title, fn) in LOOKS.items():
        path = args.output / name
        count = write_cube(path, title, fn)
        print(f"{path}  entries={count - 5}  {path.stat().st_size} bytes")


if __name__ == "__main__":
    main()
