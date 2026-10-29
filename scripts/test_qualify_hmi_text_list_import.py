import unicodedata
import unittest
from pathlib import Path

from qualify_hmi_text_list_import import boundary_requests, compile_messages, evaluate_import, orphan_texts, problems, resolve, stripped, unrelated

FIXTURE = Path(__file__).resolve().parents[1] / 'fixtures/hmi/unified-text-list'
# Comparison data: one valid list and one list whose text reference is missing.
UNRESOLVED_PROBE = (
    '#Version: 2.0\nTextListContainers:\n  TextList:\n    ResourceListType: TextList\n    ResourceLists:\n'
    '      "ProbeValid":\n        RangeType: Decimal\n        Entries:\n          "Zero":\n            Type: SingleValue\n'
    '            Value: 0\n            FromValue: 0\n            ToValue: 0\n            IsDefaultEntry: false\n            Text: "ProbeTexts.Zero"\n'
    '      "ProbeBroken":\n        RangeType: Decimal\n        Entries:\n          "Zero":\n            Type: SingleValue\n'
    '            Value: 0\n            FromValue: 0\n            ToValue: 0\n            IsDefaultEntry: false\n            Text: "ProbeTexts.Missing"\n',
    '#Version: 2.0\nTextLibraries:\n  "ProbeTexts":\n    Type: Text\n    Languages:\n      - "en-US"\n      - "de-DE"\n'
    '    DefaultLanguage: "en-US"\n    Entries:\n      "Zero":\n        Text:\n          - "Zero"\n          - "Null"\n')
# Shape of a V21 export (run q/tl1): renamed entries, one pooled library, defaults omitted, CRLF, unquoted Off/Null texts.
NATIVE = ('#Version: 2.0\r\n\r\nTextListContainers:\r\n  DeviceTextList:\r\n    ResourceListType: TextList\r\n    ResourceLists:\r\n'
          '      QualificationStates:\r\n        Entries:\r\n          Text_list_entry_0:\r\n            IsDefaultEntry: True\r\n'
          '            Text: MyTextLibrary.Text_0\r\n          Text_list_entry_1:\r\n            Value: 1\r\n            FromValue: 1\r\n'
          '            ToValue: 1\r\n            Text: MyTextLibrary.Text_1\r\n',
          '#Version: 2.0\r\n\r\nTextLibraries:\r\n  MyTextLibrary:\r\n    Type: Text\r\n    Languages:\r\n    - en-US\r\n    - de-DE\r\n'
          '    DefaultLanguage: en-US\r\n    Entries:\r\n      Text_0:\r\n        Text:\r\n        - Qualification off\r\n        - Qualifikation aus\r\n'
          '      Text_1:\r\n        Text:\r\n        - |-\r\n          Qualification ready\r\n          Off\r\n        - Null\r\n')


