"""Offline checks of testsuite_observation.py; nothing here starts TIA."""
import pathlib
import tempfile
import unittest

import testsuite_observation as t

CHALLENGE = {"nonce": "n", "issuedUtc": "2026-10-29T10:00:00.0000000Z", "phase": "prepare", "requireLicenseCheck": True,
             "selectedProgramBlocks": ["Main"], "excludedBranches": ["Software Units"]}
ANSWERS = {"selectedProgramBlocks": ["Main"], "excludedBranches": ["Software Units"], "allLeavesVisible": True,
           "checkboxesChanged": False, "objectSelector": t.SELECTOR, "ruleEnabled": True,
           "licenseProduct": t.PRODUCT, "validKeys": 1, "defectiveKeys": 0}


class ObservationTests(unittest.TestCase):
    def test_the_document_has_every_field_the_worker_validates(self):
        document = t.observation(CHALLENGE, ANSWERS, [("scope-1.png", "a" * 64, "scope"), ("license-2.png", "b" * 64, "license")])
        self.assertEqual("operator-read-native-scope-editor", document["method"])
        self.assertIs(CHALLENGE, document["challenge"])
        self.assertEqual({"product": t.PRODUCT, "method": "Automation License Manager Check", "validKeys": 1, "defectiveKeys": 0}, document["license"])
        self.assertEqual(["scope", "license"], [s["purpose"] for s in document["screenshots"]])

    def test_no_license_block_unless_the_challenge_asks(self):
        self.assertNotIn("license", t.observation({**CHALLENGE, "requireLicenseCheck": False}, ANSWERS, []))

    def test_only_unanswered_challenges_are_pending(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            for name in ("answered", "open"):
                (root / name).mkdir()
                (root / name / "challenge.json").write_text("{}", encoding="utf-8")
            (root / "answered" / "observation.json").write_text("{}", encoding="utf-8")
            self.assertEqual([root / "open" / "challenge.json"], t.pending(root))


if __name__ == "__main__":
    unittest.main()
