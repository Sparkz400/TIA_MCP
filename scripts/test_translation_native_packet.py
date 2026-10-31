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

    def restricted(self):
        """The 28 October PLC: five block comments and one hardware comment; each native category export returned all six."""
        labels = ['<BlockCommentCategoryData>'] * 5 + ['<HwcnCommentTextCategoryData>']
        whole = {'rows': [{'nativeKey': f'K-{i}', 'category': c, 'source': {'state': 'text', 'text': f'S{i}'}, 'target': {'state': 'text', 'text': f'T{i}'}}
                          for i, c in enumerate(labels)]}
        captures = {}
        for category in coordinator.RESTRICTED:
            kept = [r for r in whole['rows'] if r['category'] == '<' + category + '>']
            captures[category] = {'rows': kept, 'scope': {'categoryIds': [category], 'objectSelections': ['plc:/PLC_1/plc/PLC_1']},
                                  'excludedRows': {r['nativeKey']: {'category': r['category']} for r in whole['rows'] if r not in kept}}
        return whole, captures

    def test_over_returned_rows_are_filtered_to_five_one_and_zero(self):
        result = coordinator.restricted_verdict(*self.restricted())
        self.assertEqual([(c['keptRows'], c['excludedRows'], c['nativeRows']) for c in result['captures']], [(5, 1, 6), (1, 5, 6), (0, 6, 6)])

    def test_leaked_row_in_a_filtered_capture_is_refused(self):
        whole, captures = self.restricted()
        captures['HwcnCommentTextCategoryData']['rows'].append(whole['rows'][0])
        with self.assertRaisesRegex(ValueError, 'kept 2 rows'): coordinator.restricted_verdict(whole, captures)

    def test_changed_text_in_a_filtered_capture_is_refused(self):
        whole, captures = self.restricted()
        captures['BlockCommentCategoryData']['rows'][0] = dict(whole['rows'][0], target={'state': 'text', 'text': 'drift'})
        with self.assertRaisesRegex(ValueError, 'BlockCommentCategoryData: kept'): coordinator.restricted_verdict(whole, captures)

    def test_requested_category_in_the_excluded_rows_is_refused(self):
        whole, captures = self.restricted()
        captures['AlarmClassDisplayNameCategory']['excludedRows']['K-9'] = {'category': '<AlarmClassDisplayNameCategory>'}
        with self.assertRaisesRegex(ValueError, 'excluded rows'): coordinator.restricted_verdict(whole, captures)

    def test_whole_project_scope_cannot_pass_as_restricted(self):
        whole, captures = self.restricted()
        captures['BlockCommentCategoryData']['scope']['objectSelections'] = ['whole-project:legacy-bilingual']
        with self.assertRaisesRegex(ValueError, 'restricted scope'): coordinator.restricted_verdict(whole, captures)

    def test_fixture_drift_from_the_expected_counts_is_refused(self):
        whole, captures = self.restricted()
        whole['rows'].append({'nativeKey': 'K-6', 'category': '<AlarmClassDisplayNameCategory>', 'source': {}, 'target': {}})
        with self.assertRaisesRegex(ValueError, 'AlarmClassDisplayNameCategory: kept 0 rows; the whole-project capture has 1'): coordinator.restricted_verdict(whole, captures)


if __name__ == '__main__': unittest.main()
