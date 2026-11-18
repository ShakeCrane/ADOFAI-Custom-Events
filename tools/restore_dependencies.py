#!/usr/bin/env python3
"""Restore the pinned .NET Framework assemblies used to build PEV.Core."""

import hashlib
import io
from pathlib import Path
from urllib.request import urlopen
from zipfile import ZipFile


ROOT = Path(__file__).resolve().parents[1]
NUGET = "https://api.nuget.org/v3-flatcontainer"
PACKAGES = (
    ("jint", "4.17.0", "Jint.dll", "3330d3a7c7f9640601e0db89f7e09e6f35d837b1c89c06c4cf6fd1dc562cbe59"),
    ("acornima", "1.7.0", "Acornima.dll", "44c74d86bf468a71986380b787bf32fc6a38ecdc293396fd1056b0bb94dbf404"),
)


def restore(destination):
    destination.mkdir(parents=True, exist_ok=True)
    for package, version, assembly, expected in PACKAGES:
        url = f"{NUGET}/{package}/{version}/{package}.{version}.nupkg"
        with urlopen(url, timeout=30) as response:
            archive = response.read(16 * 1024 * 1024 + 1)
        if hashlib.sha256(archive).hexdigest() != expected:
            raise SystemExit(f"NuGet checksum mismatch: {package} {version}")
        with ZipFile(io.BytesIO(archive)) as package_zip:
            contents = package_zip.read(f"lib/net462/{assembly}")
        path = destination / assembly
        if not path.exists():
            with path.open("xb") as output:
                output.write(contents)
        elif path.read_bytes() != contents:
            raise SystemExit(f"Existing file differs from the pinned assembly: {path}")
        print(f"Restored {assembly} ({version})")


if __name__ == "__main__":
    restore(ROOT / "packages" / "jint-lib")
