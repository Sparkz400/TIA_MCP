import tempfile
import unittest
from pathlib import Path

from probe_library_api import inspect_api


class LibraryApiProbeTests(unittest.TestCase):
    @staticmethod
    def scratch():
        root = Path(__file__).resolve().parents[1] / "artifacts" / "library-test-temp"
        root.mkdir(parents=True, exist_ok=True)
        return tempfile.TemporaryDirectory(prefix="probe_", dir=root)

    def test_missing_optional_product_is_metadata_incomplete_not_native_failure(self):
        with self.scratch() as directory:
            report = inspect_api(Path(directory), {"hmi": ("Optional.Hmi", ("T:Example",))})
        self.assertEqual("incomplete", report["groups"][0]["metadataStatus"])
        self.assertFalse(report["nativeCodeExecuted"])
        self.assertFalse(report["nativeQualified"])

    def test_exact_overloads_and_hashes_are_retained_without_native_execution(self):
        with self.scratch() as directory:
            root = Path(directory)
            (root / "Fake.xml").write_text('<doc><members><member name="M:Example.Open(System.String)"><summary>Open.</summary></member><member name="M:Example.Open(System.IO.FileInfo)"><summary>Other.</summary></member></members></doc>')
            (root / "Fake.dll").write_bytes(b"not executable")
            report = inspect_api(root, {"open": ("Fake", ("M:Example.Open(",))})
        self.assertEqual("available", report["groups"][0]["metadataStatus"])
        self.assertEqual(2, len(report["groups"][0]["observations"][0]["matches"]))
        self.assertEqual(64, len(report["assemblies"]["Fake"]["assemblySha256"]))
        self.assertFalse(report["nativeQualified"])

    def test_malformed_document_cannot_report_available(self):
        with self.scratch() as directory:
            root = Path(directory)
            (root / "Fake.xml").write_text('<doc><members>')
            report = inspect_api(root, {"open": ("Fake", ("M:Example.Open(",))})
        self.assertEqual("incomplete", report["groups"][0]["metadataStatus"])
        self.assertIn("metadataError", report["assemblies"]["Fake"])


if __name__ == "__main__":
    unittest.main()
