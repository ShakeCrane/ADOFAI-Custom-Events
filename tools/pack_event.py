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
import os
import sys
import zipfile

REQUIRED_FIELDS = ("schemaVersion", "id", "version", "entry")
VALID_TYPES = {
    "Array", "Bool", "Color", "Export", "File", "FilterProperties", "Float",
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
    except json.JSONDecodeError as exc:
        return None, f"event.json is not valid JSON: {exc}"


def validate(root, manifest):
    """Returns a list of problems. Empty means the package will load."""
    problems = []

    for field in REQUIRED_FIELDS:
        if field not in manifest:
            problems.append(f"missing required field '{field}'")

    if manifest.get("schemaVersion", 1) > 1:
        problems.append(
            f"schemaVersion {manifest['schemaVersion']} is newer than supported (1)")

    entry = manifest.get("entry")
    if entry and not os.path.isfile(os.path.join(root, entry)):
        problems.append(f"entry script '{entry}' not found")

    icon = manifest.get("icon")
    if icon and not os.path.isfile(os.path.join(root, icon)):
        problems.append(f"icon '{icon}' not found (the event will use a placeholder)")

    category = manifest.get("category", "VisualFx")
    if category not in VALID_CATEGORIES:
        problems.append(
            f"unknown category '{category}', will fall back to VisualFx. "
            f"Valid: {', '.join(sorted(VALID_CATEGORIES))}")

    for i, param in enumerate(manifest.get("params", [])):
        name = param.get("name")
        if not name:
            problems.append(f"params[{i}] has no name")
            continue
        ptype = param.get("type", "")
        if not ptype:
            problems.append(f"param '{name}' has no type")
            continue
        # Enum types are written as "Enum:TypeName" or "Enum:TypeName, Assembly".
        base = ptype.split(":", 1)[0]
        if base != "Enum" and base not in VALID_TYPES:
            problems.append(
                f"param '{name}' has type '{ptype}', which the editor does not "
                f"recognise; the control will not appear. "
                f"Valid: {', '.join(sorted(VALID_TYPES))}, or Enum:TypeName")

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
    event_id = manifest.get("id", "<no id>")

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

    output = args.output or f"{event_id}.event"
    pack(root, output)
    size = os.path.getsize(output)
    print(f"packed -> {output} ({size} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
