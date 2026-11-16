#!/usr/bin/env python3
"""Prepare a limited, reproducible Phira-Pro GPU reference driver.

This writes a new example in an isolated checkout; production renderer sources
are never edited. The missing official-shader comparison and chart fixture are
explicitly skipped, so a successful run is not an upstream regression pass.
"""

import argparse
from pathlib import Path
import subprocess


COMMIT = "c8fb11c6a41faaf1233a5affabf87b0223e2edd8"
SOURCE = "prpr/examples/block_area_gpu.rs"


def replace_once(text, old, new):
    if text.count(old) != 1:
        raise RuntimeError(f"Expected exactly one occurrence: {old!r}")
    return text.replace(old, new, 1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("checkout", type=Path, help="isolated Phira-Pro checkout")
    args = parser.parse_args()
    root = args.checkout.resolve()
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    if head != COMMIT:
        raise SystemExit(f"Expected {COMMIT}, got {head}")
    if subprocess.run(["git", "diff", "--quiet", "HEAD", "--", "prpr/src/core"], cwd=root).returncode:
        raise SystemExit("Production core differs from the pinned commit")
    text = subprocess.check_output(["git", "show", f"{COMMIT}:{SOURCE}"], cwd=root, text=True)
    text = replace_once(text, "Some(block_shader::Zone {\n                    line2area:",
                        "Some(block_shader::Zone {\n                    projection: None,\n                    line2area:")
    text = replace_once(text, "    block_shader::Zone {\n        line2area:",
                        "    block_shader::Zone {\n        projection: None,\n        line2area:")
    signature = "fn native_reference_scene(zones: &[Zone], port: &Image, time: f32, touches: &[(u64, Vec2)], input: Option<&Image>) {\n"
    text = replace_once(text, signature, signature +
                        '    if std::env::var_os("PEV_SKIP_MISSING_OFFICIAL_REFERENCE").is_some() { return; }\n')
    chart = "    // Parse the real native JSON and use the existing, unchanged transforms.\n"
    text = replace_once(text, chart,
                        '    if std::env::var_os("PEV_SKIP_MISSING_CHART").is_some() { println!("PEV: skipped missing chart; native shader comparison also skipped"); return; }\n' + chart)
    gray = "Color::new(0.1, 0.1, 0.1, 1.)"
    if text.count(gray) != 7:
        raise RuntimeError("Unexpected number of gray fixtures")
    text = text.replace(gray, "Color::new(25. / 255., 25. / 255., 25. / 255., 1.)")
    output = root / "prpr/examples/pev_block_area_gpu.rs"
    with output.open("x", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print(f"Created {output}")
    print("Set PEV_SKIP_MISSING_OFFICIAL_REFERENCE=1 and PEV_SKIP_MISSING_CHART=1")
    print("Run: cargo run --locked -p prpr --example pev_block_area_gpu")
    print("The official-shader comparison and chart fixture are skipped; renderer sources are unchanged.")


if __name__ == "__main__":
    main()
