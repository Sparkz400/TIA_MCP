import unittest

from validate_milestones import changed_keys, export_digest, require_effects

EXPORT = '<?xml version="1.0"?>\n<Document>\n  <DocumentInfo>\n    <Created>%s</Created>\n  </DocumentInfo>\n  <SW.Blocks.FC ID="0">%s</SW.Blocks.FC>\n</Document>'


class MilestoneReadbackTests(unittest.TestCase):
    def test_export_digest_ignores_only_the_export_time(self):
        first = export_digest(EXPORT % ("2026-10-24T08:54:24Z", "Enable"))
        self.assertEqual(first, export_digest(EXPORT % ("2026-10-28T10:00:00Z", "Enable")))
        self.assertNotEqual(first, export_digest(EXPORT % ("2026-10-24T08:54:24Z", "EnableAlternate")))

    def test_changed_keys_include_added_removed_and_none_values(self):
        before = {"block A": "1", "block B": "2", "network X1 RouterAddress": None}
        after = {"block A": "1", "block C": "3", "network X1 RouterAddress": "192.168.0.254"}
        self.assertEqual(["block B", "block C", "network X1 RouterAddress"], changed_keys(before, after))
        self.assertEqual(["block B"], changed_keys(before, {"block A": "1", "network X1 RouterAddress": None}))

    def test_require_effects_accepts_own_and_optional_dependent_changes(self):
        before = {"block IsEnabled": "1", "block EnableGate": "2", "block Main": "3"}
        after = {"block IsEnabled_Renamed": "1", "block EnableGate": "4", "block Main": "3"}
        changed = require_effects(before, after, {"block IsEnabled", "block IsEnabled_Renamed"}, {"block EnableGate"})
        self.assertEqual(["block EnableGate", "block IsEnabled", "block IsEnabled_Renamed"], changed)
        self.assertEqual([], require_effects(before, dict(before), ()))

    def test_require_effects_rejects_missing_and_unrelated_changes(self):
        before = {"block Fixture": "1", "block Main": "3"}
        with self.assertRaisesRegex(AssertionError, r"missing: \['block Fixture'\]"):
            require_effects(before, dict(before), {"block Fixture"})
        with self.assertRaisesRegex(AssertionError, r"unrelated changes: \['block Main'\]"):
            require_effects(before, {"block Fixture": "2", "block Main": "4"}, {"block Fixture"})


if __name__ == "__main__":
    unittest.main()
