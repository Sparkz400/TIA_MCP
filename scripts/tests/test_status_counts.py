"""The counts copied into docs/STATUS.md and docs/TODO.md match the catalog and the release matrix."""
import json
import unittest

import status_counts


def matrix(rows):
    return {"frozenFrom": {"catalogVersion": "1.2.3"},
            "summary": {"support": {"unqualified": 1, "unavailable_target": 1},
                        "cells": {"bound": 0, "open": 2, "blocked": 1}},
            "rows": rows}


CATALOG = {"catalogVersion": "1.2.3", "features": [
    {"qualification": {"status": "verified"}, "mcpTools": ["a", "b"]},
    {"qualification": {"status": "unverified"}, "mcpTools": ["b"]}]}
ROWS = [
    {"support": "unqualified", "owners": ["X07", "X08"], "cells": [{"status": "open"}, {"status": "open"}]},
    {"support": "unavailable_target", "owners": ["D07"], "cells": [{"status": "open"}]}]
TODO_TEXT = """Catalog `1.2.2` (the matrix's `frozenFrom.catalogVersion`, read today): 5 open cells in 3 `unqualified` rows; the matrix has 0 bound, 1 blocked and 0 descoped cells.

### Open matrix rows by owner

| Owner | Open cells | Rows | Blocker |
| --- | --- | --- | --- |
| X07 HMI | 4 | `x` | y |

### Next
"""


class StatusCountsTests(unittest.TestCase):
    def test_repository_documents_are_current(self):
        self.assertEqual(0, status_counts.main(["--check"]),
                         "Counts drifted; run python -B scripts/status_counts.py --write")

    def test_counts_follow_the_matrix_classification(self):
        values = status_counts.counts(CATALOG, matrix(ROWS))
        self.assertEqual((2, 1, 1, 2), (values["features"], values["verified"], values["unverified"], values["tools"]))
        # The blocked row's cell is not open, and a shared row counts once, under its first owner.
        self.assertEqual({"X07": 2}, values["openByFirstOwner"])
        self.assertEqual(1, values["openRows"])

    def test_write_replaces_only_the_numbers(self):
        values = status_counts.counts(CATALOG, matrix(ROWS))
        problems = []
        text = status_counts.apply(TODO_TEXT, status_counts.TODO, values, problems)
        self.assertIn("Catalog `1.2.3` (the matrix's `frozenFrom.catalogVersion`, read today): 2 open cells in 1 `unqualified` rows", text)
        self.assertIn("| X07 HMI | 2 | `x` | y |", text)
        self.assertEqual(4, len(problems), json.dumps(problems))
        again = []
        self.assertEqual(text, status_counts.apply(text, status_counts.TODO, values, again))
        self.assertEqual([], again)

    def test_a_missing_sentence_is_reported(self):
        problems = []
        status_counts.apply("nothing here", status_counts.STATUS, status_counts.counts(CATALOG, matrix(ROWS)), problems)
        self.assertEqual(4, len(problems))


if __name__ == "__main__":
    unittest.main()
