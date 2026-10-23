"""Independent count/address/type oracle for the native scale fixture."""
import unittest
import xml.etree.ElementTree as ET

from qualify_project_explorer_tags import generated_tag_table, validate_native_tags


class NativeScaleTagOracleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.xml = generated_tag_table("<Document><SW.Tags.PlcTagTable ID='0'><AttributeList><Name>Default</Name></AttributeList></SW.Tags.PlcTagTable></Document>")

    def test_all_two_thousand_distinct_native_declarations(self):
        rows = validate_native_tags(self.xml.encode(), "/exact/table")
        self.assertEqual(2000, len(rows))
        self.assertEqual({"objectPath": "/exact/table/tags/ExplorerScale_1999", "dataType": "Byte", "logicalAddress": "%MB1999"}, rows["ExplorerScale_1999"])

    def test_missing_native_tag_is_rejected(self):
        xml = ET.fromstring(self.xml)
        objects = xml.find("SW.Tags.PlcTagTable/ObjectList")
        objects.remove(objects[-1])
        with self.assertRaises(AssertionError):
            validate_native_tags(ET.tostring(xml), "/exact/table")

    def test_wrong_native_address_is_rejected(self):
        xml = ET.fromstring(self.xml)
        xml.find(".//SW.Tags.PlcTag/AttributeList/LogicalAddress").text = "%MB2000"
        with self.assertRaises(AssertionError):
            validate_native_tags(ET.tostring(xml), "/exact/table")

    def test_wrong_native_type_is_rejected(self):
        xml = ET.fromstring(self.xml)
        xml.find(".//SW.Tags.PlcTag/AttributeList/DataTypeName").text = "Word"
        with self.assertRaises(AssertionError):
            validate_native_tags(ET.tostring(xml), "/exact/table")

    def test_duplicate_native_name_is_rejected(self):
        xml = ET.fromstring(self.xml)
        tags = list(xml.iter("SW.Tags.PlcTag"))
        tags[-1].find("AttributeList/Name").text = "ExplorerScale_0000"
        with self.assertRaises(AssertionError):
            validate_native_tags(ET.tostring(xml), "/exact/table")

    def test_fixture_never_overwrites_an_existing_tag_table(self):
        with self.assertRaises(AssertionError):
            generated_tag_table(self.xml)


if __name__ == "__main__":
    unittest.main()
