"""Offline checks for the independent FB export reading used by the release-change qualifier."""
import unittest

from qualify_library_release_changes import EXPECTED, compile_errors, fb_shape

NS = "http://www.siemens.com/automation/Openness/SW/Interface/v5"


def document(output, negated):
    body = '<Token Text=":=" /><Blank />' + ('<Token Text="NOT" /><Blank />' if negated else "")
    return f"""<Document><SW.Blocks.FB ID="0"><AttributeList><Interface><Sections xmlns="{NS}">
<Section Name="Input"><Member Name="Enable" Datatype="Bool" /></Section>
<Section Name="Output"><Member Name="{output}" Datatype="Bool" /></Section>
</Sections></Interface></AttributeList><ObjectList><SW.Blocks.CompileUnit ID="1"><AttributeList><NetworkSource>
<StructuredText xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4">{body}</StructuredText>
</NetworkSource></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FB></Document>""".encode()


class FbShapeTests(unittest.TestCase):
    def test_each_release_is_distinguished(self):
        self.assertEqual(fb_shape(document("Active", False)), EXPECTED["1.0.0"])
        self.assertEqual(fb_shape(document("Active", True)), EXPECTED["1.1.0"])
        self.assertEqual(fb_shape(document("Ready", True)), EXPECTED["2.0.0"])
        self.assertNotEqual(fb_shape(document("Ready", False)), EXPECTED["2.0.0"])

    def test_only_error_messages_count(self):
        result = {"targets": [{"messages": [{"state": "Warning", "path": "Main"}, {"state": "Error", "path": "Main"}]}]}
        self.assertEqual(compile_errors(result), [{"state": "Error", "path": "Main"}])


if __name__ == "__main__":
    unittest.main()
