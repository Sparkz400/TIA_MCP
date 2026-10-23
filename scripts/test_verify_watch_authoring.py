"""Adversarial file-only WT-05 acceptance checks; no native process starts."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

import verify_watch_authoring as subject
import verify_watch_expansion as expansion
import verify_watch_workflow as base

FIXTURE = Path(__file__).resolve().parents[1] / 'fixtures/watch-tables/native-owned-v21/MilestoneWatch.before.xml'
REASON = 'Captured Output state of the selected FB instance: Active'


def candidate():
    tree = expansion.document(FIXTURE.read_bytes())
    base.table(tree).find('AttributeList/Name').text = subject.TABLE
    row = expansion.row_elements(tree)[0]
    row.find('AttributeList/ModifyValue').text = ''
    items = row.find('ObjectList/MultilingualText/ObjectList')
    for item in list(items):
        if item.findtext('AttributeList/Culture') != 'en-GB':
            items.remove(item)
        else:
            item.set('ID', '3')
            item.find('AttributeList/Text').text = REASON
    return tree


def raw(tree):
    return ET.tostring(tree, encoding='utf-8', xml_declaration=True)


class ExactDelta(unittest.TestCase):
    def verify(self, tree):
        return subject.exact_creation(FIXTURE.read_bytes(), raw(tree), ['en-GB'], REASON)

    def test_exact_default_bool_creation(self):
        self.assertEqual(self.verify(candidate())['requiredProfiles'], subject.PROFILES)

    def test_unapproved_row_fields_refused(self):
        for field, value in [('Name', '"Other".Active'), ('DisplayFormat', 'DEC_signed'),
                             ('ModifyValue', 'TRUE'), ('MonitorTrigger', 'Once')]:
            with self.subTest(field=field):
                tree = candidate()
                expansion.row_elements(tree)[0].find('AttributeList/' + field).text = value
                with self.assertRaises(base.VerificationError):
                    self.verify(tree)

    def test_extra_row_refused(self):
        tree = candidate()
        clone = copy.deepcopy(expansion.row_elements(tree)[0])
        for index, node in enumerate(n for n in clone.iter() if 'ID' in n.attrib):
            node.set('ID', format(index + 10, 'X'))
        base.table(tree).find('ObjectList').append(clone)
        with self.assertRaises(base.VerificationError):
            self.verify(tree)

    def test_native_ids_remain_significant(self):
        tree = candidate()
        expansion.row_elements(tree)[0].set('ID', 'A')
        with self.assertRaises(base.VerificationError):
            self.verify(tree)

    def test_comment_whitespace_is_significant(self):
        tree = candidate()
        tree.find('.//MultilingualTextItem/AttributeList/Text').text = REASON + ' '
        with self.assertRaises(base.VerificationError):
            self.verify(tree)

    def test_unknown_payload_and_prolog_refused(self):
        tree = candidate()
        ET.SubElement(expansion.row_elements(tree)[0].find('AttributeList'), 'Opaque').text = 'kept?'
        with self.assertRaises(base.VerificationError):
            self.verify(tree)
        with self.assertRaises(base.VerificationError):
            subject.exact_creation(FIXTURE.read_bytes(), raw(candidate()) + b'<!--extra-->', ['en-GB'], REASON)

    def test_template_bytes_are_pinned(self):
        with self.assertRaisesRegex(base.VerificationError, 'template hash'):
            subject.exact_creation(FIXTURE.read_bytes() + b' ', raw(candidate()), ['en-GB'], REASON)


class Absence(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        path = self.root/'absent.txt'
        path.write_bytes(('watch-table-absent.v1\nroot\n' + subject.TABLE).encode())
        self.snap = {'target': {'kind': 'watch', 'exists': False, 'group': 'root', 'name': subject.TABLE,
            'rows': [], 'isConsistent': True, 'artifact': base.ref(path), 'semanticSha256': base.sha(path.read_bytes())}, 'tables': []}

    def test_absence_accepted(self):
        subject.absence(self.root, self.snap)

    def test_case_insensitive_force_collision_refused(self):
        self.snap['tables'] = [{'name': subject.TABLE.lower(), 'kind': 'force'}]
        with self.assertRaisesRegex(base.VerificationError, 'collides'):
            subject.absence(self.root, self.snap)

    def test_wrong_destination_or_fabricated_empty_target_refused(self):
        for key, value in [('name', 'Other'), ('exists', True), ('rows', [{}])]:
            with self.subTest(key=key):
                changed = copy.deepcopy(self.snap)
                changed['target'][key] = value
                with self.assertRaises(base.VerificationError):
                    subject.absence(self.root, changed)

    def test_missing_evidence_is_failed_not_qualified(self):
        result = subject.verify(self.root)
        self.assertFalse(result['qualificationPassed'])
        self.assertFalse(result['productionActivation'])


class NativeSnapshot(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.before = self.make_snapshot('before', False, 10, 'first')
        self.after = self.make_snapshot('after', True, 11, 'second')

    def make_snapshot(self, directory, exists, pid, epoch):
        folder = self.root/directory
        folder.mkdir()
        path = folder/('target.xml' if exists else 'absent.txt')
        path.write_bytes(raw(candidate()) if exists else ('watch-table-absent.v1\nroot\n' + subject.TABLE).encode())
        target = {'kind': 'watch', 'exists': exists, 'group': 'root', 'name': subject.TABLE,
            'isConsistent': True, 'artifact': base.ref(path), 'semanticSha256': base.sha(path.read_bytes()),
            'rows': [{'kind': 'PlcWatchTableEntry', 'name': base.SYMBOL, 'displayFormat': 'Bool',
                     'modifyValue': '', 'modifyIntention': False}] if exists else []}
        snap = {'target': target, 'tables': [target] if exists else [], 'project': {'path': 'original.ap21'},
                'projectModified': False, 'processId': pid, 'peerSha256': 'peers', 'peers': {'languages': ['en-GB']},
                'observation': {'offline': True, 'projectFilePath': 'original.ap21', 'sessionEpoch': epoch,
                    'adapterSha256': 'adapter', 'profileSha256': 'profile', 'hardwareIdentity': 'cpu', 'nativeIdentity': None,
                    'reference': {'contentSha256': target['semanticSha256']}}}
        self.receipt(snap, folder/'snapshot.json')
        return snap

    def receipt(self, snap, path=None):
        path = path or Path(snap['receipt']['path'])
        path.write_text(json.dumps({k: v for k, v in snap.items() if k != 'receipt'}), encoding='utf-8')
        snap['receipt'] = base.ref(path)

    def verify(self):
        subject.snapshot(self.root, self.after, self.before, raw(candidate()), 'original.ap21', True)

    def test_saved_fresh_created_snapshot(self):
        self.verify()

    def test_same_process_or_epoch_is_not_fresh(self):
        for key in ('processId', 'sessionEpoch'):
            with self.subTest(key=key):
                original = copy.deepcopy(self.after)
                if key == 'processId': self.after[key] = self.before[key]
                else: self.after['observation'][key] = self.before['observation'][key]
                self.receipt(self.after)
                with self.assertRaisesRegex(base.VerificationError, 'Fresh native'):
                    self.verify()
                self.after = original
                self.receipt(self.after)

    def test_changed_native_profile_refused(self):
        self.after['observation']['profileSha256'] = 'another-version'
        self.receipt(self.after)
        with self.assertRaisesRegex(base.VerificationError, 'Bound target profile'):
            self.verify()

    def test_typed_row_differing_from_xml_refused(self):
        self.after['target']['rows'][0]['modifyValue'] = 'TRUE'
        self.receipt(self.after)
        with self.assertRaisesRegex(base.VerificationError, 'Typed native row field'):
            self.verify()

    def test_peer_manifest_change_refused(self):
        self.after['peers']['languages'].append('de-DE')
        self.receipt(self.after)
        with self.assertRaisesRegex(base.VerificationError, 'peer manifest'):
            self.verify()

    def test_missing_native_receipt_refused(self):
        Path(self.after['receipt']['path']).unlink()
        with self.assertRaises(base.VerificationError):
            self.verify()


class RetainedNativeCreation(unittest.TestCase):
    directory = FIXTURE.parent.parent / 'native-authoring-v21'

    def test_native_fixture_hashes_and_scoped_provenance(self):
        manifest = base.load(self.directory/'manifest.json')
        self.assertTrue(manifest['lifecyclePassed'])
        self.assertFalse(manifest['productionApplyEnabled'])
        self.assertEqual(manifest['mutationProfileKeys'], subject.PROFILES)
        for entry in manifest['files']:
            data = (self.directory/entry['path']).read_bytes()
            self.assertEqual(base.sha(data), entry['sha256'])
            self.assertEqual(len(data), entry['bytes'])

    def test_native_created_row_survives_saved_and_original_reopens(self):
        documents = [(self.directory/('creation.' + phase + '.xml')).read_bytes()
                     for phase in ('applied', 'reopened', 'original')]
        self.assertTrue(all(expansion.native_key(d) == expansion.native_key(documents[0]) for d in documents))
        for data in documents:
            rows = expansion.row_elements(expansion.document(data))
            self.assertEqual(len(rows), 1)
            self.assertEqual(rows[0].findtext('AttributeList/Name'), base.SYMBOL)
            self.assertEqual(rows[0].findtext('AttributeList/DisplayFormat'), 'Bool')
            self.assertIn(b'<ModifyValue />', data)

    def test_recovered_absence_matches_exact_before_receipt(self):
        expected = ('watch-table-absent.v1\nroot\n' + subject.TABLE).encode()
        for phase in ('before', 'restored'):
            self.assertEqual((self.directory/('absence.' + phase + '.txt')).read_bytes(), expected)


if __name__ == '__main__':
    unittest.main()
