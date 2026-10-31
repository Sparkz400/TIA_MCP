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
        """The 31 October PLC: five block comments and one hardware comment; each native category export returned all six.
        The baseline is the category probe's independently decoded all-category PLC export."""
        labels = ['<BlockCommentCategoryData>'] * 5 + ['<HwcnCommentTextCategoryData>']
        baseline = {f'K-{i}': {'nativeKey': f'K-{i}', 'category': c, 'source': f'S{i}', 'target': '' if i == 5 else f'T{i}'} for i, c in enumerate(labels)}
        def row(r): return {'nativeKey': r['nativeKey'], 'category': r['category'], 'source': {'state': 'text', 'text': r['source']},
                            'target': {'state': 'empty', 'text': ''} if not r['target'] else {'state': 'text', 'text': r['target']}}
        captures = {}
        for category in coordinator.RESTRICTED:
            kept = [row(r) for r in baseline.values() if r['category'] == '<' + category + '>']
            captures[category] = {'rows': kept, 'scope': {'categoryIds': [category], 'objectSelections': ['plc:/PLC_1/plc/PLC_1']},
                                  'excludedRows': {k: {'category': r['category']} for k, r in baseline.items() if r['category'] != '<' + category + '>'}}
        return baseline, captures

    def test_over_returned_rows_are_filtered_to_five_one_and_zero(self):
        result = coordinator.restricted_verdict(*self.restricted())
        self.assertEqual([(c['keptRows'], c['excludedRows'], c['nativeRows']) for c in result['captures']], [(5, 1, 6), (1, 5, 6), (0, 6, 6)])
        self.assertEqual(result['plcRows'], 6)

    def test_leaked_row_in_a_filtered_capture_is_refused(self):
        baseline, captures = self.restricted()
        captures['HwcnCommentTextCategoryData']['rows'].append(captures['BlockCommentCategoryData']['rows'][0])
        with self.assertRaisesRegex(ValueError, 'kept 2 rows'): coordinator.restricted_verdict(baseline, captures)

    def test_changed_text_in_a_filtered_capture_is_refused(self):
        baseline, captures = self.restricted()
        captures['BlockCommentCategoryData']['rows'][0] = dict(captures['BlockCommentCategoryData']['rows'][0], target={'state': 'text', 'text': 'drift'})
        with self.assertRaisesRegex(ValueError, 'BlockCommentCategoryData: kept'): coordinator.restricted_verdict(baseline, captures)

    def test_requested_category_in_the_excluded_rows_is_refused(self):
        baseline, captures = self.restricted()
        captures['AlarmClassDisplayNameCategory']['excludedRows']['K-9'] = {'category': '<AlarmClassDisplayNameCategory>'}
        with self.assertRaisesRegex(ValueError, 'excluded rows'): coordinator.restricted_verdict(baseline, captures)

    def test_dropped_plc_row_is_refused(self):
        baseline, captures = self.restricted()
        del captures['HwcnCommentTextCategoryData']['excludedRows']['K-0']
        with self.assertRaisesRegex(ValueError, 'excluded rows'): coordinator.restricted_verdict(baseline, captures)

    def test_whole_project_scope_cannot_pass_as_restricted(self):
        baseline, captures = self.restricted()
        captures['BlockCommentCategoryData']['scope']['objectSelections'] = ['whole-project:legacy-bilingual']
        with self.assertRaisesRegex(ValueError, 'restricted scope'): coordinator.restricted_verdict(baseline, captures)

    def test_fixture_drift_from_the_expected_counts_is_refused(self):
        baseline, captures = self.restricted()
        baseline['K-6'] = {'nativeKey': 'K-6', 'category': '<AlarmClassDisplayNameCategory>', 'source': 'Alarm', 'target': ''}
        for category in ('BlockCommentCategoryData', 'HwcnCommentTextCategoryData'):
            captures[category]['excludedRows']['K-6'] = {'category': '<AlarmClassDisplayNameCategory>'}
        with self.assertRaisesRegex(ValueError, 'AlarmClassDisplayNameCategory: kept 0 rows; the PLC export has 1'): coordinator.restricted_verdict(baseline, captures)


if __name__ == '__main__': unittest.main()
