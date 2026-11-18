#!/usr/bin/env python3
"""Compile the draft HLSL as SM5 using Windows D3DCompiler, without Unity.

This checks the D3D11 compiler frontend only. It does not import ShaderLab,
execute on the GPU, build a Unity bundle or validate visual equivalence.
"""
import ctypes
from ctypes import wintypes
from pathlib import Path
import sys

from generate_noise_unity_shaders import ROOT, TARGET, VERTEX, PROBE_TRANSFORM


def main():
    if sys.platform != "win32":
        raise SystemExit("Windows D3DCompiler is required")
    compiler = ctypes.WinDLL("d3dcompiler_47.dll")
    compile_shader = compiler.D3DCompile
    compile_shader.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_char_p, ctypes.c_void_p,
                               ctypes.c_void_p, ctypes.c_char_p, ctypes.c_char_p, wintypes.UINT,
                               wintypes.UINT, ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_void_p)]
    compile_shader.restype = ctypes.c_long

    def blob_content(blob):
        table = ctypes.cast(blob, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
        ptr = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)(table[3])(blob)
        size = ctypes.WINFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)(table[4])(blob)
        return ctypes.string_at(ptr, size)

    def release(blob):
        if blob:
            table = ctypes.cast(blob, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
            ctypes.WINFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)(table[2])(blob)

    warnings = []

    def check(source, name, entry, target):
        raw = source.encode("utf-8")
        buffer = ctypes.create_string_buffer(raw)
        output, errors = ctypes.c_void_p(), ctypes.c_void_p()
        result = compile_shader(buffer, len(raw), name.encode(), None, None, entry.encode(), target.encode(),
                                1 << 11, 0, ctypes.byref(output), ctypes.byref(errors))
        try:
            message = blob_content(errors).rstrip(b"\0").decode("utf-8", errors="replace") if errors else ""
            if result < 0 or not output:
                raise RuntimeError(f"D3DCompile failed for {name} ({target}/{entry}):\n{message}")
            if message.strip():
                warnings.append((name, entry, message.strip()))
        finally:
            release(output)
            release(errors)

    variants = sorted(TARGET.glob("*.hlsl"))
    if len(variants) != 10:
        raise RuntimeError(f"Expected 10 generated HLSL variants, got {len(variants)}")
    for path in variants:
        source = path.read_text(encoding="utf-8")
        combined = "#define UNITY_UV_STARTS_AT_TOP 1\n" + PROBE_TRANSFORM + source + VERTEX
        check(combined, path.name, "pev_vertex", "vs_5_0")
        check(combined, path.name, "pev_fragment", "ps_5_0")
    compute = (ROOT / "src/UnityShaders/Draft/NoiseFieldCoverage.compute").read_text(encoding="utf-8")
    # Unity consumes these pragmas before handing HLSL to D3DCompiler.
    compute = "\n".join(line for line in compute.splitlines() if not line.strip().startswith("#pragma kernel "))
    for entry in ("BuildSources", "ComposeSources", "WarpActive", "DilateRing", "ExportMasks", "BuildTouch", "WarpColors"):
        check(compute, "NoiseFieldCoverage.compute", entry, "cs_5_0")
    print(f"D3DCompiler SM5: {len(variants)} vertex/fragment pairs and 7 compute kernels compiled")
    for name, entry, message in warnings:
        print(f"{name}/{entry}: {message}")
    print("Unity shader import, D3D11 GPU execution and visual equivalence remain untested.")


if __name__ == "__main__":
    main()
