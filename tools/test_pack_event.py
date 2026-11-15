import copy
import tempfile
import unittest
import zipfile
from pathlib import Path

from pack_event import pack, validate


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
            "schemaVersion": (None, "1", True, 0, 2, []),
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


if __name__ == "__main__":
    unittest.main()
