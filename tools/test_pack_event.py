import copy
import tempfile
import unittest
import zipfile
from pathlib import Path

from pack_event import pack, validate
from stage_noise_draft import stage, stage_state


class PackageValidationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "main.js").write_text("function create() { return {}; }", encoding="utf-8")
        self.manifest = {"schemaVersion": 1, "id": "test.event", "version": "1.0.0", "entry": "main.js"}

    def test_valid_package(self):
        self.assertEqual([], validate(self.root, self.manifest))

    def test_non_object_manifest(self):
        for value in (None, [], "manifest", 1):
            with self.subTest(value=value):
                self.assertTrue(validate(self.root, value))

    def test_invalid_fields_are_reported(self):
        for field, values in {
            "schemaVersion": (None, "1", True, 0, 3, []),
            "apiVersion": (None, "1", True, 0, 2, {}),
            "id": (None, "", "  ", 3),
            "version": (None, "", 3),
            "entry": (None, "", []),
            "category": (None, [], {}),
            "params": (None, {}, "params", [None]),
        }.items():
            for value in values:
                with self.subTest(field=field, value=value):
                    manifest = dict(self.manifest, **{field: value})
                    self.assertTrue(validate(self.root, manifest))

    def test_resources_stay_inside_package(self):
        for path in ("../main.js", "..\\main.js", "/main.js", "C:\\main.js", "assets/../../main.js"):
            with self.subTest(path=path):
                self.assertTrue(validate(self.root, dict(self.manifest, entry=path)))

    def test_missing_resource(self):
        self.assertTrue(validate(self.root, dict(self.manifest, entry="absent.js")))

    def test_shader_bundle_version_and_package_boundary(self):
        (self.root / "field.bundle").write_bytes(b"fixture")
        valid = dict(self.manifest, schemaVersion=2, shaderBundle="field.bundle")
        self.assertEqual([], validate(self.root, valid))
        for path in (None, "", "../field.bundle", "/field.bundle", "C:\\field.bundle", "missing.bundle"):
            with self.subTest(path=path):
                self.assertTrue(validate(self.root, dict(valid, shaderBundle=path)))
        self.assertTrue(validate(self.root, dict(valid, schemaVersion=1)))

    def test_only_exact_property_types_are_valid(self):
        for type_name in ("Enum", "Enum:", "Enum: ", "Float:bad", "Number", [], None):
            with self.subTest(type_name=type_name):
                manifest = dict(self.manifest, params=[{"name": "x", "type": type_name}])
                self.assertTrue(validate(self.root, manifest))

    def test_qualified_enum_is_valid(self):
        manifest = dict(self.manifest, params=[{"name": "x", "type": "Enum:Example.Type, Assembly"}])
        self.assertEqual([], validate(self.root, manifest))

    def test_duplicate_and_reserved_names(self):
        for params in ([{"name": "x", "type": "Int"}] * 2, [{"name": "__pevUid", "type": "String"}]):
            with self.subTest(params=params):
                self.assertTrue(validate(self.root, dict(self.manifest, params=params)))

    def test_validation_does_not_mutate_manifest(self):
        before = copy.deepcopy(self.manifest)
        validate(self.root, self.manifest)
        self.assertEqual(before, self.manifest)

    def test_output_inside_source_is_not_archived(self):
        output = self.root / "output.zip"
        pack(self.root, output)
        with zipfile.ZipFile(output) as archive:
            self.assertEqual(["main.js"], archive.namelist())

    def test_noise_draft_staging_is_self_contained_and_never_overwrites(self):
        bundle = self.root / "draft.bundle"
        bundle.write_bytes(b"test fixture, not a Unity bundle")
        output = self.root / "draft.event"
        stage(bundle, output)
        with zipfile.ZipFile(output) as archive:
            names = set(archive.namelist())
            self.assertTrue({"event.json", "main.js", "icon.png", "assets/noise-field.bundle",
                             "assets/BlockNoise1.png", "assets/PointNoise.png", "assets/FD_Noise_00000.png",
                             "assets/TouchHover.png", "assets/README.md"}.issubset(names))
            self.assertIn(b"invert: !!params.invert", archive.read("main.js"))
            self.assertEqual(bundle.read_bytes(), archive.read("assets/noise-field.bundle"))
        old = output.read_bytes()
        with self.assertRaises(FileExistsError):
            stage(bundle, output)
        self.assertEqual(old, output.read_bytes())

    def test_noise_draft_rejects_missing_or_empty_bundle(self):
        bundle = self.root / "empty.bundle"
        bundle.write_bytes(b"")
        with self.assertRaises(ValueError):
            stage(bundle, self.root / "draft.event")
        self.assertFalse((self.root / "draft.event").exists())

    def test_noise_state_staging_never_overwrites(self):
        output = self.root / "state.event"
        stage_state(output)
        with zipfile.ZipFile(output) as archive:
            self.assertEqual({"event.json", "main.js"}, set(archive.namelist()))
        before = output.read_bytes()
        with self.assertRaises(FileExistsError):
            stage_state(output)
        self.assertEqual(before, output.read_bytes())


if __name__ == "__main__":
    unittest.main()
