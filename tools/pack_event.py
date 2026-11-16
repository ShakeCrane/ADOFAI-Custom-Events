#!/usr/bin/env python3
"""Pack an event directory into a .event archive (a plain ZIP).

Usage:
    python pack_event.py <event-directory> [-o output.event]
    python pack_event.py --check <event-directory>

The output is a standard ZIP so it can be inspected with any archive tool.
`--check` validates the manifest without writing anything.
"""

import argparse
import json
import math
import os
import re
import sys
import zipfile
from pathlib import PurePosixPath

REQUIRED_FIELDS = ("schemaVersion", "id", "version", "entry")
VALID_TYPES = {
    "Array", "Bool", "Choice", "Color", "Export", "File", "FilterProperties", "Float",
    "FloatPair", "Int", "List", "MinMaxGradient", "Note", "ParticlePlayback",
    "Rating", "String", "Text", "Tile", "Vector2",
}
VALID_CATEGORIES = {
    "Gameplay", "TrackFx", "DecorationFx", "VisualFx",
    "FxModifiers", "Conveniences", "Jank", "Favorites",
}


def load_manifest(root):
    path = os.path.join(root, "event.json")
    if not os.path.isfile(path):
        return None, f"missing event.json in {root}"
    try:
        with open(path, encoding="utf-8") as fh:
            return json.load(fh), None
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        return None, f"event.json is not valid JSON: {exc}"


