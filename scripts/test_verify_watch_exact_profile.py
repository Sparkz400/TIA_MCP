"""Portable adversarial oracle checks. Synthetic peer XML is never native evidence."""
import copy
import json
from pathlib import Path
import tempfile
import unittest

import verify_watch_authoring as creation
import verify_watch_exact_profile as subject
import verify_watch_workflow as base


class SourceNegative(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.binding = {'projectPath': 'fixture.ap21', 'plcPath': '/PLC', 'blockPath': '/PLC/blocks/TiaMcp_EnableGate',
            'instanceDbPath': '/PLC/blocks/TiaMcp_EnableGate_DB', 'blockSha256': 'old-fb', 'instanceSha256': 'db',
            'profile': 'v21-direct-instance-declarations-1'}
        before, changed, restored = (self.snapshot(name, author) for name, author in
            (('before', 'Original'), ('changed', 'WT05_STALE_SOURCE'), ('restored', 'Original')))
        archive = self.root/'baseline.zap21'
        archive.write_bytes(b'synthetic-test-archive')
        self.result = {'before': before, 'changed': changed, 'restored': restored, 'sourceBinding': self.binding,
            'checkpoint': base.ref(archive), 'originalAuthor': 'Original', 'temporaryAuthor': 'WT05_STALE_SOURCE',
            'automaticReplay': False, 'nativeWatchImportInvoked': False, 'nativeSourceWriteAttempted': True, 'restorationPassed': True,
            'changedCapture': self.capture(changed), 'restoredCapture': self.capture(restored),
            'refusal': {'code': 'StaleSource', 'guard': 'VerifyWatchSource', 'checkpointOrImportAfterChange': False}}
        self.receipt()

    def snapshot(self, phase, author):
        folder = self.root/phase
        folder.mkdir()
        absent = folder/'absent.txt'
        absent.write_bytes(('watch-table-absent.v1\nroot\n' + creation.TABLE).encode())
        target = {'kind': 'watch', 'name': creation.TABLE, 'group': 'root', 'exists': False, 'rows': [], 'isConsistent': True,
                  'artifact': base.ref(absent), 'semanticSha256': base.sha(absent.read_bytes())}
        def xml(kind, name, author=''):
            return ('<Document><Engineering version="V21"/><DocumentInfo><Created>2026-10-16T00:00:00Z</Created></DocumentInfo>'
                '<' + kind + '><AttributeList><Name>' + name + '</Name><HeaderAuthor>' + author + '</HeaderAuthor></AttributeList></' + kind + '></Document>').encode()
        for index, (kind, name) in enumerate((('SW.Blocks.FB', 'TiaMcp_EnableGate'), ('SW.Blocks.InstanceDB', 'TiaMcp_EnableGate_DB'),
                                            ('SW.Blocks.FC', 'TiaMcp_IsEnabled'), ('SW.Blocks.OB', 'Main'))):
            (folder/f'block-{index}.xml').write_bytes(xml(kind, name, author if index == 0 else 'Original'))
        (folder/'tags-0.xml').write_bytes(xml('SW.Tags.PlcTagTable', 'Tags'))
        force = folder/'force-0.xml'
        force.write_bytes(xml('SW.WatchAndForceTables.PlcForceTable', 'Force table'))
        peers = {'block:/PLC/blocks/TiaMcp_EnableGate': 'old-fb' if author == 'Original' else 'changed-fb', 'other': 'preserved'}
        snap = {'target': target, 'tables': [{'kind': 'force', 'name': 'Force table', 'group': 'root', 'artifact': base.ref(force)}],
            'processId': 100, 'projectModified': False, 'project': {'path': 'fixture.ap21'}, 'peerSha256': str(peers), 'peers': peers,
            'observation': {'offline': True, 'projectFilePath': 'fixture.ap21', 'sessionEpoch': 'native-one',
                'adapterSha256': 'worker', 'profileSha256': 'api', 'hardwareIdentity': 'cpu', 'nativeIdentity': None,
                'reference': {'contentSha256': target['semanticSha256']}}}
        path = folder/'snapshot.json'
        path.write_text(json.dumps(snap))
        snap['receipt'] = base.ref(path)
        return snap

    def capture(self, snapshot):
        binding = copy.deepcopy(self.binding)
        binding['blockSha256'] = snapshot['peers']['block:/PLC/blocks/TiaMcp_EnableGate']
        folder = Path(snapshot['target']['artifact']['path']).parent
        return {'binding': binding, 'artifacts': [base.ref(folder/'block-0.xml'), base.ref(folder/'block-1.xml')]}

    def receipt(self):
        path = self.root/'negative.json'
        path.write_text(json.dumps({k: v for k, v in self.result.items() if k != 'receipt'}))
        self.result['receipt'] = base.ref(path)

    def verify(self):
        return subject.source_negative(self.root, self.result)

    def test_real_saved_delta_refusal_and_full_restore(self):
        self.assertTrue(self.verify()['realSavedSourceDrift'])

    def test_native_empty_header_author_is_valid_baseline(self):
        for phase in ('before', 'restored'):
            path = self.root/phase/'block-0.xml'
            path.write_bytes(path.read_bytes().replace(b'<HeaderAuthor>Original</HeaderAuthor>', b'<HeaderAuthor />'))
        self.result['originalAuthor'] = ''
        self.result['restoredCapture'] = self.capture(self.result['restored'])
        self.receipt()
        self.assertTrue(self.verify()['realSavedSourceDrift'])

    def test_missing_refusal_failure_or_import_cannot_qualify(self):
        mutations = [lambda r: r['refusal'].update(code='Other'), lambda r: r.update(failure='native failed'),
                     lambda r: r.update(nativeWatchImportInvoked=True), lambda r: r.update(restorationPassed=False)]
        original = copy.deepcopy(self.result)
        for mutate in mutations:
            self.result = copy.deepcopy(original)
            mutate(self.result)
            self.receipt()
            with self.assertRaises(base.VerificationError): self.verify()

    def test_changed_unselected_native_bytes_rejected(self):
        path = self.root/'changed'/'block-3.xml'
        path.write_bytes(path.read_bytes().replace(b'Original', b'Unexpected'))
        self.receipt()
        with self.assertRaisesRegex(base.VerificationError, 'Unselected native peer'): self.verify()

    def test_changed_source_beyond_author_rejected(self):
        path = self.root/'changed'/'block-0.xml'
        path.write_bytes(path.read_bytes().replace(b'</AttributeList>', b'<Unexpected>payload</Unexpected></AttributeList>'))
        self.result['changedCapture'] = self.capture(self.result['changed'])
        self.receipt()
        with self.assertRaisesRegex(base.VerificationError, 'Source delta exceeds'): self.verify()

    def test_changed_source_annotation_is_not_an_author_only_delta(self):
        path = self.root/'changed'/'block-0.xml'
        path.write_bytes(path.read_bytes() + b'<?vendor changed?>')
        self.result['changedCapture'] = self.capture(self.result['changed'])
        self.receipt()
        with self.assertRaisesRegex(base.VerificationError, 'Source delta exceeds'): self.verify()

    def test_restored_peer_file_must_match(self):
        path = self.root/'restored'/'tags-0.xml'
        path.write_bytes(path.read_bytes().replace(b'<HeaderAuthor>', b'<Extra/><HeaderAuthor>'))
        self.receipt()
        with self.assertRaisesRegex(base.VerificationError, 'Native peer bytes changed'): self.verify()

    def test_source_capture_cannot_alias_wrong_block(self):
        self.result['changedCapture']['artifacts'][1] = base.ref(self.root/'changed'/'block-2.xml')
        self.receipt()
        with self.assertRaisesRegex(base.VerificationError, 'Captured source bytes differ'): self.verify()

    def test_changed_receipt_or_missing_checkpoint_rejected(self):
        (self.root/'baseline.zap21').unlink()
        with self.assertRaises(base.VerificationError): self.verify()


if __name__ == '__main__':
    unittest.main()
