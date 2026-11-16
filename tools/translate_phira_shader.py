#!/usr/bin/env python3
"""Translate the pinned Phira-Pro fragment shader to draft HLSL for inspection.

Requires glslang 16.6.0 and SPIRV-Cross built from 57e4efcc84112abcd426dc5ec248b5339bee04f2.
The generated source is NOT a Unity shader or proof of rendering equivalence.
"""

import argparse
import hashlib
from pathlib import Path
import subprocess
import tempfile

SOURCE_SHA256 = "37f2a93fa9a93058aa8bf744286de92ffad5a103d50a517b6f6fd2551b803b4e"
HEADER = """// Draft translation; not yet validated in Unity or D3D11.
// Phira-Pro c8fb11c6a41faaf1233a5affabf87b0223e2edd8,
// prpr/src/core/block_shader_full.frag, GPL-3.0-or-later.
// Generated with tools/translate_phira_shader.py; do not edit by hand.
"""


def specialize(text, disabled, hover, colored):
    """Apply the production renderer's uniform specializations before translation."""
    text = text.replace("uniform int uLayer;", f"const int uLayer = {0 if disabled else 3};")
    text = text.replace("uniform int uColored;", f"const int uColored = {1 if colored else 0};")
    if not colored:
        for name in ("uDisabledSparkTint", "uDisabledFillColor", "_SparkTint", "_FillColor",
                     "_GlowColor", "_EdgeColor", "_NoiseTint", "_TouchGlowColor"):
            text = text.replace(f"blockPalette({name}.xyz, rgb)", f"{name}.xyz")
    if not hover:
        text = text.replace("uniform \tint _TouchPosCount;", "const int _TouchPosCount = 0;")
        start = text.rfind("float hoverSample(vec2 uv) {")
        if start < 0:
            raise ValueError("Reference hover function not found")
        end = text.index("}", start) + 1
        text = text[:start] + "float hoverSample(vec2 uv) { return 0.0; }" + text[end:]
    return text.replace("mediump", "highp") if colored else text


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("glslang", type=Path)
    parser.add_argument("spirv_cross", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--variants-dir", type=Path, help="also emit the six production material variants")
    args = parser.parse_args()
    raw = args.source.read_bytes()
    if hashlib.sha256(raw).hexdigest() != SOURCE_SHA256:
        raise SystemExit("Reference shader does not match the pinned source")
    text = raw.decode("utf-8").replace("\r\n", "\n")
    if not text.startswith("#version 100\n"):
        raise SystemExit("Unexpected GLSL version")
    def translate(source, output):
        source = source.replace("#version 100", "#version 310 es\nlayout(location=0) out highp vec4 pev_output;", 1)
        source = source.replace("varying ", "in ").replace("texture2D(", "texture(").replace("gl_FragColor", "pev_output")
        with tempfile.TemporaryDirectory(prefix="pev-shader-") as tmp:
            compile_one(source, output, Path(tmp))

    def compile_one(source, output, tmp):
        frag, spv, hlsl = tmp / "source.frag", tmp / "source.spv", tmp / "result.hlsl"
        frag.write_text(source, encoding="utf-8", newline="\n")
        subprocess.run([str(args.glslang.resolve()), "-G", "--auto-map-bindings", "--auto-map-locations",
                        "-S", "frag", "-o", str(spv), str(frag)], check=True)
        subprocess.run([str(args.spirv_cross.resolve()), str(spv), "--hlsl", "--shader-model", "50",
                        "--output", str(hlsl)], check=True)
        subprocess.run([str(args.glslang.resolve()), "-D", "-V", "-e", "main", "-S", "frag",
                        "--auto-map-bindings", "--auto-map-locations", "-o", str(tmp / "roundtrip.spv"), str(hlsl)], check=True)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(HEADER + hlsl.read_text(encoding="utf-8"), encoding="utf-8", newline="\n")

    translate(text, args.output)
    if args.variants_dir:
        for disabled, hover, colored in ((1, 0, 0), (0, 0, 0), (0, 1, 0),
                                          (1, 0, 1), (0, 0, 1), (0, 1, 1)):
            name = f"{disabled}{hover}{colored}.hlsl"
            translate(specialize(text, disabled, hover, colored), args.variants_dir / name)
    print(f"Draft HLSL written to {args.output}; runtime equivalence remains unverified.")


if __name__ == "__main__":
    main()
