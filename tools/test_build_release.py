import contextlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import build_release


class SplitReleaseTest(unittest.TestCase):
    def test_core_contains_no_events_and_packages_are_separate(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            core = root / "core"
            events = root / "events"
            packages = root / "packages"
            docs = root / "docs"
            (core / "bin/Release").mkdir(parents=True)
            packages.mkdir()
            docs.mkdir()
            (core / "bin/Release/PEV.Core.dll").write_bytes(b"core")
            (packages / "Jint.dll").write_bytes(b"jint")
            (packages / "Acornima.dll").write_bytes(b"acornima")
            (root / "LICENSE").write_text("license", encoding="utf-8")
            (docs / "INSTALL-zh-CN.md").write_text("install", encoding="utf-8")
            (docs / "third-party").mkdir()
            for name in ("Jint-LICENSE.txt", "Acornima-LICENSE.txt"):
                (docs / "third-party" / name).write_text("third party license", encoding="utf-8")
            for name in ("NoiseField.event",):
                folder = events / name
                folder.mkdir(parents=True)
                (folder / "main.js").write_text("function create() {}", encoding="utf-8")
                (folder / "event.json").write_text(json.dumps({
                    "schemaVersion": 1, "id": name, "version": "1.0.0", "entry": "main.js"
                }), encoding="utf-8")
            output = root / "out"
            replacements = {"ROOT": str(root), "CORE": str(core), "EVENTS": str(events), "PACKAGES": str(packages)}
            with patch.multiple(build_release, **replacements), patch.object(
                sys, "argv", ["build_release.py", "--skip-build", "--version", "1.0.0", "--out", str(output)]
            ), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(0, build_release.main())
                core_zip = output / "PEV.Core-1.0.0.zip"
                original = core_zip.read_bytes()
                with zipfile.ZipFile(core_zip) as archive:
                    self.assertIn("PEV.Core/PEV.Core.dll", archive.namelist())
                    self.assertIn("PEV.Core/third-party-licenses/Jint-LICENSE.txt", archive.namelist())
                    self.assertIn("PEV.Core/third-party-licenses/Acornima-LICENSE.txt", archive.namelist())
                    self.assertFalse(any(name.startswith("PEV.Core/Events/")
                                         for name in archive.namelist()))
                for name in ("NoiseField.event",):
                    with zipfile.ZipFile(output / name) as archive:
                        self.assertEqual(name, json.loads(archive.read("event.json"))["id"])
                self.assertEqual({"PEV.Core-1.0.0.zip", "NoiseField.event"}, {p.name for p in output.iterdir()})
                with self.assertRaises(SystemExit):
                    build_release.main()
                self.assertEqual(original, core_zip.read_bytes())


if __name__ == "__main__":
    unittest.main()
