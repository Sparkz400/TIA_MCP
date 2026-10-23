"""Checkpoint receipt byte/context checks with synthetic archives and no Siemens."""
import copy
import hashlib
import json
from pathlib import Path
import sys
import os
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import encoder_checkpoint
from encoder_checkpoint import verify_seeder_checkpoint
from smoke_mcp import ROOT


def artifact(path):
    data = path.read_bytes()
    return {'path': str(path), 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}


def checkpoint_result(auth_path, authorization):
    archive = auth_path.with_suffix('.zap21'); archive.write_bytes(b'synthetic pre-mutation checkpoint')
    receipt = {'schemaVersion': 1, 'phase': 'beforeEncoderMutation', 'processId': authorization['processId'],
        'projectPath': authorization['projectPath'], 'authorization': artifact(auth_path), 'archive': artifact(archive)}
    path = Path(str(auth_path) + '.checkpoint.json'); path.write_text(json.dumps(receipt), encoding='utf-8')
    return {'completed': True, 'checkpointArchive': str(archive), 'checkpointReceipt': artifact(path)}


class EncoderCheckpointTests(unittest.TestCase):
    def setUp(self):
        (ROOT / 'artifacts').mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix='checkpoint-', dir=ROOT / 'artifacts'); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        auth = {'processId': 123, 'projectPath': str(self.root / 'TiaMcpScratch_Test.ap21')}
        path = self.root / 'connect.authorization.json'; path.write_text(json.dumps(auth), encoding='utf-8')
        self.report = {'runDirectory': str(self.root), 'projectPath': auth['projectPath'], 'sessions': [{'processId': 123}],
            'seederConnect': checkpoint_result(path, auth), 'seederLifetimes': [{
            'mode': 'connect', 'authorization': str(path), 'authorizationSha256': artifact(path)['sha256'],
            'authorizationProcessId': auth['processId'], 'authorizationProjectPath': auth['projectPath'],
            'terminal': {'completed': True, 'exitCode': 0}, 'unknown': [], 'outcomeCaptured': True}]}

    def rewrite(self, update):
        path = Path(self.report['seederConnect']['checkpointReceipt']['path'])
        receipt = json.loads(path.read_text()); update(receipt)
        path.write_text(json.dumps(receipt), encoding='utf-8'); self.report['seederConnect']['checkpointReceipt'] = artifact(path)

    def test_exact_receipt_verifies_without_changing_evidence(self):
        before = copy.deepcopy(self.report); value = verify_seeder_checkpoint(self.report)
        self.assertEqual(artifact(Path(self.report['seederConnect']['checkpointArchive'])), value)
        self.assertEqual(before, self.report)

    def test_replacement_archive_cannot_supply_its_own_expected_digest(self):
        path = Path(self.report['seederConnect']['checkpointArchive']); path.write_bytes(b'substituted archive')
        self.report['seederConnect']['checkpointArchiveSha256'] = artifact(path)['sha256']
        with self.assertRaisesRegex(AssertionError, 'bytes changed'): verify_seeder_checkpoint(self.report)

    def test_phase_source_identity_and_original_authorization_are_required(self):
        original = copy.deepcopy(self.report)
        path = Path(original['seederConnect']['checkpointReceipt']['path']); raw = path.read_bytes()
        for key, value in (('schemaVersion', True), ('phase', 'afterMutation'), ('processId', True),
                           ('processId', 124), ('projectPath', 'other.ap21')):
            self.report = copy.deepcopy(original); path.write_bytes(raw)
            self.rewrite(lambda receipt: receipt.update({key: value}))
            with self.subTest(key=key, value=value), self.assertRaises(AssertionError): verify_seeder_checkpoint(self.report)
        self.report = copy.deepcopy(original); path.write_bytes(raw)
        auth = Path(self.report['seederLifetimes'][0]['authorization']); auth.write_text('changed authorization')
        self.rewrite(lambda receipt: receipt.update(authorization=artifact(auth)))
        with self.assertRaisesRegex(AssertionError, 'original dispatched bytes'): verify_seeder_checkpoint(self.report)

    def test_wrong_run_path_hash_byte_count_or_missing_receipt_refuses(self):
        for defect in ('missing', 'hash', 'bytes', 'outside', 'other-invocation'):
            report = copy.deepcopy(self.report); value = report['seederConnect']['checkpointReceipt']
            if defect == 'missing': report['seederConnect'].pop('checkpointReceipt')
            if defect == 'hash': value['sha256'] = '0' * 64
            if defect == 'bytes': value['bytes'] = True
            if defect == 'outside': value['path'] = str(ROOT / 'scripts/encoder_checkpoint.py')
            if defect == 'other-invocation': report['seederLifetimes'][0]['authorization'] += '.other'
            with self.subTest(defect=defect), self.assertRaises((AssertionError, ValueError)): verify_seeder_checkpoint(report)

    def test_uncertain_or_duplicate_helper_cannot_authorize_recovery(self):
        for defect in ('pending', 'exit', 'booleanExit', 'source', 'unknown', 'capture', 'launch', 'outcome', 'duplicate'):
            report = copy.deepcopy(self.report); packet = report['seederLifetimes'][0]
            if defect == 'pending': packet['terminal']['completed'] = False
            if defect == 'exit': packet['terminal']['exitCode'] = -1
            if defect == 'booleanExit': packet['terminal']['exitCode'] = False
            if defect == 'source': report['sessions'][0]['processId'] = 999
            if defect == 'unknown': packet['unknown'] = ['timeout']
            if defect == 'capture': packet['captureFailure'] = 'capture failed'
            if defect == 'launch': packet['launchFailure'] = 'launch failed'
            if defect == 'outcome': packet['outcomeCaptured'] = False
            if defect == 'duplicate': report['seederLifetimes'].append(copy.deepcopy(packet))
            with self.subTest(defect=defect), self.assertRaises(AssertionError): verify_seeder_checkpoint(report)

    def test_archive_directory_alias_cannot_bypass_run_boundary(self):
        target = self.root / 'target'; target.mkdir()
        archive = target / 'checkpoint.zap21'; archive.write_bytes(b'archive')
        link = self.root / 'alias'
        if os.name == 'nt':
            subprocess.run(['cmd', '/c', 'mklink', '/J', str(link), str(target)], capture_output=True, check=True)
        else: link.symlink_to(target, target_is_directory=True)
        try:
            reference = artifact(link / 'checkpoint.zap21')
            self.rewrite(lambda receipt: receipt.update(archive=reference))
            self.report['seederConnect']['checkpointArchive'] = reference['path']
            with self.assertRaises((AssertionError, ValueError)): verify_seeder_checkpoint(self.report)
        finally:
            if os.name == 'nt': link.rmdir()
            else: link.unlink()
        self.assertEqual(b'archive', archive.read_bytes())

    def test_receipt_replacement_between_hash_and_parse_is_rejected(self):
        receipt_path = Path(self.report['seederConnect']['checkpointReceipt']['path'])
        original = encoder_checkpoint.file_record
        def replace_after_hash(path):
            observed = original(path)
            if path == receipt_path:
                archive = Path(self.report['seederConnect']['checkpointArchive']); archive.write_bytes(b'replaced archive')
                receipt = json.loads(path.read_bytes()); receipt['archive'] = artifact(archive)
                path.write_text(json.dumps(receipt), encoding='utf-8')
            return observed
        with patch.object(encoder_checkpoint, 'file_record', side_effect=replace_after_hash), \
             self.assertRaisesRegex(AssertionError, 'changed before parsing'):
            verify_seeder_checkpoint(self.report)


if __name__ == '__main__':
    if len(sys.argv) == 3 and sys.argv[1] == '--fixture':
        report = json.loads(Path(sys.argv[2]).read_text(encoding='utf-8'))
        verify_seeder_checkpoint(report)
        print('PASS actual C# checkpoint receipt verified independently in Python; synthetic archive only')
    else:
        unittest.main()