class TextListImportOracleTests(unittest.TestCase):
    def setUp(self):
        self.peer = resolve((FIXTURE / 'QualificationTextLists.hmi.yml').read_bytes(), (FIXTURE / 'QualificationTextLists.TextLibrary.hmi.yml').read_bytes())
        self.probe = resolve(UNRESOLVED_PROBE[0].encode(), UNRESOLVED_PROBE[1].encode())

    def test_native_export_shape_resolves_like_the_candidate(self):
        native = resolve(NATIVE[0].encode(), NATIVE[1].encode())
        ready = native['QualificationStates']['Entries'][1]
        self.assertEqual(ready['Text'], {'en-US': 'Qualification ready\nOff', 'de-DE': 'Null'})
        ready['Text'] = self.peer['QualificationStates']['Entries'][1]['Text']
        self.assertEqual(native, self.peer)
        self.assertIs(native['QualificationStates']['Entries'][0]['IsDefaultEntry'], True)

    def test_tab_inside_a_plain_native_text_is_text(self):
        native = resolve(NATIVE[0].encode(), NATIVE[1].replace('- Null', '- a\tb').encode())
        self.assertEqual(native['QualificationStates']['Entries'][1]['Text']['de-DE'], 'a\tb')

    def test_unresolved_reference_stays_visible(self):
        self.assertEqual(self.probe['ProbeBroken']['Entries'][0]['Text']['unresolved'], 'ProbeTexts.Missing')

    def test_exact_import_keeps_peers(self):
        result = evaluate_import(self.peer, {**self.peer, **self.probe}, self.probe, True)
        self.assertEqual((result['outcome'], result['lists']), ('exact', {'ProbeValid': 'exact', 'ProbeBroken': 'exact'}))

    def test_changed_peer_value_type_extra_field_or_unexpected_list_is_inexact(self):
        for change in ('peer', 'bool', 'field', 'extra', 'absent'):
            after = {**self.peer, **self.probe}
            entry = dict(after['ProbeValid']['Entries'][0])
            if change == 'peer': after['QualificationStates'] = {**after['QualificationStates'], 'RangeType': 'Bool'}
            elif change == 'bool': entry['Value'] = False
            elif change == 'field': entry['Comment'] = ''
            elif change == 'extra': after['Unexpected'] = {}
            else: del after['ProbeBroken']
            if change in ('bool', 'field'): after['ProbeValid'] = {**after['ProbeValid'], 'Entries': [entry]}
            self.assertEqual(evaluate_import(self.peer, after, self.probe, True)['outcome'], 'inexact', change)

    def test_refusal_must_leave_lists_unchanged(self):
        self.assertEqual(evaluate_import(self.peer, self.peer, self.probe, False)['outcome'], 'refusedUnchanged')
        self.assertEqual(evaluate_import(self.peer, {**self.peer, 'ProbeValid': self.probe['ProbeValid']}, self.probe, False)['outcome'], 'refusedChanged')

    def test_orphans_and_unrelated_walk(self):
        library = UNRESOLVED_PROBE[1].replace('"Zero":', '"Unused":\n        Text:\n          - "u"\n          - "u"\n      "Zero":')
        self.assertEqual(orphan_texts(library.encode(), UNRESOLVED_PROBE[0].encode()), ['ProbeTexts.Unused'])
        walk = {'attributes': {'Name': 'HMI'}, 'sections': {'HmiTextLists': [1], 'Screens': [{'export': {'path': 'a', 'sha256': 'x'}}]}}
        other = {'attributes': {'Name': 'HMI'}, 'sections': {'HmiTextLists': [2], 'Screens': [{'export': {'path': 'b', 'sha256': 'x'}}]}}
        self.assertEqual(unrelated(walk), unrelated(other))
        self.assertNotEqual(stripped(walk), stripped(other))

    def test_compile_comparison_ignores_only_incremental_information(self):
        row = {'name': 'HMI', 'messagesComplete': True, 'messages': [{'state': 'Error', 'description': 'd', 'path': 'p', 'children': [
            {'state': 'Information', 'description': 'Number of used tags: 1', 'path': None, 'children': []}]}]}
        self.assertEqual(problems(compile_messages([row])), {'HMI': [('Error', 'd', 'p')]})
        self.assertIsNone(compile_messages([{**row, 'messagesComplete': False}]))
        self.assertIsNone(compile_messages([]))

    def test_boundary_requests_stay_inside_what_the_tool_accepts(self):
        requests = boundary_requests()
        self.assertEqual(sorted(requests), ['boundary-names', 'boundary-texts', 'boundary-values'])
        names = [row['name'] for request in requests.values() for row in request['lists']]
        self.assertEqual(len({name.lower() for name in names}), len(names))
        for request in requests.values():
            self.assertLessEqual(len(request['lists']), 128)
            for row in request['lists']:
                name = row['name']
                self.assertTrue(len(name) <= 128 and name.strip() == name and all(c.isalnum() or c in " !&'()+,-;<=>@\\^_{|}" for c in name), name)
                for entry in row['entries']:
                    self.assertTrue(all(v is None or -2**31 <= v < 2**31 for v in (entry['fromValue'], entry['toValue'])))
                    if entry['isDefault']:
                        self.assertTrue(row['rangeType'] != 'Bool' and entry['type'] == 'SingleValue' and entry['fromValue'] == 0, entry)
            for text in request['texts']:
                for value in text['translations'].values():
                    self.assertFalse(any(unicodedata.category(c) == 'Cc' and c not in '\t\n' for c in value))
                    self.assertLessEqual(len(value), 16384)


if __name__ == '__main__':
    unittest.main()
