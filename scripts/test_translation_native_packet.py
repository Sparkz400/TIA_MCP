"""File-only adversarial checks for native packet interpretation; no TIA calls."""
import unittest
from pathlib import Path

import qualify_translation_import as coordinator
import verify_translation_import as verifier


class PacketChecks(unittest.TestCase):
    def xml(self, created='one', culture='en-GB', text='Before', code='logic'):
        en = text if culture == 'en-GB' else 'Before'
        de = text if culture == 'de-DE' else 'Quelle'
        return f'<Document><DocumentInfo><Created>{created}</Created></DocumentInfo><Code>{code}</Code><SW.Blocks.OB><AttributeList><Name>Main</Name><Number>1</Number></AttributeList><ObjectList><MultilingualText CompositionName="Comment"><ObjectList><MultilingualTextItem><AttributeList><Culture>en-GB</Culture><Text>{en}</Text></AttributeList></MultilingualTextItem><MultilingualTextItem><AttributeList><Culture>de-DE</Culture><Text>{de}</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.OB></Document>'.encode()

    def normalize(self, data): return verifier.peer(data, 'tia21-simaticml-translation-v1', 'Before', 'After')

    def test_only_timestamp_and_approved_target_may_vary(self):
        self.assertEqual(self.normalize(self.xml()), self.normalize(self.xml(created='two', text='After')))

    def test_unrelated_code_change_is_detected(self):
        self.assertNotEqual(self.normalize(self.xml()), self.normalize(self.xml(code='other logic')))

    def test_other_language_change_is_detected(self):
        self.assertNotEqual(self.normalize(self.xml(culture='de-DE')), self.normalize(self.xml(culture='de-DE', text='After')))

    def test_unapproved_target_change_is_detected(self):
        self.assertNotEqual(self.normalize(self.xml()), self.normalize(self.xml(text='Different')))

    def test_same_text_in_another_composition_is_not_approved(self):
        before=self.xml().replace(b'</ObjectList></SW.Blocks.OB>',b'<MultilingualText CompositionName="Title"><ObjectList><MultilingualTextItem><AttributeList><Culture>en-GB</Culture><Text>Before</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.OB>')
        after=before.replace(b'<Text>Before</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.OB>', b'<Text>After</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.OB>')
        self.assertNotEqual(self.normalize(before),self.normalize(after))

    def test_unknown_profile_cannot_ignore_peers(self):
        with self.assertRaisesRegex(ValueError, 'Unknown peer'): verifier.peer(self.xml(), 'ignore', 'Before', 'After')

    def test_incomplete_packet_cannot_pass(self):
        import tempfile, json
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder); (path/'result.json').write_text(json.dumps({'completed':False}))
            with self.assertRaisesRegex(ValueError, 'Incomplete native'): verifier.verify(path)

    def test_category_mutation_is_refused_before_interpretation(self):
        with self.assertRaisesRegex(ValueError, 'modified'): coordinator.category_verdict({'projectModified': True})


if __name__ == '__main__': unittest.main()
