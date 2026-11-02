"""Keep the frozen release matrix in step with the catalog and its classification rules."""
import unittest

import json
import copy
import hashlib
import pathlib
import subprocess
import tempfile

from release_matrix import LEDGER, MATRIX, ROOT, build, disposition_errors, owners, release_gate_errors, render, support


def feature(implementation="implemented", qualification="verified", availability="conditional", api="available"):
    return {"implementation": {"status": implementation}, "qualification": {"status": qualification},
            "availability": {"status": availability}, "api": {"status": api}}


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
        self.assertEqual(support(feature("not_implemented", "unverified", "unsupported")), "deferred")
        self.assertEqual(support(feature("not_implemented", "unverified", "unsupported", "unavailable")), "unavailable_target")

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

    def test_blocked_rows_match_ledger_dispositions(self):
        dispositions = json.loads((ROOT / LEDGER).read_text(encoding="utf-8"))["dispositions"]
        self.assertEqual(disposition_errors(build()["rows"], dispositions), [])
        rows = [{"featureId": "a", "support": "unavailable_target"}, {"featureId": "b", "support": "shipped"}]
        self.assertEqual(disposition_errors(rows, [{"featureId": "a"}]), [])
        self.assertEqual(len(disposition_errors(rows, [])), 1)  # blocked without a disposition
        self.assertEqual(len(disposition_errors(rows, [{"featureId": "a"}, {"featureId": "b"}])), 1)  # disposition without a blocked row

    def test_release_gate_blocks_unfinished_1_0(self):
        matrix = {"summary": {"cells": {"open": 2, "deferred": 1}}}
        done = {"summary": {"cells": {"bound": 9}}}
        accepted = [{"id": "F01", "releaseObligation": False, "releaseAccepted": False}]
        self.assertEqual(release_gate_errors("0.9.0", {"releaseReady": False, "obligations": []}, matrix), [])
        self.assertEqual(len(release_gate_errors("1.0.0", {"releaseReady": False, "obligations": accepted}, matrix)), 3)
        self.assertEqual(release_gate_errors("1.0.0", {"releaseReady": True, "obligations": accepted}, done), [])
        # A set flag cannot bypass an unaccepted obligation (audit of 1 November).
        pending = accepted + [{"id": "F02", "releaseObligation": True, "releaseAccepted": False}]
        self.assertEqual(release_gate_errors("1.0.0", {"releaseReady": True, "obligations": pending}, done),
                         ["F02: release obligation not accepted"])

    def test_flags_cannot_replace_reviewed_acceptance(self):
        ledger = {"releaseReady": True, "obligations": [{"id": "F00", "releaseObligation": True, "releaseAccepted": True}]}
        self.assertEqual(release_gate_errors("1.0.0", ledger, {"summary": {"cells": {}}}),
                         ["F00: acceptance record missing"])

    def test_acceptance_binds_source_matrix_evidence_and_blocker_dispositions(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)

            def git(*args):
                return subprocess.check_output(["git", "-C", directory, *args], stderr=subprocess.DEVNULL).decode().strip()

            git("init", "-q")
            (root / "src").mkdir()
            (root / "src/probe.txt").write_text("frozen\n", encoding="utf-8")
            matrix = {"summary": {"cells": {"bound": 1, "blocked": 1}}, "rows": [
                {"featureId": "implemented", "owners": ["F00"], "support": "shipped", "evidenceIds": ["proof"]},
                {"featureId": "boundary", "owners": ["F00"], "support": "unavailable_target", "evidenceIds": []}],
                "evidence": [{"id": "proof", "retained": [{"sha256": "a" * 64}]}]}
            target = root / MATRIX
            target.parent.mkdir(parents=True)
            target.write_text(render(matrix), encoding="utf-8", newline="\n")
            git("-c", "core.autocrlf=false", "add", ".")
            git("-c", "user.name=Gate test", "-c", "user.email=gate@example.invalid", "commit", "-qm", "candidate")
            record = {"date": "2026-11-02", "acceptedBy": "maintainer", "commit": git("rev-parse", "HEAD"),
                      "matrixSha256": hashlib.sha256(target.read_bytes()).hexdigest(), "evidenceIds": ["proof"],
                      "dispositions": ["boundary"], "blockers": {"B01": "resolved"}, "limitations": []}
            ledger = {"releaseReady": True, "dispositions": [{"featureId": "boundary"}], "obligations": [
                {"id": "F00", "releaseObligation": True, "releaseAccepted": True, "blockers": ["B01"], "acceptance": record}]}
            self.assertEqual(release_gate_errors("1.0.0", ledger, matrix, root), [])
            for key, value in [("date", "not-a-date"), ("acceptedBy", "agent"), ("commit", "0" * 40),
                               ("matrixSha256", "b" * 64), ("evidenceIds", []), ("evidenceIds", ["invented"]),
                               ("dispositions", []), ("blockers", {}), ("limitations", "none")]:
                with self.subTest(key=key, value=value):
                    invalid = copy.deepcopy(ledger)
                    invalid["obligations"][0]["acceptance"][key] = value
                    self.assertTrue(release_gate_errors("1.0.0", invalid, matrix, root))
            changed = copy.deepcopy(matrix)
            changed["rows"][0]["support"] = "limited"
            self.assertIn("owned matrix rows changed", release_gate_errors("1.0.0", ledger, changed, root)[0])
            changed = copy.deepcopy(matrix)
            changed["evidence"][0]["retained"][0]["sha256"] = "c" * 64
            self.assertIn("accepted evidence changed", release_gate_errors("1.0.0", ledger, changed, root)[0])
            (root / "src/probe.txt").write_text("changed\n", encoding="utf-8")
            self.assertIn("release inputs changed", release_gate_errors("1.0.0", ledger, matrix, root)[0])

    def test_committed_ledger_fails_the_1_0_gate_per_obligation(self):
        ledger = json.loads((ROOT / LEDGER).read_text(encoding="utf-8"))
        errors = release_gate_errors("1.0.0", dict(ledger, releaseReady=True), {"summary": {"cells": {}}})
        self.assertEqual(len(errors), sum(1 for o in ledger["obligations"] if o["releaseObligation"] and not o["releaseAccepted"]))

    def test_only_verified_features_bind_cells(self):
        for row in build()["rows"]:
            bound = row["support"] in ("shipped", "limited")
            for cell in row["cells"]:
                self.assertEqual(cell["status"] == "bound", bound, row["featureId"])
                self.assertEqual(bool(cell["evidenceIds"]), bound, row["featureId"])


if __name__ == "__main__":
    unittest.main()
