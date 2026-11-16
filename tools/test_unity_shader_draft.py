#!/usr/bin/env python3
"""Offline checks for generated Unity shader drafts; this is not a Unity build."""
import argparse
from pathlib import Path
import re
import subprocess
import tempfile

from generate_noise_unity_shaders import ROOT, SOURCE, TARGET, VERTEX, variant


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--dxc", type=Path, required=True)
    args = ap.parse_args()
    dxc = str(args.dxc.resolve())
    with tempfile.TemporaryDirectory(prefix="pev-dxc-") as tmp:
        tmp = Path(tmp)
        count = 0
        for colored in (0, 1):
            for disabled, hover, mode in ((1, 0, "disabled"), (0, 0, "capture"), (0, 1, "capture"),
                                          (0, 0, "nocapture"), (0, 1, "nocapture")):
                key = f"{disabled}{hover}{colored}"
                name = f"PEVNoise_{key}_{mode}"
                hlsl = TARGET / (name + ".hlsl")
                shader = (TARGET / (name + ".shader")).read_text(encoding="utf-8")
                expected = variant((SOURCE / (key + ".hlsl")).read_text(encoding="utf-8"),
                                   mode == "capture", bool(disabled))
                if hlsl.read_text(encoding="utf-8") != expected:
                    raise AssertionError(f"{hlsl} is out of date")
                if ("GrabPass" in shader) != (mode == "capture"):
                    raise AssertionError(f"Incorrect capture path: {name}")
                if mode == "nocapture" and ("uScene.Sample" in expected or "_GrabTexture" in expected):
                    raise AssertionError(f"Scene texture still sampled in {name}")
                for texture in re.findall(r"Texture2D<float4>\s+(\w+)\s*;", expected):
                    if not re.search(r"SamplerState\s+sampler" + re.escape(texture) + r"\s*;", expected):
                        raise AssertionError(f"Sampler does not follow Unity naming convention: {name}/{texture}")
                subprocess.run([dxc, "-T", "ps_6_0", "-E", "pev_fragment", "-Fo", str(tmp / (name + ".dxil")),
                                str(hlsl)], check=True)
                combined = tmp / (name + "-combined.hlsl")
                combined.write_text(expected + "\nfloat4 UnityObjectToClipPos(float4 v) { return v; }\n" + VERTEX,
                                    encoding="utf-8")
                for profile, entry in (("vs_6_0", "pev_vertex"), ("ps_6_0", "pev_fragment")):
                    subprocess.run([dxc, "-T", profile, "-E", entry, "-Fo", str(tmp / (name + profile + ".dxil")),
                                    str(combined)], check=True)
                count += 1
        vertex = tmp / "vertex.hlsl"
        vertex.write_text("float4 UnityObjectToClipPos(float4 v) { return v; }\n" + VERTEX, encoding="utf-8")
        subprocess.run([dxc, "-T", "vs_6_0", "-E", "pev_vertex", "-Fo", str(tmp / "vertex.dxil"),
                        str(vertex)], check=True)
        for entry in ("BuildSources", "ComposeSources", "WarpActive", "DilateRing", "ExportMasks", "BuildTouch", "WarpColors"):
            subprocess.run([dxc, "-T", "cs_6_0", "-E", entry, "-Fo", str(tmp / (entry + ".dxil")),
                            str(ROOT / "src/UnityShaders/Draft/NoiseFieldCoverage.compute")], check=True)
        print(f"DXC: {count} draft pixel shaders, one vertex shader, seven compute kernels compiled")
        print("No Unity shader packaging, D3D11 rendering, or visual equivalence was tested.")


if __name__ == "__main__":
    main()
