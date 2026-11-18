#!/usr/bin/env python3
"""Compare draft HLSL compute masks against pinned Phira-Pro fixtures on OpenGL.

Requires glfw, moderngl, numpy, glslang and SPIRV-Cross. This does not test Unity
or D3D11; it translates the compute kernels to GLSL for a reproducible GPU check.
"""
import argparse
from pathlib import Path
import struct
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
FIXTURES = ROOT / "tests/fixtures/noise-field-coverage"
SOURCE = ROOT / "src/UnityShaders/Runtime/NoiseFieldCoverage.compute"
AREAS = [
    (-.2, .05, .45, .27, .23, 1., 1., 1),
    (.3, -.1, .35, .18, 0., 1., 1., 3),
    (.05, .1, .28, .22, 0., 1., .35, 0),
    (-.3, -.12, .24, .20, 0., 1., .65, 2),
    (.05, -.05, .21, .13, 0., 1., 1., 4),
    (.15, .08, .17, .14, 0., 1., .4, 6),
]
FADED_AREAS = [
    (0., 0., .45, .25, -.31, 1., .37, 1),
    (.15, .05, .32, .17, .15, 1., .67, 1),
    (.1, 0., .22, .18, 0., 1., 1., 3),
    (.1, 0., .22, .18, 0., 1., 1., 3),
    (.1, 0., .22, .18, 0., 1., 1., 3),
    (-.1, .08, .27, .16, 0., 1., .52, 4),
]


