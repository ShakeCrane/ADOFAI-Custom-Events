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


def validate(root, manifest, _included=False):
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

    if "includedEvents" in manifest:
        included = manifest["includedEvents"]
        if (_included or manifest.get("schemaVersion", 0) < 4 or not isinstance(included, list) or
                not 1 <= len(included) <= 16):
            problems.append("includedEvents requires schemaVersion 4, 1-16 paths and no nesting")
        else:
            paths, ids = set(), set()
            if isinstance(manifest.get("id"), str):
                ids.add(manifest["id"])
            for path in included:
                if not isinstance(path, str) or not path.strip():
                    problems.append("invalid included event path")
                    continue
                normalized = path.replace("\\", "/")
                if (normalized.startswith("/") or ":" in normalized or ".." in PurePosixPath(normalized).parts or
                        normalized.lower() == "event.json" or normalized.lower() in paths):
                    problems.append("invalid or duplicate included event path")
                    continue
                paths.add(normalized.lower())
                try:
                    with open(os.path.join(root, normalized), encoding="utf-8") as fh:
                        child = json.load(fh)
                except (OSError, UnicodeError, json.JSONDecodeError) as exc:
                    problems.append(f"cannot read included event '{normalized}': {exc}")
                    continue
                if isinstance(child, dict):
                    child_id = child.get("id")
                    if isinstance(child_id, str) and child_id in ids:
                        problems.append(f"duplicate event id '{child_id}'")
                    if isinstance(child_id, str):
                        ids.add(child_id)
                problems.extend(f"included event '{normalized}': {p}" for p in validate(root, child, _included=True))

    for declaration in ("vectorPrograms", "computeGraphs", "pixelResources", "keySpecs"):
        if declaration not in manifest:
            continue
        programs = manifest[declaration]
        if manifest.get("schemaVersion", 0) < 4 or not isinstance(programs, dict) or not 1 <= len(programs) <= 16:
            problems.append(f"{declaration} requires schemaVersion 4 and 1-16 package-relative resources")
        else:
            for name, path in programs.items():
                if (not isinstance(name, str) or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", name) or
                        name in ("__proto__", "constructor", "prototype") or not isinstance(path, str) or not path.strip()):
                    problems.append(f"invalid {declaration} declaration")
                    continue
                normalized = path.replace("\\", "/")
                if (normalized.startswith("/") or ":" in normalized or ".." in PurePosixPath(normalized).parts or
                        not os.path.isfile(os.path.join(root, normalized))):
                    problems.append(f"invalid or missing {declaration} resource '{name}'")

    if "runtimeBindings" in manifest:
        bindings = manifest["runtimeBindings"]
        if manifest.get("schemaVersion", 0) < 4 or not isinstance(bindings, dict) or not 1 <= len(bindings) <= 16:
            problems.append("runtimeBindings requires schemaVersion 4 and 1-16 named resource roles")
        else:
            programs = manifest.get("vectorPrograms")
            graphs = manifest.get("computeGraphs")
            specs = manifest.get("keySpecs")
            roles = (set(programs if isinstance(programs, dict) else ()) | set(graphs if isinstance(graphs, dict) else ()) |
                     set(specs if isinstance(specs, dict) else ()))
            for name, role in bindings.items():
                if (not isinstance(name, str) or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", name) or
                        name in ("__proto__", "constructor", "prototype") or not isinstance(role, str) or role not in roles):
                    problems.append("invalid runtime resource binding")

    if "shaderAssets" in manifest:
        assets = manifest["shaderAssets"]
        if (manifest.get("schemaVersion", 0) < 4 or not manifest.get("shaderBundle") or not isinstance(assets, dict) or
                not 1 <= len(assets) <= 32):
            problems.append("shaderAssets requires schemaVersion 4 and a shader bundle")
        else:
            for name, path in assets.items():
                if (not isinstance(name, str) or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", name) or name.startswith("__") or
                        not isinstance(path, str) or not path.strip() or path.replace("\\", "/").startswith("/") or
                        ":" in path or ".." in PurePosixPath(path.replace("\\", "/")).parts):
                    problems.append(f"invalid shader asset {name!r}")

    if "imageAssets" in manifest:
        images = manifest["imageAssets"]
        if manifest.get("schemaVersion", 0) < 4 or not isinstance(images, dict) or not 1 <= len(images) <= 16:
            problems.append("imageAssets requires schemaVersion 4 and 1-16 named images")
        else:
            for name, spec in images.items():
                if (not isinstance(name, str) or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", name) or name.startswith("__") or
                        not isinstance(spec, dict) or set(spec) != {"path", "wrap", "filter", "flipY", "linear"} or
                        spec.get("wrap") not in ("clamp", "repeat", "mirror") or spec.get("filter") not in ("point", "linear") or
                        type(spec.get("flipY")) is not bool or type(spec.get("linear")) is not bool or
                        not isinstance(spec.get("path"), str)):
                    problems.append(f"invalid image asset {name!r}")
                    continue
                path = spec["path"].replace("\\", "/")
                if not path.strip() or path.startswith("/") or ":" in path or ".." in PurePosixPath(path).parts or not os.path.isfile(os.path.join(root, path)):
                    problems.append(f"invalid or missing image asset {name!r}")

    for field in REQUIRED_FIELDS:
        if field not in manifest:
            problems.append(f"missing required field '{field}'")

    for field in ("schemaVersion", "apiVersion"):
        value = manifest.get(field, 1)
        supported = (1, 2, 3, 4, 5) if field == "schemaVersion" else (1, 2)
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
    if "hostPrimitive" in manifest and (manifest.get("schemaVersion", 0) < 3 or
            primitive not in ("fx.field", "fx.video")):
        problems.append("invalid hostPrimitive (requires schemaVersion 3 and fx.field or fx.video)")
    if "shaderBundle" in manifest:
        if manifest.get("schemaVersion", 0) < 2:
            problems.append("shaderBundle requires schemaVersion 2 or newer")
        bundle = manifest["shaderBundle"]
        if not isinstance(bundle, str) or not bundle.strip():
            problems.append("shaderBundle must be a package-relative path string")
        else:
            check_resource("shaderBundle")

    if "globalSettings" in manifest:
        settings = manifest["globalSettings"]
        if manifest.get("schemaVersion", 0) < 3 or manifest.get("apiVersion", 1) != 2:
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
                    if (not isinstance(binding, str) or not re.fullmatch(r"[A-Za-z][A-Za-z0-9_.]{0,127}", binding) or binding in bindings):
                        problems.append("invalid or duplicate global setting binding")
                    else:
                        bindings.add(binding)

    if "hiddenDecorationProperties" in manifest:
        hidden = manifest["hiddenDecorationProperties"]
        if (manifest.get("schemaVersion", 0) < 4 or manifest.get("decoration") is not True or not isinstance(hidden, list) or len(hidden) > 32
                or any(not isinstance(name, str) or not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]{0,63}", name) for name in hidden)):
            problems.append("invalid hiddenDecorationProperties for schema 4 decoration event")

    if "decorationDefaults" in manifest:
        defaults = manifest["decorationDefaults"]
        if (manifest.get("schemaVersion", 0) < 3 or manifest.get("decoration") is not True or
                not isinstance(defaults, dict) or any(k not in ("color", "tag") for k in defaults) or
                ("color" in defaults and (not isinstance(defaults["color"], str) or
                                          not re.fullmatch(r"[0-9a-fA-F]{6}", defaults["color"]))) or
                ("tag" in defaults and (not isinstance(defaults["tag"], str) or len(defaults["tag"]) > 128 or
                                        (defaults["tag"] and not defaults["tag"].strip())))):
            problems.append("decorationDefaults requires a decoration-backed schema 3 event, optional six-digit color and tag")

    if "decorationListName" in manifest:
        list_name = manifest["decorationListName"]
        if (manifest.get("schemaVersion", 0) < 4 or manifest.get("decoration") is not True or
                not isinstance(list_name, str) or not list_name.strip() or len(list_name) > 128):
            problems.append("invalid decorationListName for schema 4 decoration event")

    if "decorationMarker" in manifest:
        # A carrier marker is a deliberately nonexistent path. It has to stay inside the
        # chart directory and start with the shared prefix, because that is how Core tells a
        # carrier row apart from a real image decoration.
        marker = manifest["decorationMarker"]
        if (manifest.get("schemaVersion") != 4 or manifest.get("decoration") is not True or
                not isinstance(marker, str) or not re.fullmatch(r"pev/[a-z0-9_-]+(/[a-z0-9_-]+)*", marker or "")):
            problems.append("invalid decorationMarker for schema 4 decoration event "
                            "(expected pev/<lower-case path>)")

    if "decorationMigration" in manifest:
        migrations = manifest["decorationMigration"]
        if manifest.get("schemaVersion", 0) < 3 or manifest.get("decoration") is not True or not isinstance(migrations, list) or len(migrations) > 16:
            problems.append("decorationMigration requires a schema 3 decoration event and at most 16 bindings")
        else:
            allowed = {("scale", "tilesToScale"), ("position", "vector2"), ("rotation", "float"),
                       ("tag", "string"), ("color", "string"), ("depth", "int"), ("opacity", "percent")}
            targets = set()
            for item in migrations:
                if not isinstance(item, dict) or set(item) != {"source", "target", "conversion"}:
                    problems.append("invalid decorationMigration binding")
                    continue
                source, target, conversion = item["source"], item["target"], item["conversion"]
                if (not isinstance(source, str) or source.startswith("__") or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", source) or
                        not isinstance(target, str) or not isinstance(conversion, str) or (target, conversion) not in allowed or target in targets):
                    problems.append("invalid or duplicate decorationMigration binding")
                else:
                    targets.add(target)

    if "fieldUniforms" in manifest:
        uniforms = manifest["fieldUniforms"]
        reserved = {"uView", "uUnityTime", "_PEVScreenParams", "_EffectRT_TexelSize", "_TouchPosCount",
                    "_TouchPos", "_TouchPosShine", "uMasks", "uAuxMasks", "uDisplaceTex", "uSparkTex",
                    "uNoiseTex", "uActiveColors", "uDisabledColors", "_PEVBackground", "_GrabTexture"}
        if manifest.get("schemaVersion", 0) < 3 or primitive != "fx.field" or not isinstance(uniforms, dict) or len(uniforms) > 128:
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
        if manifest.get("schemaVersion", 0) < 3 or primitive != "fx.field" or not isinstance(waves, dict) or len(waves) > 16:
            problems.append("fieldWaves requires a schema 3 fx.field event and at most 16 entries")
        else:
            for name, wave in waves.items():
                if (not isinstance(name, str) or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", name) or
                        name in reserved or name in existing_uniforms or not isinstance(wave, dict) or
                        set(wave) != {"base", "amplitude", "frequency", "scale"} or
                        not all(valid_uniform_number(v) for v in wave.values())):
                    problems.append(f"invalid field wave {name!r}")

    if "fieldTextures" in manifest or "fieldShaders" in manifest or (manifest.get("schemaVersion") == 3 and primitive == "fx.field" and manifest.get("shaderBundle")):
        texture_roles = {"displacement", "spark", "noise", "touch"}
        shader_roles = {"compute", "disabled", "activeCapture", "hoverCapture", "activeNoCapture", "hoverNoCapture",
                        "disabledColored", "activeCaptureColored", "hoverCaptureColored", "activeNoCaptureColored", "hoverNoCaptureColored"}
        textures, shaders = manifest.get("fieldTextures"), manifest.get("fieldShaders")
        named_shaders = (isinstance(shaders, dict) and 1 <= len(shaders) <= 32 and "compute" in shaders and
                         all(isinstance(name, str) and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", name) and
                             not name.startswith("__") for name in shaders))
        shader_roles_ok = set(shaders) == shader_roles if isinstance(shaders, dict) and manifest.get("schemaVersion") == 3 else named_shaders
        if (manifest.get("schemaVersion", 0) < 3 or primitive != "fx.field" or not isinstance(textures, dict) or
                not isinstance(shaders, dict) or set(textures) != texture_roles or not shader_roles_ok):
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

    if "legacyFieldTextures" in manifest:
        textures = manifest["legacyFieldTextures"]
        if (manifest.get("schemaVersion", 0) < 3 or primitive != "fx.field" or
                not isinstance(textures, dict) or set(textures) != {"block", "point"}):
            problems.append("legacyFieldTextures requires block and point roles on a schema 3 fx.field event")
        else:
            for role, path in textures.items():
                if not isinstance(path, str) or not path.strip():
                    problems.append(f"legacy field texture '{role}' needs a package-relative path")
                    continue
                normalized = path.replace("\\", "/")
                if normalized.startswith("/") or ":" in normalized or ".." in PurePosixPath(normalized).parts:
                    problems.append(f"legacy field texture '{role}' needs a package-relative path")
                elif not os.path.isfile(os.path.join(root, normalized)):
                    problems.append(f"legacy field texture '{path}' not found")

    if "legacyFieldProfile" in manifest or "legacyFieldTextures" in manifest:
        profile = manifest.get("legacyFieldProfile")
        scalars = {"composeScale", "composeDisplaceSpeed", "composeDisplaceStrength", "displaceBlendIntensity",
                   "activeSparkDisplaceIntensity", "activeSparkHueShiftAmount", "fillStrength", "activeDisplaceSpeed",
                   "activeDisplaceStrength", "sparkMapOpacity", "fillValue", "glowSaturation", "sparkSaturation", "grainPixels"}
        vectors = {"composeDisplaceDirection", "composeDisplaceMapScale", "activeDisplaceDirection",
                   "activeDisplaceMapScale", "activeSparkMapScale"}
        if (manifest.get("schemaVersion", 0) < 3 or primitive != "fx.field" or not isinstance(profile, dict) or
                set(profile) != scalars | vectors | {"glowWeights"}):
            problems.append("legacyFieldProfile requires all declared scalar, vector and glow weight roles")
        else:
            for name, value in profile.items():
                values = value if isinstance(value, list) else [value]
                expected = 1 if name in scalars else 2 if name in vectors else 5
                if (len(values) != expected or (name in scalars and isinstance(value, list)) or
                        not all(valid_uniform_number(v) and abs(v) <= 1024 for v in values)):
                    problems.append(f"invalid legacyFieldProfile value '{name}'")
            scale = profile["composeScale"]
            if not valid_uniform_number(scale) or not 0 < scale <= 1:
                problems.append("legacyFieldProfile composeScale must be in (0,1]")

    if "fieldPreview" in manifest:
        preview = manifest["fieldPreview"]
        if (manifest.get("schemaVersion", 0) < 3 or primitive != "fx.field" or not isinstance(preview, dict) or
                set(preview) != {"key", "parameterMap"} or not isinstance(preview.get("key"), str) or
                not preview["key"].strip() or len(preview["key"]) > 128 or not isinstance(preview.get("parameterMap"), dict) or
                len(preview["parameterMap"]) > 64):
            problems.append("fieldPreview requires a key and parameterMap on a schema 3 fx.field event")
        else:
            seen_targets = set()
            for source, target in preview["parameterMap"].items():
                if (not isinstance(source, str) or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", source) or source.startswith("__") or
                        not isinstance(target, str) or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", target) or target.startswith("__") or target in seen_targets):
                    problems.append("invalid or duplicate fieldPreview parameter mapping")
                else:
                    seen_targets.add(target)

    category = manifest.get("category", "VisualFx")
    if not isinstance(category, str) or category not in VALID_CATEGORIES:
        problems.append(
            f"unknown category '{category}', will fall back to VisualFx. "
            f"Valid: {', '.join(sorted(VALID_CATEGORIES))}")
    if "screenEffect" in manifest:
        screen = manifest["screenEffect"]
        assets = manifest.get("shaderAssets")
        if (manifest.get("schemaVersion", 0) < 5 or not manifest.get("shaderBundle") or
                not isinstance(screen, dict) or set(screen) - {"shader", "pass"} or
                not isinstance(screen.get("shader"), str) or
                not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", screen["shader"]) or
                not isinstance(assets, dict) or screen["shader"] not in assets):
            problems.append("screenEffect requires schemaVersion 5, a shader bundle and a declared shader role")
        elif "pass" in screen and (type(screen["pass"]) is not int or not 0 <= screen["pass"] <= 8):
            problems.append("screenEffect pass must be an integer between 0 and 8")

    if "editorPanel" in manifest:
        panel = manifest["editorPanel"]
        declared = {p.get("name") for p in manifest.get("params", []) if isinstance(p, dict)}
        blobs = []
        if manifest.get("schemaVersion", 0) < 5 or not isinstance(panel, dict):
            problems.append("editorPanel requires schemaVersion 5 and an object")
        else:
            for key in ("curves", "hueCurves"):
                if key in panel:
                    if not isinstance(panel[key], dict):
                        problems.append(f"editorPanel {key} must map channel names to parameters")
                    else:
                        blobs.extend(panel[key].values())
            if "wheels" in panel:
                if not isinstance(panel["wheels"], list) or not 1 <= len(panel["wheels"]) <= 8:
                    problems.append("editorPanel wheels must list 1-8 wheels")
                else:
                    for wheel in panel["wheels"]:
                        if not isinstance(wheel, dict):
                            problems.append("editorPanel wheel must be an object")
                            continue
                        blobs.extend([wheel.get("xy"), wheel.get("luma")])
            if "lut" in panel:
                lut = panel["lut"]
                if not isinstance(lut, dict):
                    problems.append("editorPanel lut must be an object")
                else:
                    blobs.extend([lut.get("preset"), lut.get("file"), lut.get("strength")])
            if "compare" in panel:
                blobs.append(panel["compare"])
            for name in blobs:
                if name is not None and (not isinstance(name, str) or name not in declared):
                    problems.append(f"editorPanel refers to an undeclared parameter: {name!r}")

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
            if (manifest.get("schemaVersion", 0) < 3 or not isinstance(options, list) or not 1 <= len(options) <= 64 or
                    any(not isinstance(o, str) or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", o) or o == "value__" for o in options) or
                    len(set(options)) != len(options) or not isinstance(param.get("default"), str) or param["default"] not in options):
                problems.append(f"Choice '{name}' requires schemaVersion 3, 1-64 unique identifier options and a matching default")
        if "fileType" in param and (ptype != "File" or param.get("fileType") not in ("Audio", "Image", "Video")):
            problems.append(f"param '{name}': fileType is only valid on a File param and must be Audio, Image or Video")

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
