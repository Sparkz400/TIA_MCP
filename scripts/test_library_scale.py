"""Offline checks of the library scale runner's generated documents; no native evidence."""
import unittest

from qualify_library_scale import chain_source, flat_source


class ScaleSourceTests(unittest.TestCase):
    def test_flat_type_is_one_self_contained_fb(self):
        source = flat_source("Scale_00042")
        self.assertTrue(source.startswith('FUNCTION_BLOCK "Scale_00042"') and source.count("FUNCTION_BLOCK") == 2)
        self.assertNotIn('"Scale_', source.split("\n", 1)[1])

    def test_chain_calls_exactly_its_predecessor_and_the_root_calls_nothing(self):
        self.assertIn('"Chain_0006"(Value := #Value)', chain_source("Chain_0007", "Chain_0006"))
        root = chain_source("Chain_0000", None)
        self.assertNotIn('"Chain_', root.split("\n", 1)[1].replace('#"Chain_0000"', ""))


if __name__ == "__main__":
    unittest.main()
