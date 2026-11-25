"""Offline checks for the software-unit library qualifier's created-alongside case."""
from pathlib import Path
import tempfile
import unittest

from qualify_library_units import CREATED_FB, CREATED_UDT, DEPENDENCY, DEPENDENT_FB, UNIT, alongside_documents, created_alongside

SCOPE = "/devices/PLC_1/plc/PLC_1/units/" + UNIT
FB_PATH = SCOPE + "/blocks/namespaces/" + UNIT + "/" + CREATED_FB
UDT_PATH = SCOPE + "/types/" + CREATED_UDT


def software(fb=FB_PATH, udt=UDT_PATH, scope=SCOPE):
    return {"blocks": [{"path": fb, "scopePath": scope}, {"path": SCOPE + "/blocks/Other", "scopePath": SCOPE}],
            "types": [{"path": udt, "scopePath": scope}]}


class CreatedAlongsideTests(unittest.TestCase):
    def test_namespaced_fb_and_udt_without_namespace(self):
        self.assertEqual(created_alongside(software(), SCOPE), {CREATED_FB: FB_PATH, CREATED_UDT: UDT_PATH})

    def test_udt_in_the_fb_namespace_or_fb_without_it_fails(self):
        for fb, udt in ((FB_PATH, SCOPE + "/types/namespaces/" + UNIT + "/" + CREATED_UDT), (SCOPE + "/blocks/" + CREATED_FB, UDT_PATH)):
            with self.assertRaises(AssertionError):
                created_alongside(software(fb, udt), SCOPE)

    def test_missing_or_outside_the_unit_fails(self):
        for value in ({"blocks": [], "types": [{"path": UDT_PATH, "scopePath": SCOPE}]}, software(scope="/devices/PLC_1/plc/PLC_1")):
            with self.assertRaises(AssertionError):
                created_alongside(value, SCOPE)


class AlongsideDocumentsTests(unittest.TestCase):
    def test_component_documents_renamed(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = alongside_documents(Path(directory) / "documents")
            self.assertEqual(sorted(p.name for p in folder.iterdir()), [CREATED_FB + ".scl", CREATED_UDT + ".udt"])
            fb = (folder / (CREATED_FB + ".scl")).read_text(encoding="utf-8")
            self.assertIn('FUNCTION_BLOCK "%s"' % CREATED_FB, fb)
            self.assertIn('State : "%s";' % CREATED_UDT, fb)
            self.assertIn('TYPE "%s"' % CREATED_UDT, (folder / (CREATED_UDT + ".udt")).read_text(encoding="utf-8"))
            for path in folder.iterdir():
                text = path.read_text(encoding="utf-8")
                self.assertNotIn(DEPENDENT_FB, text)
                self.assertNotIn(DEPENDENCY, text)


if __name__ == "__main__":
    unittest.main()
