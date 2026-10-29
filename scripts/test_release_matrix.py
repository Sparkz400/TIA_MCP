"""Keep the frozen release matrix in step with the catalog and its classification rules."""
import unittest

from release_matrix import MATRIX, ROOT, build, owners, render, support


def feature(implementation="implemented", qualification="verified", availability="conditional"):
    return {"implementation": {"status": implementation}, "qualification": {"status": qualification},
            "availability": {"status": availability}}


class ReleaseMatrixTests(unittest.TestCase):
    def test_committed_matrix_is_current(self):
        self.assertEqual((ROOT / MATRIX).read_text(encoding="utf-8"), render(build()),
                         "Run python -B scripts/release_matrix.py after changing the catalog, ledger or evidence.")

    def test_support_classes(self):
        self.assertEqual(support(feature()), "shipped")
        self.assertEqual(support(feature("partial")), "limited")
        self.assertEqual(support(feature(qualification="unverified")), "unqualified")
        self.assertEqual(support(feature(qualification="failed")), "unqualified")
        self.assertEqual(support(feature(availability="blocked")), "unavailable_target")
        self.assertEqual(support(feature("not_implemented", "unverified")), "deferred")

    def test_owner_comes_from_ledger_then_longest_prefix(self):
        self.assertEqual(owners("block_read", {"block_read": ["X11"]}), ["X11"])
        self.assertEqual(owners("qualification_watch_compete", {}), ["D02"])
        with self.assertRaises(ValueError):
            owners("unowned_feature", {})

    def test_unavailable_target_cells_are_blocked_not_open(self):
        matrix = build()
        blocked = sum(len(row["cells"]) for row in matrix["rows"] if row["support"] == "unavailable_target")
        self.assertEqual(matrix["summary"]["cells"].get("blocked", 0), blocked)
        opened = sum(len(row["cells"]) for row in matrix["rows"] if row["support"] == "unqualified")
        self.assertEqual(matrix["summary"]["cells"]["open"], opened)

    def test_only_verified_features_bind_cells(self):
        for row in build()["rows"]:
            bound = row["support"] in ("shipped", "limited")
            for cell in row["cells"]:
                self.assertEqual(cell["status"] == "bound", bound, row["featureId"])
                self.assertEqual(bool(cell["evidenceIds"]), bound, row["featureId"])


if __name__ == "__main__":
    unittest.main()
