"""Every native runner that requests a safety write launches its Host with --enable-safety-changes.

Since 20 November 2026 the Host refuses safety_import_xml, safety_fparameter_set, safety_drive_tags and a tag_set with
safetyAcknowledged (SafetyChangesDisabled) unless it is launched with that option; runners written before then failed
natively at their first safety write. This reads the runners' source, as the gate itself is checked by the managed suite.
"""
import pathlib
import re
import unittest

SCRIPTS = pathlib.Path(__file__).resolve().parent.parent
SAFETY_WRITE = re.compile(r'''["'](safety_import_xml|safety_drive_tags)["']|KIND\s*=\s*["']safety_fparameter_set["']'''
                          r'''|["']safetyAcknowledged["']\s*:\s*True''')
STARTS_HOST = re.compile(r"\bHarness\(|\bClient\(")
# Offline tests (test_*.py) use fake clients and are skipped. Runners that name a safety kind without requesting it through a Host they start, or that use the narrower option the
# gate also accepts for safety_fparameter_set.
EXEMPT = {
    "probe_safety_fparameters.py": "--safety-fparameter-discovery admits safety_fparameter_set, the only kind it writes",
    "qualify_interruption.py": "lists safety_fparameter_set only to classify hardware rule kinds",
}


class SafetyRunnerHostFlagTests(unittest.TestCase):
    def test_safety_writing_runners_enable_safety_changes(self):
        missing = []
        for path in sorted(SCRIPTS.glob("*.py")):
            text = path.read_text(encoding="utf-8")
            if path.name in EXEMPT or path.name.startswith("test_") or not SAFETY_WRITE.search(text) or not STARTS_HOST.search(text):
                continue
            if "--enable-safety-changes" not in text:
                missing.append(path.name)
        self.assertEqual([], missing, "Launch the Host with --enable-safety-changes (scratch only)")

    def test_known_runners_are_covered(self):
        # The rule must keep finding the runners it was written for; a renamed kind would silently match nothing.
        found = {p.name for p in SCRIPTS.glob("*.py") if SAFETY_WRITE.search(p.read_text(encoding="utf-8"))}
        self.assertLessEqual({"qualify_unrelated_errors.py", "qualify_safety_changes.py", "qualify_safety_drive_tags.py",
                              "probe_safety_kinds.py", "probe_safety_logic.py"}, found)

    def test_exemptions_still_exist(self):
        self.assertEqual([], [name for name in EXEMPT if not (SCRIPTS / name).is_file()])


if __name__ == "__main__":
    unittest.main()
