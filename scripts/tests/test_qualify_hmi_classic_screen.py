"""File-only checks for the native screen runner; no Siemens process is started."""
import tempfile
import unittest
from pathlib import Path

from qualify_hmi_classic_screen import digest, payload, read_artifact, same_documents


class ScreenRunnerTests(unittest.TestCase):
    def test_single_and_batch_request_shapes_and_retained_hash(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'screen.xml'
            xml = '<Document><Hmi.Screen.Screen ID="0"><AttributeList><Name>A</Name></AttributeList></Hmi.Screen.Screen></Document>'
            path.write_text(xml, encoding='utf-8')
            reference = dict(path=str(path), sha256=digest(path.read_bytes()), bytes=path.stat().st_size)
            capture = dict(identity=dict(projectCopyId='owned', sessionGeneration='current'), contentHash='scope', screens={'A': reference, 'B': reference})
            single = payload(capture, {'A': dict(xml=xml)})
            self.assertNotIn('additionalScreens', single)
            self.assertEqual('current', single['target']['sessionGeneration'])
            batch = payload(capture, {'A': dict(xmlArtifact=reference), 'B': dict(xml=xml)})
            self.assertEqual('B', batch['additionalScreens'][0]['target']['objectPath'])
            self.assertEqual(reference, batch['xmlArtifact'])
            with self.assertRaises(AssertionError):
                read_artifact({**reference, 'bytes': reference['bytes'] + 1})
            path.write_text(xml + ' ', encoding='utf-8')
            with self.assertRaises(AssertionError):
                payload(capture, {'A': dict(xml=xml)})

    def test_independent_comparison_rejects_lost_screen_or_changed_value(self):
        xml = '<Document><Hmi.Screen.Screen ID="0"><AttributeList><Name>A</Name></AttributeList></Hmi.Screen.Screen></Document>'
        with self.assertRaises(AssertionError):
            same_documents({'A': xml}, {})
        with self.assertRaises(AssertionError):
            same_documents({'A': xml}, {'A': xml.replace('<Name>A', '<Name>B')})


if __name__ == '__main__':
    unittest.main()
