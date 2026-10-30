"""Offline checks of the pure helpers in qualify_block_protection.py; nothing here starts TIA."""
import base64
import pathlib
import unittest

import qualify_block_protection as q

ROOT = pathlib.Path(__file__).resolve().parents[1]


class BlockProtectionRunnerTests(unittest.TestCase):
    def test_binding_matches_the_worker_derivation(self):
        source = "".join((ROOT / "src/TiaMcp.Openness.V21" / name).read_text(encoding="utf-8") for name in ("OpennessSession.BlockProtection.cs", "RoutedCredentials.cs"))
        binding = q.binding("/p/plc", "/p/plc/blocks/FB", "write")
        self.assertEqual({"provider", "authority", "ownerId", "resourceKind", "resourceId"}, set(binding["resource"]))
        for literal in (binding["resource"]["provider"], binding["resource"]["resourceKind"], binding["purpose"], binding["subjectId"], '"write" : "knowHow"'):
            self.assertIn(literal, source)
        self.assertEqual(("/p/plc", "/p/plc/blocks/FB"), (binding["resource"]["authority"], binding["resource"]["ownerId"]))

    def test_leaks_finds_every_encoding_and_nothing_else(self):
        password = "BpSynthetic-123"
        for data in (password.encode(), password.encode("utf-16-le"), base64.b64encode(password.encode()), b"x" + password.encode() + b"y"):
            self.assertTrue(q.leaks(data, [password]))
        self.assertFalse(q.leaks(b"BpSynthetic-12", [password]))
        self.assertFalse(q.leaks(b"anything", []))


if __name__ == "__main__":
    unittest.main()