def main():
    import glfw
    import moderngl
    import numpy as np
    from PIL import Image

    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--glslang", type=Path, required=True)
    ap.add_argument("--spirv-cross", type=Path, required=True)
    ap.add_argument("--case", choices=("normal", "faded"), default="normal")
    args = ap.parse_args()
    areas = AREAS if args.case == "normal" else FADED_AREAS
    with tempfile.TemporaryDirectory(prefix="pev-coverage-") as tmp:
        tmp = Path(tmp)
        glsl = []
        for entry in ("BuildSources", "ComposeSources", "WarpActive", "DilateRing", "ExportMasks", "BuildTouch", "WarpColors"):
            binary, source = tmp / (entry + ".spv"), tmp / (entry + ".comp")
            subprocess.run([str(args.glslang.resolve()), "-D", "-V", "-S", "comp", "-e", entry,
                            "--auto-map-bindings", "-o", str(binary), str(SOURCE)], check=True)
            subprocess.run([str(args.spirv_cross.resolve()), str(binary), "--version", "430",
                            "--output", str(source)], check=True)
            glsl.append(source.read_text(encoding="utf-8"))
    if not glfw.init():
        raise RuntimeError("GLFW initialization failed")
    window = None
    try:
        glfw.window_hint(glfw.VISIBLE, glfw.FALSE)
        glfw.window_hint(glfw.CONTEXT_VERSION_MAJOR, 4)
        glfw.window_hint(glfw.CONTEXT_VERSION_MINOR, 3)
        window = glfw.create_window(64, 64, "PEV coverage check", None, None)
        if not window:
            raise RuntimeError("OpenGL 4.3 context creation failed")
        glfw.make_context_current(window)
        ctx = moderngl.create_context(require=430)
        print("GPU:", ctx.info["GL_VERSION"])
        build, compose, warp, ring, export, touch, warp_colors = (ctx.compute_shader(s) for s in glsl)
        data = ctx.buffer(b"".join(struct.pack("<7fI", *a) for a in areas))
        data.bind_to_storage_buffer(0)
        noise_image = np.asarray(Image.open(FIXTURES / "BlockNoise1.png").convert("RGBA"), dtype=np.uint8)
        noise_image = np.ascontiguousarray(np.flipud(noise_image))
        noise_height, noise_width = noise_image.shape[:2]
        noise = ctx.texture((noise_width, noise_height), 4, noise_image.tobytes(), dtype="f1")
        noise.use(0)
        warp["SPIRV_Cross_Combined_NoiseTextureSPIRV_Cross_DummySampler"].value = 0
        warp_colors["SPIRV_Cross_Combined_NoiseTextureSPIRV_Cross_DummySampler"].value = 0
        warp_colors["SPIRV_Cross_Combined_RawActiveColorsSPIRV_Cross_DummySampler"].value = 2
        touch_image = np.asarray(Image.open(FIXTURES / "TouchHover.png").convert("RGBA"), dtype=np.uint8)
        touch_image = np.ascontiguousarray(np.flipud(touch_image))
        touch_texture = ctx.texture((touch_image.shape[1], touch_image.shape[0]), 4, touch_image.tobytes(), dtype="f1")
        touch_texture.filter = (moderngl.LINEAR, moderngl.LINEAR)
        touch_texture.repeat_x = touch_texture.repeat_y = False
        touch_texture.use(1)
        touch["SPIRV_Cross_Combined_TouchTextureSPIRV_Cross_DummySampler"].value = 1
        for width, height in ((256, 144), (255, 143)):
            mw, mh = max(1, width // 8) * 2, max(1, height // 8) * 2
            param_bytes = bytearray(64)
            struct.pack_into("<I", param_bytes, 0, len(areas))
            struct.pack_into("<2I", param_bytes, 8, mw, mh)
            struct.pack_into("<f", param_bytes, 16, 16 / 9)
            struct.pack_into("<2I", param_bytes, 24, noise_width, noise_height)
            struct.pack_into("<2I", param_bytes, 56, touch_image.shape[1], touch_image.shape[0])
            params = ctx.buffer(bytes(param_bytes))
            src, aux, out, aux_out = (ctx.texture((mw, mh), 4, dtype="u4") for _ in range(4))
            params.bind_to_uniform_block(1)
            src.bind_to_image(2, read=False, write=True)
            aux.bind_to_image(3, read=False, write=True)
            build.run((mw + 7) // 8, (mh + 7) // 8)
            ctx.memory_barrier()
            params.bind_to_uniform_block(0)
            for unit, texture, read in ((1, src, True), (2, aux, True), (3, out, False), (4, aux_out, False)):
                texture.bind_to_image(unit, read=read, write=not read)
            compose.run((mw + 7) // 8, (mh + 7) // 8)
            ctx.memory_barrier()
            actual = [np.frombuffer(t.read(), dtype=np.uint32).reshape(mh, mw, 4).astype(np.uint8)
                      for t in (src, aux, out, aux_out)]
            prefix = FIXTURES / f"{'' if args.case == 'normal' else 'faded-'}{width}x{height}"

            def read(suffix, channels):
                return np.frombuffer(Path(str(prefix) + "-" + suffix + ".bin").read_bytes(),
                                     dtype=np.uint8).reshape(mh, mw, channels)

            expected = [
                read("sources", 4),
                np.concatenate([read("ready-normal", 1), read("ready-inverse", 1),
                                read("disabled-green", 1), read("ready-green", 1)], axis=2),
                np.concatenate([read("active-compose", 1), np.zeros((mh, mw, 2), dtype=np.uint8),
                                read("base", 4)[:, :, 3:4]], axis=2),
                read("aux-compose", 4),
            ]
            for name, got, want in zip(("sources", "aux-sources", "compose", "aux-compose"), actual, expected):
                diff = np.abs(got.astype(np.int16) - want.astype(np.int16))
                count = int(np.count_nonzero(diff))
                print(f"{width}x{height} {name}: {count} differing bytes; max={diff.max()}")
                if count:
                    raise AssertionError(f"{width}x{height} {name} differs from reference")
            release_scale = np.float32(1) - (np.float32(.3) - np.float32(.25)) / np.float32(.1)
            touch_cases = (("half", [(0.5, 0.5, 0.5)]),
                           ("two", [(0.5, 0.5, 1.), (0.55, 0.5, 1.)]),
                           ("release", [(0.5, 0.5, release_scale), (0.55, 0.5, 1.)]))
            for label, contacts in touch_cases:
                touch_data = ctx.buffer(b"".join(struct.pack("<4f", x, y, scale, 0.) for x, y, scale in contacts))
                touch_data.bind_to_storage_buffer(2)
                struct.pack_into("<If", param_bytes, 44, len(contacts), width / height)
                params.write(bytes(param_bytes))
                params.bind_to_uniform_block(0)
                aux_out.bind_to_image(1, read=True, write=True)
                touch.run((mw + 7) // 8, (mh + 7) // 8)
                ctx.memory_barrier()
                got = np.frombuffer(aux_out.read(), dtype=np.uint32).reshape(mh, mw, 4)[:, :, 3].astype(np.uint8)
                want = read("touch-" + label, 1)[:, :, 0]
                diff = np.abs(got.astype(np.int16) - want.astype(np.int16))
                count = int(np.count_nonzero(diff))
                print(f"{width}x{height} touch {label}: {count} differing bytes; max={diff.max()}")
                if count:
                    for y, x in np.argwhere(diff)[:8]:
                        print(f"  ({x},{y}): actual={got[y,x]} expected={want[y,x]}")
                    raise AssertionError(f"{width}x{height} touch {label} differs from reference")
                touch_data.release()
            # Effect tests below compare auxiliary alpha with the no-touch fixture.
            aux_out.write(np.concatenate([expected[3][:, :, :3], np.zeros((mh, mw, 1), dtype=np.uint8)], axis=2)
                          .astype(np.uint32).tobytes())
            effect = ctx.texture((mw, mh), 4, dtype="u4")
            palette_input = palette_output = None
            if args.case == "normal":
                color_prefix = ROOT / "tests/fixtures/noise-field-colors" / f"{width}x{height}"
                palette_input = ctx.texture((mw, mh), 4, Path(str(color_prefix) + "-raw-active.bin").read_bytes(), dtype="f1")
                palette_output = ctx.texture((mw, mh), 4, dtype="f4")
                palette_input.use(2)
            effect_normalized, aux_normalized = (ctx.texture((mw, mh), 4, dtype="f4") for _ in range(2))
            ping, pong = (ctx.texture((mw, mh), 1, dtype="u4") for _ in range(2))
            weights = np.array([np.float32(k) ** np.float32(2.65) for k in range(1, 7)], dtype=np.float32)
            weights = weights[::-1] / np.sum(weights, dtype=np.float32)
            for label, time in (("0", 0.), ("1", 1.), ("9", 9.)):
                struct.pack_into("<f", param_bytes, 32, time)
                params.write(bytes(param_bytes))
                params.bind_to_uniform_block(0)
                if palette_input is not None:
                    palette_output.bind_to_image(3, read=False, write=True)
                    warp_colors.run((mw + 7) // 8, (mh + 7) // 8)
                    ctx.memory_barrier()
                    values = np.frombuffer(palette_output.read(), dtype=np.float32).reshape(mh, mw, 4)
                    got = np.rint(values * 255).astype(np.uint8)
                    want = np.frombuffer(Path(str(color_prefix) + f"-active-{label}.bin").read_bytes(),
                                         dtype=np.uint8).reshape(mh, mw, 4)
                    count = int(np.count_nonzero(got != want))
                    print(f"{width}x{height} color warp t={label}: {count} differing bytes")
                    if count:
                        raise AssertionError(f"{width}x{height} color warp t={label} differs from reference")
                out.bind_to_image(1, read=True, write=False)
                effect.bind_to_image(3, read=False, write=True)
                ping.bind_to_image(4, read=False, write=True)
                warp.run((mw + 7) // 8, (mh + 7) // 8)
                ctx.memory_barrier()
                for pass_index, weight in enumerate(weights):
                    if weight < 0.01:
                        break
                    struct.pack_into("<fI", param_bytes, 36, float(weight), pass_index)
                    params.write(bytes(param_bytes))
                    effect.bind_to_image(1, read=True, write=True)
                    ping.bind_to_image(2, read=True, write=False)
                    pong.bind_to_image(3, read=False, write=True)
                    ring.run((mw + 7) // 8, (mh + 7) // 8)
                    ctx.memory_barrier()
                    ping, pong = pong, ping
                got = np.frombuffer(effect.read(), dtype=np.uint32).reshape(mh, mw, 4).astype(np.uint8)
                want = read("effect-" + label, 4)
                diff = np.abs(got.astype(np.int16) - want.astype(np.int16))
                count = int(np.count_nonzero(diff))
                print(f"{width}x{height} effect t={label}: {count} differing bytes; max={diff.max()}")
                if count:
                    for y, x, channel in np.argwhere(diff)[:8]:
                        print(f"  ({x},{y}) channel={channel}: actual={got[y,x,channel]} expected={want[y,x,channel]}")
                    raise AssertionError(f"{width}x{height} effect t={label} differs from reference")
                aux_out.bind_to_image(1, read=True, write=False)
                effect.bind_to_image(2, read=True, write=False)
                effect_normalized.bind_to_image(3, read=False, write=True)
                aux_normalized.bind_to_image(4, read=False, write=True)
                export.run((mw + 7) // 8, (mh + 7) // 8)
                ctx.memory_barrier()
                for name, texture, expected_bytes in (("effect", effect_normalized, want),
                                                      ("aux", aux_normalized, expected[3])):
                    values = np.frombuffer(texture.read(), dtype=np.float32).reshape(mh, mw, 4)
                    converted = np.rint(values * 255).astype(np.uint8)
                    if not np.array_equal(converted, expected_bytes):
                        raise AssertionError(f"{width}x{height} normalized {name} t={label} differs")
            for obj in (effect, ping, pong, effect_normalized, aux_normalized):
                obj.release()
            if palette_input is not None:
                palette_input.release()
                palette_output.release()
            for obj in (src, aux, out, aux_out, params):
                obj.release()
        data.release()
        noise.release()
        touch_texture.release()
    finally:
        if window:
            glfw.destroy_window(window)
        glfw.terminate()


if __name__ == "__main__":
    main()
