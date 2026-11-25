"""Offline checks of qualify_watch_source_reconcile.py; nothing here starts TIA."""
import json
import tempfile
import unittest
from pathlib import Path

import qualify_watch_production_host as owned
import qualify_watch_source_reconcile as q


class SourceReconcileRunnerTests(unittest.TestCase):
    def test_point_is_a_host_point_for_this_runner_only(self):
        import verify_watch_expansion
        self.assertIn(q.POINT, owned.oracle.HOST_POINTS)
        self.assertNotIn(q.POINT, verify_watch_expansion.HOST_POINTS)

    def test_admissions_reads_the_latest_revision_of_source_negatives_only(self):
        with tempfile.TemporaryDirectory() as temp:
            run = q.Run.__new__(q.Run)
            run.root = Path(temp)
            for name, operation, states in (("a", "watch_authoring_source_negative", ["prepared", "dispatchIntent"]),
                                            ("b", "scratch_create", ["dispatchIntent", "returned"])):
                folder = run.root / "engineering-admissions" / name
                folder.mkdir(parents=True)
                for index, state in enumerate(states):
                    (folder / ("%04d.json" % index)).write_text(json.dumps({"id": name, "state": state, "scope": {"operation": operation}}))
            self.assertEqual([("a", "dispatchIntent")], [(r["id"], r["state"]) for r in run.admissions()])


if __name__ == "__main__":
    unittest.main()