def validate(root, manifest):
    """Returns a list of problems. Empty means the package will load."""
    problems = []

    def valid_uniform_number(value):
        if type(value) not in (int, float):
            return False
        try:
            return math.isfinite(value) and abs(value) <= 3.4028234663852886e38
        except OverflowError:
            return False

    if not isinstance(manifest, dict):
        return ["manifest must be an object"]

    for field in REQUIRED_FIELDS:
        if field not in manifest:
            problems.append(f"missing required field '{field}'")

    for field in ("schemaVersion", "apiVersion"):
        value = manifest.get(field, 1)
        supported = (1, 2, 3) if field == "schemaVersion" else (1, 2)
        if type(value) is not int or value not in supported:
            problems.append(f"unsupported {field}: {value!r} (supported: {supported})")

    for field in ("id", "version", "entry"):
        value = manifest.get(field)
        if not isinstance(value, str) or not value.strip():
            problems.append(f"'{field}' must be a non-empty string")

    def check_resource(field):
        value = manifest.get(field)
        if value is None and field == "icon":
            return
        if not isinstance(value, str) or not value.strip():
            return
        normalized = value.replace("\\", "/")
        if normalized.startswith("/") or ":" in normalized or ".." in PurePosixPath(normalized).parts:
            problems.append(f"{field} must be a package-relative path")
        elif not os.path.isfile(os.path.join(root, normalized)):
            problems.append(f"{field} '{value}' not found")

    check_resource("entry")
    check_resource("icon")
    primitive = manifest.get("hostPrimitive")
    if "hostPrimitive" in manifest and (manifest.get("schemaVersion") != 3 or
            primitive != "fx.field"):
        problems.append("invalid hostPrimitive (requires schemaVersion 3)")
    if "shaderBundle" in manifest:
        if manifest.get("schemaVersion") not in (2, 3):
            problems.append("shaderBundle requires schemaVersion 2 or newer")
        bundle = manifest["shaderBundle"]
        if not isinstance(bundle, str) or not bundle.strip():
            problems.append("shaderBundle must be a package-relative path string")
        else:
            check_resource("shaderBundle")

    if "globalSettings" in manifest:
        settings = manifest["globalSettings"]
        if manifest.get("schemaVersion") != 3 or manifest.get("apiVersion", 1) != 2:
            problems.append("globalSettings requires schemaVersion 3 and apiVersion 2")
        if not isinstance(settings, list):
            problems.append("globalSettings must be an array")
        else:
            names, bindings = set(), set()
            for setting in settings:
                if not isinstance(setting, dict) or not isinstance(setting.get("name"), str) or not setting["name"].strip() or setting.get("type") != "Bool" or type(setting.get("default")) is not bool:
                    problems.append("globalSettings entries require a name, type Bool and boolean default")
                    continue
                name = setting["name"]
                if name in names or name in ("__proto__", "constructor", "prototype"):
                    problems.append(f"invalid or duplicate global setting name '{name}'")
                names.add(name)
                if "binding" in setting:
                    binding = setting["binding"]
                    if binding != "fx.field.captureScene" or primitive != "fx.field" or binding in bindings:
                        problems.append("invalid or duplicate global setting binding")
                    else:
                        bindings.add(binding)

    if "decorationDefaults" in manifest:
        defaults = manifest["decorationDefaults"]
        if (manifest.get("schemaVersion") != 3 or manifest.get("decoration") is not True or
                not isinstance(defaults, dict) or any(k != "color" for k in defaults) or
                ("color" in defaults and (not isinstance(defaults["color"], str) or
                                          not re.fullmatch(r"[0-9a-fA-F]{6}", defaults["color"])))):
            problems.append("decorationDefaults requires a decoration-backed schema 3 event and optional six-digit color")

    if "fieldUniforms" in manifest:
        uniforms = manifest["fieldUniforms"]
        reserved = {"uView", "uUnityTime", "_PEVScreenParams", "_EffectRT_TexelSize", "_TouchPosCount",
                    "_TouchPos", "_TouchPosShine", "uMasks", "uAuxMasks", "uDisplaceTex", "uSparkTex",
                    "uNoiseTex", "uActiveColors", "uDisabledColors", "_PEVBackground", "_GrabTexture"}
        if manifest.get("schemaVersion") != 3 or primitive != "fx.field" or not isinstance(uniforms, dict) or len(uniforms) > 128:
            problems.append("fieldUniforms requires a schema 3 fx.field event and an object with at most 128 entries")
        else:
            for name, value in uniforms.items():
                if not isinstance(name, str) or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", name) or name in reserved:
                    problems.append(f"invalid or reserved field uniform {name!r}")
                    continue
                values = value if isinstance(value, list) else [value]
                if (isinstance(value, list) and len(values) != 4) or not all(valid_uniform_number(v) for v in values):
                    problems.append(f"field uniform '{name}' must be a finite number or four finite numbers")

    if "fieldWaves" in manifest:
        waves = manifest["fieldWaves"]
        reserved = {"uView", "uUnityTime", "_PEVScreenParams", "_EffectRT_TexelSize", "_TouchPosCount", "_TouchPos",
                    "uMasks", "uAuxMasks", "uDisplaceTex", "uSparkTex", "uNoiseTex", "uActiveColors", "uDisabledColors", "_PEVBackground", "_GrabTexture"}
        existing_uniforms = manifest.get("fieldUniforms") if isinstance(manifest.get("fieldUniforms"), dict) else {}
        if manifest.get("schemaVersion") != 3 or primitive != "fx.field" or not isinstance(waves, dict) or len(waves) > 16:
            problems.append("fieldWaves requires a schema 3 fx.field event and at most 16 entries")
        else:
            for name, wave in waves.items():
                if (not isinstance(name, str) or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", name) or
                        name in reserved or name in existing_uniforms or not isinstance(wave, dict) or
                        set(wave) != {"base", "amplitude", "frequency", "scale"} or
                        not all(valid_uniform_number(v) for v in wave.values())):
                    problems.append(f"invalid field wave {name!r}")

    if "fieldTextures" in manifest or "fieldShaders" in manifest or (primitive == "fx.field" and manifest.get("shaderBundle")):
        texture_roles = {"displacement", "spark", "noise", "touch"}
        shader_roles = {"compute", "disabled", "activeCapture", "hoverCapture", "activeNoCapture", "hoverNoCapture",
                        "disabledColored", "activeCaptureColored", "hoverCaptureColored", "activeNoCaptureColored", "hoverNoCaptureColored"}
        textures, shaders = manifest.get("fieldTextures"), manifest.get("fieldShaders")
        if (manifest.get("schemaVersion") != 3 or primitive != "fx.field" or not isinstance(textures, dict) or
                not isinstance(shaders, dict) or set(textures) != texture_roles or set(shaders) != shader_roles):
            problems.append("field shader providers require fieldTextures and fieldShaders for every role")
        else:
            for kind, mapping in (("texture", textures), ("shader", shaders)):
                for role, path in mapping.items():
                    if not isinstance(path, str) or not path.strip():
                        problems.append(f"field {kind} '{role}' needs a package-relative path")
                        continue
                    normalized = path.replace("\\", "/")
                    if normalized.startswith("/") or ":" in normalized or ".." in PurePosixPath(normalized).parts:
                        problems.append(f"field {kind} '{role}' needs a package-relative path")
                    elif kind == "texture" and not os.path.isfile(os.path.join(root, normalized)):
                        problems.append(f"field texture '{path}' not found")

    category = manifest.get("category", "VisualFx")
    if not isinstance(category, str) or category not in VALID_CATEGORIES:
        problems.append(
            f"unknown category '{category}', will fall back to VisualFx. "
            f"Valid: {', '.join(sorted(VALID_CATEGORIES))}")

    params = manifest.get("params", [])
    if not isinstance(params, list):
        problems.append("params must be an array")
        return problems
    seen = set()
    for i, param in enumerate(params):
        if not isinstance(param, dict):
            problems.append(f"params[{i}] must be an object")
            continue
        name = param.get("name")
        if not isinstance(name, str) or not name.strip():
            problems.append(f"params[{i}] has no name")
            continue
        if name in seen or name == "__pevUid":
            problems.append(f"duplicate or reserved parameter name '{name}'")
        seen.add(name)
        ptype = param.get("type", "")
        if not isinstance(ptype, str) or not ptype:
            problems.append(f"param '{name}' has no type")
            continue
        # Enum types are written as "Enum:TypeName" or "Enum:TypeName, Assembly".
        valid_enum = ptype.startswith("Enum:") and bool(ptype[5:].strip())
        if not valid_enum and ptype not in VALID_TYPES:
            problems.append(
                f"param '{name}' has type '{ptype}', which the editor does not "
                f"recognise; the control will not appear. "
                f"Valid: {', '.join(sorted(VALID_TYPES))}, or Enum:TypeName")
        if ptype == "Choice":
            options = param.get("options")
            if (manifest.get("schemaVersion") != 3 or not isinstance(options, list) or not 1 <= len(options) <= 64 or
                    any(not isinstance(o, str) or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", o) or o == "value__" for o in options) or
                    len(set(options)) != len(options) or not isinstance(param.get("default"), str) or param["default"] not in options):
                problems.append(f"Choice '{name}' requires schemaVersion 3, 1-64 unique identifier options and a matching default")

    return problems


def collect_files(root):
    for dirpath, _dirnames, filenames in os.walk(root):
        for name in filenames:
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, root).replace(os.sep, "/")
            if rel.endswith(".event"):
                continue
            yield full, rel


