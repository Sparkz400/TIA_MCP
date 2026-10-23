"""Original-report safety checkpoint admission; every fixture is synthetic."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from smoke_mcp import ROOT
from run_block_tests import file_record
from smoke_safety_direct import native_payload_hash
from engineering_preservation import native_payload_hash as complete_hash
from test_safety_lifecycle import terminal_session
from compilation_evidence import BASELINE_POLICY
from test_compilation_evidence import compile_only_records
import safety_lifecycle
import safety_recovery_evidence as evidence
import validate_safety_recovery as recovery


def write_fixture(fixture):
    safety_lifecycle.sync_steps(fixture['source'])
    fixture['sourcePath'].write_text(json.dumps(fixture['source']), encoding='utf-8')
    fixture['journalPath'].write_text(json.dumps(fixture['journal']), encoding='utf-8')


def source_fixture(root, safety, applied):
    selected, plc = safety['block']['path'], safety['plcPath']
    name = selected.rsplit('/', 1)[-1]
    change_id, preview_hash = 'a'*32, 'b'*64
    folder = root/('change-'+change_id); folder.mkdir()
    archive = folder/'before.zap21'; archive.write_bytes(b'synthetic original archive')
    export = root/'original.xml'; xml = '<Document><Block><!--retain--><Text>original</Text></Block></Document>'
    export.write_text(xml, encoding='utf-8')
    project = str(root/'original.ap21')
    native = {'xml': xml, 'artifact': file_record(export), 'processId': 1, 'project': {'path': project},
              'block': {'path': selected, 'plcPath': plc, 'name': name}}
    inspection = {**copy.deepcopy(safety), 'processId': 1, 'project': {'path': project}}
    inspection['block']['name'] = name
    payload = {'blockPath': selected, 'xml': xml, 'expectedBlockNames': [name], 'safetyAcknowledged': True}
    preview = {'changeId': change_id, 'kind': 'safety_import_xml', 'payload': payload, 'project': {'path': project}}
    journal_path = folder/'journal.json'
    prepared = {'changeId': change_id, 'state': 'prepared', 'previewHash': preview_hash, 'preview': preview,
                'journalPath': str(journal_path), 'processId': 1}
    result = {**copy.deepcopy(applied), 'changeId': change_id, 'previewHash': preview_hash, 'preview': preview,
              'journalPath': str(journal_path), 'processId': 1, 'automaticRetryAllowed': False,
              'recoveryArchive': str(archive), 'recoveryArchiveSha256': file_record(archive)['sha256']}
    result['stages'] = ['prechange_project_saved', 'recovery_archive_created', *result['stages']]
    journal = {**copy.deepcopy(result), 'kind': 'safety_import_xml', 'projectPath': project}
    baseline_prepared, baseline_applied = compile_only_records(plc, project, 1, 'baseline')
    session = terminal_session(1)
    session['calls'] = [
        {'tool': 'tia_start', 'arguments': {}, 'status': 'passed', 'result': {'processId': 1, 'ownsPortalProcess': True}},
        {'tool': 'safety_inspect', 'arguments': {'plcPath': plc, 'blockPath': selected}, 'status': 'passed', 'result': inspection},
        {'tool': 'block_read', 'arguments': {'blockPath': selected}, 'status': 'passed', 'result': native},
        {'tool': 'change_prepare', 'arguments': {'kind': 'safety_import_xml', 'payload': payload}, 'status': 'passed', 'result': prepared},
        {'tool': 'change_apply', 'arguments': {'changeId': change_id, 'previewHash': preview_hash, 'checkpointSave': True}, 'status': 'passed', 'result': result}]
    session['calls'][1:1] = [
        {'tool': 'change_prepare', 'arguments': {'kind': 'plc_compile', 'payload': {}, 'plcPath': plc}, 'status': 'passed', 'result': baseline_prepared},
        {'tool': 'change_apply', 'arguments': {'changeId': baseline_prepared['changeId'], 'previewHash': baseline_prepared['previewHash'],
            'checkpointSave': True, 'compile': True, 'save': True}, 'status': 'passed', 'result': baseline_applied}]
    source = {'baselineCompilePolicy': BASELINE_POLICY, 'baselineCompile': {'prepared': baseline_prepared, 'applied': baseline_applied}, 'projectPath': project, 'completed': True, 'cleanupVerified': True, 'lifetimePolicy': safety_lifecycle.POLICY,
              'sessions': [session], 'nativePreservation': {'before': {selected: {'artifact': file_record(export),
                  'nativePayloadHash': native_payload_hash(xml), 'completeNativePayloadHash': complete_hash(xml)}}}}
    fixture = {'source': source, 'sourcePath': root/'source.json', 'journal': journal, 'journalPath': journal_path,
               'archive': archive, 'native': native, 'safety': inspection, 'calls': session['calls']}
    write_fixture(fixture)
    return fixture


class SafetyRecoveryEvidenceTests(unittest.TestCase):
    def setUp(self):
        from test_safety_qualification import SafetyQualificationTests
        setup = SafetyQualificationTests(); setup.setUp()
        self.safety, self.applied = setup.safety, setup.applied
        self.temp = tempfile.TemporaryDirectory(dir=ROOT/'artifacts'); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.fixture = source_fixture(self.root, self.safety, self.applied)

    def load(self): return evidence.load_source(self.fixture['sourcePath'], self.root, ROOT)

    def test_original_success_and_known_failure_bind_the_same_checkpoint(self):
        packet = self.load(); evidence.recheck_inputs(packet)
        self.assertEqual(file_record(self.fixture['archive']), packet['archive'])
        self.assertFalse(packet['sourceAuthenticityVerified'])
        applied = self.fixture['calls'][-1]
        applied['result']['state'] = 'recovery_required'
        applied['result'] = {'error': {'code': 'CompileFailed', 'details': applied['result']}}
        applied['status'] = 'failed'; self.fixture['source']['completed'] = False
        self.fixture['journal']['state'] = 'recovery_required'; write_fixture(self.fixture)
        self.assertEqual(packet['archive'], self.load()['archive'])

    def test_mismatched_or_uncertain_evidence_stops_before_host_creation(self):
        original = copy.deepcopy(self.fixture)
        for defect in ('journalId', 'previewHash', 'journalProject', 'archiveDigest', 'blockSelection', 'failedInspection',
                       'exportOwner', 'inlineXml', 'missingBaseline', 'lateBaseline', 'unknownApply', 'duplicateApply',
                       'wrongName', 'missingLifetime', 'wrongProcess', 'missingStages', 'outsideJournal', 'stepSubstitution'):
            self.fixture = copy.deepcopy(original); calls = self.fixture['calls']; journal = self.fixture['journal']
            if defect == 'journalId': journal['changeId'] = 'c'*32
            elif defect == 'previewHash': journal['previewHash'] = 'c'*64
            elif defect == 'journalProject': journal['projectPath'] += '.other'
            elif defect == 'archiveDigest': journal['recoveryArchiveSha256'] = 'c'*64
            elif defect == 'blockSelection': calls[3]['arguments']['blockPath'] += '-other'
            elif defect == 'failedInspection': calls[3]['status'] = 'failed'
            elif defect == 'exportOwner': calls[4]['result']['block']['path'] += '-other'
            elif defect == 'inlineXml': calls[4]['result']['xml'] = '<Document><Other/></Document>'
            elif defect == 'missingBaseline': self.fixture['source']['nativePreservation']['before'] = {}
            elif defect == 'lateBaseline': calls.append(calls.pop(3))
            elif defect == 'unknownApply': calls[-1].update(status='failed', result=None)
            elif defect == 'duplicateApply': calls.append(copy.deepcopy(calls[-1]))
            elif defect == 'wrongName': calls[5]['arguments']['payload']['expectedBlockNames'] = ['Other']
            elif defect == 'missingLifetime': self.fixture['source']['cleanupVerified'] = False
            elif defect == 'wrongProcess': calls[-1]['result']['processId'] = 99
            elif defect == 'missingStages': calls[-1]['result']['stages'] = []
            elif defect == 'outsideJournal': calls[5]['result']['journalPath'] = str(ROOT/'scripts/test_safety_recovery_evidence.py')
            write_fixture(self.fixture)
            if defect == 'stepSubstitution':
                source = json.loads(self.fixture['sourcePath'].read_text()); source['steps'][1]['result']['processId'] = 99
                self.fixture['sourcePath'].write_text(json.dumps(source), encoding='utf-8')
            argv = ['recover', '--source-report', str(self.fixture['sourcePath']), '--report', str(self.root/'result.json'), '--artifacts', str(self.root)]
            with self.subTest(defect=defect), patch('sys.argv', argv), patch.object(safety_lifecycle, 'new_session') as factory:
                with self.assertRaises((AssertionError, ValueError)): recovery.main()
                factory.assert_not_called()

    def test_fresh_baseline_rejects_missing_stale_or_mutating_original_calls(self):
        original = copy.deepcopy(self.fixture)
        for defect in ('policy', 'summary', 'sameValueWrite', 'payload', 'flags', 'lateCompile', 'wrongOwner', 'extraApply', 'changedProject'):
            self.fixture = copy.deepcopy(original); calls = self.fixture['calls']; source = self.fixture['source']
            prepare, apply = calls[1], calls[2]
            if defect == 'policy': source.pop('baselineCompilePolicy')
            if defect == 'summary': source['baselineCompile']['applied']['stages'].append('mutation_applied')
            if defect == 'sameValueWrite': prepare['arguments']['kind'] = 'safety_import_xml'
            if defect == 'payload': prepare['arguments']['payload'] = {'xml': 'unchanged'}
            if defect == 'flags': apply['arguments']['compile'] = False
            if defect == 'lateCompile': calls[1:5] = calls[3:5] + calls[1:3]
            if defect == 'wrongOwner': apply['result']['processId'] = 99
            if defect == 'extraApply': calls.insert(3, copy.deepcopy(apply))
            if defect == 'changedProject': calls.insert(3, {'tool': 'project_open', 'arguments': {}, 'status': 'passed', 'result': {}})
            write_fixture(self.fixture)
            with self.subTest(defect=defect), self.assertRaises((RuntimeError, AssertionError, KeyError)): self.load()

    def test_every_retained_input_is_rechecked_without_repinning(self):
        packet = self.load()
        for record in packet['inputs']:
            path = Path(record['path']); original = path.read_bytes()
            try:
                path.write_bytes(original + b'changed')
                with self.subTest(path=path.name), self.assertRaisesRegex(AssertionError, 'changed after admission'):
                    evidence.recheck_inputs(packet)
            finally: path.write_bytes(original)
        evidence.recheck_inputs(packet)

    def test_changed_archive_after_start_stops_before_retrieval(self):
        from test_safety_qualification import FakeSafetyClient
        client = FakeSafetyClient({'tia_start': {'processId': 1}, 'tia_detach': {}})
        start = client.start
        def changed_start():
            result = start(); self.fixture['archive'].write_bytes(b'changed during Host startup'); return result
        client.start = changed_start
        output = self.root/'result.json'
        argv = ['recover', '--source-report', str(self.fixture['sourcePath']), '--report', str(output), '--artifacts', str(self.root)]
        with patch('sys.argv', argv), patch.object(safety_lifecycle, 'new_session', side_effect=client.as_session):
            with self.assertRaisesRegex(AssertionError, 'changed after admission'): recovery.main()
        self.assertEqual(['tia_start', 'tia_detach'], client.calls)
        self.assertFalse(json.loads(output.read_text())['completed'])

    def test_substituted_retrieval_receipts_and_changes_during_close_cannot_pass(self):
        from test_safety_qualification import FakeSafetyClient
        original = self.fixture['archive'].read_bytes()
        for defect in ('digest', 'path', 'bytes', 'process', 'boolProcess', 'duringClose'):
            self.fixture['archive'].write_bytes(original)
            archive = file_record(self.fixture['archive'])
            result = {'retrieved': True, 'processId': 1, 'archive': archive, 'project': {'path': str(self.root/'recovered.ap21')}}
            if defect in ('digest', 'duringClose'): archive['sha256'] = '0'*64
            elif defect == 'path': archive['path'] += '.other'
            elif defect == 'bytes': archive['bytes'] = True
            elif defect == 'process': result['processId'] = 99
            else: result['processId'] = True
            client = FakeSafetyClient({'tia_start': {'processId': 1}, 'project_retrieve': result, 'tia_detach': {}})
            if defect == 'duringClose':
                close = client.close
                def changed_close():
                    close(); self.fixture['archive'].write_bytes(b'changed during close')
                client.close = changed_close
            output = self.root/(defect+'.json')
            argv = ['recover', '--source-report', str(self.fixture['sourcePath']), '--report', str(output), '--artifacts', str(self.root)]
            with self.subTest(defect=defect), patch('sys.argv', argv), patch.object(safety_lifecycle, 'new_session', side_effect=client.as_session):
                with self.assertRaisesRegex(AssertionError, 'Retrieval|changed after admission'): recovery.main()
            self.assertEqual(['tia_start', 'project_retrieve', 'tia_detach'], client.calls)
            report = json.loads(output.read_text()); self.assertFalse(report['completed'])
            if defect == 'duringClose': self.assertIn('preservationFailure', report['sourceEvidence'])

    def test_duplicate_json_keys_cannot_change_source_interpretation(self):
        path = self.fixture['sourcePath']; source = path.read_text()
        path.write_text('{"cleanupVerified":false,' + source[1:], encoding='utf-8')
        with self.assertRaisesRegex(AssertionError, 'Duplicate safety evidence JSON key'): self.load()


if __name__ == '__main__': unittest.main()
