"""Offline checks for the authored dependency graph used by the native dependency qualifier."""
import unittest

from qualify_library_dependencies import CHILD, EXPECTED_EDGES, INNER, OUTER, PARENT, closure_edges


def row(name, guid, *required):
    return {"typeName": name, "versionGuid": guid, "requiredVersionGuids": list(required)}


class ClosureEdgeTests(unittest.TestCase):
    def test_nested_closure_matches_authored_graph(self):
        rows = [row(PARENT, "p", "c", "o"), row(CHILD, "c", "o"), row(OUTER, "o", "i"), row(INNER, "i")]
        self.assertEqual(closure_edges(rows), EXPECTED_EDGES["1.0.0"])

    def test_removed_child_is_a_different_graph(self):
        rows = [row(PARENT, "p", "o"), row(OUTER, "o", "i"), row(INNER, "i")]
        self.assertEqual(closure_edges(rows), EXPECTED_EDGES["1.1.0"])
        self.assertNotEqual(closure_edges(rows), EXPECTED_EDGES["1.0.0"])

    def test_reference_outside_closure_is_rejected(self):
        with self.assertRaises(AssertionError):
            closure_edges([row(PARENT, "p", "missing")])


if __name__ == "__main__":
    unittest.main()