def pack(root, output):
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as zf:
        for full, rel in collect_files(root):
            if os.path.abspath(full) == os.path.abspath(output):
                continue
            zf.write(full, rel)


def main():
    ap = argparse.ArgumentParser(description="Pack or validate a .event package")
    ap.add_argument("directory", help="directory containing event.json")
    ap.add_argument("-o", "--output", help="output .event path")
    ap.add_argument("--check", action="store_true",
                    help="validate only, write nothing")
    args = ap.parse_args()

    root = os.path.abspath(args.directory)
    manifest, err = load_manifest(root)
    if err:
        print(f"error: {err}", file=sys.stderr)
        return 1

    problems = validate(root, manifest)
    event_id = manifest.get("id", "<no id>") if isinstance(manifest, dict) else "<invalid>"

    if problems:
        print(f"{event_id}: {len(problems)} problem(s)")
        for p in problems:
            print(f"  - {p}")
    else:
        print(f"{event_id}: OK")

    if args.check:
        return 1 if problems else 0

    if problems:
        # Still a loadable package in most cases, but make the failure visible.
        print("\nRefusing to pack while problems remain. "
              "Use --check to inspect without packing.", file=sys.stderr)
        return 1

    if not args.output and any(c in event_id for c in '/\\:'):
        print("error: id cannot be used as an output filename; specify --output", file=sys.stderr)
        return 1
    output = args.output or f"{event_id}.event"
    pack(root, output)
    size = os.path.getsize(output)
    print(f"packed -> {output} ({size} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
