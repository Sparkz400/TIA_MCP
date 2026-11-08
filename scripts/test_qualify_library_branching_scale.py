"""The authored branching tree: callees are created first and every node but the root has exactly one caller."""
import collections
import unittest

from qualify_library_branching_scale import children, expected_edges, name, source


class TreeTests(unittest.TestCase):
    def test_complete_binary_tree(self):
        for nodes in (3, 7, 1023, 1000):
            edges = expected_edges(nodes)
            callers = collections.Counter(c for callees in edges.values() for c in callees)
            self.assertEqual(sum(callers.values()), nodes - 1)
            self.assertEqual(set(callers.values()), {1})
            self.assertNotIn(name(nodes - 1), callers)  # the root is the last created
            self.assertTrue(all(c < k for k in range(nodes) for c in children(k, nodes)))  # callees first

    def test_source_calls_its_children(self):
        text = source(6, 7)
        self.assertIn('"Tree_0005"(Value := #Value)', text)
        self.assertIn('"Tree_0004"(Value := #Value)', text)
        self.assertNotIn('"Tree_', source(0, 7).replace('"Tree_0000"', ''))


if __name__ == "__main__":
    unittest.main()
