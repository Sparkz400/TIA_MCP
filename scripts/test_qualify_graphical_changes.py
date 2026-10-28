"""Offline checks of the fixture helpers in qualify_graphical_changes.py; nothing here starts TIA."""
import re
import unittest

import qualify_graphical_changes as q


class GraphicalFixtureTests(unittest.TestCase):
    def test_fixture_takes_the_culture_and_one_extra_input(self):
        for language in ("LAD", "FBD"):
            xml = q.fixture(language, "en-US")
            self.assertNotIn("de-DE", xml)
            self.assertEqual(1, xml.count('<Member Name="EnableAlternate" Datatype="Bool"'))
            self.assertLess(xml.index("EnableAlternate"), xml.index('<Section Name="Output">'))

    def test_copied_network_ids_are_fresh_and_keep_their_order(self):
        xml = q.fixture("LAD", "en-US")
        unit = q.network(xml)
        copy = q.copy_unit(xml, unit)
        existing = set(re.findall(r'\sID="([0-9A-F]+)"', xml))
        ids = [int(i, 16) for i in re.findall(r'\sID="([0-9A-F]+)"', copy)]
        self.assertFalse(existing & {"%X" % i for i in ids})
        self.assertEqual(sorted(ids), ids)
        self.assertEqual(re.sub(r'\sID="[0-9A-F]+"', "", copy), re.sub(r'\sID="[0-9A-F]+"', "", re.search(
            r'<SW\.Blocks\.CompileUnit ID="%s"[\s\S]*?</SW\.Blocks\.CompileUnit>' % unit, xml).group(0)))


if __name__ == "__main__":
    unittest.main()
