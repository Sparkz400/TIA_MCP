import unittest
from pathlib import Path

from qualify_hmi_text_list_import import UNRESOLVED_PROBE, compile_messages, evaluate_import, orphan_texts, resolve, stripped, unrelated

ROOT = Path(__file__).resolve().parents[1]
FIXTURE = ROOT / 'fixtures/hmi/unified-text-list'


class TextListImportOracleTests(unittest.TestCase):
    def setUp(self):
        self.peer = resolve((FIXTURE / 'QualificationTextLists.hmi.yml').read_bytes(), (FIXTURE / 'QualificationTextLists.TextLibrary.hmi.yml').read_bytes())
        self.probe = resolve(UNRESOLVED_PROBE[0].encode(), UNRESOLVED_PROBE[1].encode())

    def test_resolve_replaces_references_by_language_texts(self):
        entry = self.peer['QualificationStates']['Entries']['StateOff']
        self.assertEqual(entry['Text'], {'en-US': 'Qualification off', 'de-DE': 'Qualifikation aus'})
        self.assertIs(entry['IsDefaultEntry'], True)
        self.assertEqual(self.probe['ProbeBroken']['Entries']['Zero']['Text']['unresolved'], 'ProbeTexts.Missing')

    def test_exact_import_keeps_peers_and_reports_native_extras(self):
        after = {**self.peer, **self.probe}
        after['ProbeValid'] = {**after['ProbeValid'], 'Comment': ''}
        result = evaluate_import(self.peer, after, self.probe, True)
        self.assertEqual(result['outcome'], 'exact')
        self.assertEqual(result['extras'], ['ProbeValid/Comment'])

    def test_changed_peer_value_type_or_unexpected_list_is_inexact(self):
        for change in ('peer', 'bool', 'extra'):
            after = {**self.peer, **self.probe}
            if change == 'peer': after['QualificationStates'] = {**after['QualificationStates'], 'RangeType': 'Bool'}
            elif change == 'bool':
                entries = dict(after['ProbeValid']['Entries']); entries['Zero'] = {**entries['Zero'], 'Value': False}
                after['ProbeValid'] = {**after['ProbeValid'], 'Entries': entries}
            else: after['Unexpected'] = {}
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

    def test_incomplete_compile_capture_is_not_comparable(self):
        row = {'name': 'HMI', 'messagesComplete': True, 'messages': [{'state': 'Error', 'description': 'd', 'path': 'p', 'children': []}]}
        self.assertEqual(compile_messages([row]), {'HMI': [('Error', 'd', 'p')]})
        self.assertIsNone(compile_messages([{**row, 'messagesComplete': False}]))
        self.assertIsNone(compile_messages([]))


if __name__ == '__main__':
    unittest.main()
