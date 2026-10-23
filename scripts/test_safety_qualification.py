"""Qualification gates for safety evidence; no Siemens runtime is started."""
import copy
import gc
import hashlib
import json
import pathlib
import tempfile
import unittest
import warnings
from types import SimpleNamespace
from unittest.mock import patch

import smoke_safety_direct as direct
import smoke_safety_scratch as scratch
import validate_safety_recovery as recovery
import safety_lifecycle
from block_test_runtime import NativeToolError
from test_safety_lifecycle import terminal_session
from test_safety_recovery_evidence import source_fixture, write_fixture

from audit_safety_preservation import audit
from smoke_mcp import ROOT
from test_compilation_evidence import clean_compile, device_compile, device_page, compile_only_records
from smoke_safety_direct import (native_payload_hash, require_applied_evidence,
                                 require_native_equal, require_safety_equal, require_signature_evidence)


PLC = '/projects/Fixture/devices/Station/items/CPU/plc/PLC'
BLOCK = PLC + '/blocks/Main_Safety_RTG1'
GENERATED = PLC + '/blocks/systemGroups/System/groups/Safety/DB1_C'
DEVICES = ['/projects/Fixture/devices/ScratchStation', '/projects/Fixture/devices/SafetyScratchStation']


def invalid_compiles():
    yield {'errorCount': 0}
    yield {'errorCount': False}
    yield {'errorCount': 0, 'targets': []}
    for field, value in (('state', 'Error'), ('messagesTruncated', True),
                         ('messages', [{'state': 'Information', 'children': [{'state': 'Error'}]}])):
        receipt = clean_compile(PLC)
        receipt['targets'][0][field] = value
        yield receipt


def with_safety_observations(report, safety):
    """Attach synthetic inspections to the existing retained-export lifecycle fixture."""
    report = copy.deepcopy(report)
    project = report['projectPath']
    steps = []
    for step in report['steps']:
        steps.append(step)
        if step['name'] == 'change_restore': project = step['result']['recoveredProject']['path']
        if step['name'] == 'project_open': project = step['arguments']['path']
        if step['name'] == 'block_read' and step['arguments']['blockPath'] == BLOCK:
            observation = {**copy.deepcopy(safety), 'project': {'path': project}, 'processId': step['result']['processId']}
            steps.append({'name': 'safety_inspect', 'arguments': {'plcPath': PLC, 'blockPath': BLOCK}, 'result': observation})
    report['steps'] = steps
    report['sessions'] = [terminal_session(row['result']['processId']) for row in steps if row['name'] == 'tia_start']
    report['lifetimePolicy'] = safety_lifecycle.POLICY
    report['cleanupVerified'] = True
    return report


class FakeSafetyClient:
    def __init__(self, replies, failed_tool=None):
        self.replies = replies
        self.failed_tool = failed_tool
        self.calls = []
        self.errors = []
        self.process = SimpleNamespace(returncode=None)

    def request(self, method, params):
        if method == 'initialize': return {}
        name = params['name']
        self.calls.append(name)
        if name not in self.replies: raise AssertionError('Unexpected tool dispatch: ' + name)
        value = self.replies[name]
        value = value.pop(0) if isinstance(value, list) else value
        if getattr(self, 'reply_context', False) and name in ('safety_inspect', 'block_read', 'project_retrieve'):
            value = copy.deepcopy(value); value['processId'] = self.session['processId']
            if name != 'project_retrieve': value['project'] = {'path': self.current_project}
        return {'isError': name == self.failed_tool, 'structuredContent': value, 'content': [{'type': 'text', 'text': json.dumps(value)}]}

    def notify(self, *args): pass

    def as_session(self, host, worker, artifacts, report, persist, directory, timeout=300):
        self.report, self.persist = report, persist
        self.report['lifetimePolicy'] = safety_lifecycle.POLICY
        self.session = None; self.known_paths = set(); self.active = False; self.current_project = None
        return self

    def start(self):
        self.session = {'calls': []}
        self.report.setdefault('sessions', []).append(self.session)
        self.active = True
        result = self.call('tia_start')
        self.session.update(terminal_session(result['processId']))
        self.session.pop('cleanup')
        return result

    def call(self, name, arguments=None):
        raw = self.request('tools/call', {'name': name, 'arguments': arguments or {}})
        result = raw['structuredContent']
        self.session['calls'].append({'tool': name, 'arguments': arguments or {},
            'status': 'failed' if raw['isError'] else 'passed', 'result': copy.deepcopy(result)})
        self.persist()
        if raw['isError']: raise NativeToolError(name, result)
        return result

    def remember(self, path):
        self.known_paths.add(path); self.current_project = path
    def guard(self): pass
    def reopen(self, path):
        self.close(); self.start()
        result = self.call('project_open', {'path': path}); self.remember(path); return result

    def close(self):
        if not self.active: return
        self.active = False
        if all(row['status'] == 'passed' for row in self.session['calls']): self.call('tia_detach')
        self.session['cleanup'] = terminal_session(self.session['processId'])['cleanup']
        self.process.returncode = 0
        if getattr(self, 'cleanup_error', False): raise RuntimeError('Synthetic terminal cleanup failure')


class SafetyQualificationTests(unittest.TestCase):
    def setUp(self):
        self.safety = {'programSignatures': [{'type': name, 'value': value} for name, value in
                           [('CollectiveOfflineSignature', 1), ('SoftwareOfflineSignature', 2),
                            ('HardwareOfflineSignature', 3), ('CommunicationAddressOfflineSignature', 0)]],
                       'blockSignatures': [{'type': 'BlockOfflineSignature', 'value': 2}],
                       'blockSignatureServiceAvailable': True, 'warnings': [],
                       'plcPath': PLC, 'block': {'path': BLOCK, 'plcPath': PLC},
                       'completeness': {'scopeComplete': True, 'truncated': False},
                       'runtimeGroups': [{'name': 'RTG1', 'mainSafetyBlockName': 'Main_Safety_RTG1'}]}
        self.applied = {'state': 'applied', 'compile': clean_compile(PLC),
                        'stages': ['compile_finished', 'changed_project_saved'], 'afterFingerprint': 'a' * 64,
                        'safetyAfter': {'signatures': self.safety['programSignatures']},
                        'fingerprintCoverage': {'policyVersion': 2, 'complete': False,
                            'protectedGeneratedPayloadExclusions': [{'name': 'DB1_C', 'path': GENERATED,
                                                                      'mode': 'protectedGeneratedMetadata'}],
                            'safetyCoverage': [{'plcPath': PLC, 'available': True, 'signatureCount': 4}]}}

    def test_truncated_or_warned_signatures_cannot_qualify(self):
        require_signature_evidence(self.safety)
        for extra in ({'completeness': {'truncated': True}}, {'warnings': ['read failed']}):
            with self.assertRaisesRegex(AssertionError, '[Tt]runcated|[Ii]ncomplete'):
                require_signature_evidence({**self.safety, **extra})

    def test_signature_values_require_uint64_and_required_nonzero_values(self):
        for key in ('programSignatures', 'blockSignatures'):
            for value in (None, True, False, -1, 1.0, '1', [], {}, 2**64, 0):
                bad = copy.deepcopy(self.safety)
                bad[key][0]['value'] = value
                with self.subTest(key=key, value=value), self.assertRaisesRegex(AssertionError, 'UInt64|absent or invalid'):
                    require_signature_evidence(bad)
            valid = copy.deepcopy(self.safety)
            for row in valid[key]: row['value'] = 2**64 - 1
            require_signature_evidence(valid)
        # The fixture's zero communication-address signature remains unavailable, not valid.
        require_signature_evidence(self.safety)

    def test_signature_identity_requires_exact_unique_fixture_set(self):
        for key in ('programSignatures', 'blockSignatures'):
            for defect in ('missing', 'extra', 'duplicate', 'unknown', 'untyped', 'extraField', 'notObject', 'notList'):
                bad = copy.deepcopy(self.safety)
                rows = bad[key]
                if defect == 'missing': rows.pop()
                elif defect == 'extra': rows.append(copy.deepcopy(rows[0]))
                elif defect == 'duplicate': rows[-1] = copy.deepcopy(rows[0]) if len(rows) > 1 else {'type': 'CollectiveOfflineSignature', 'value': 2}
                elif defect == 'unknown': rows[0]['type'] = 'FutureSignature'
                elif defect == 'untyped': del rows[0]['type']
                elif defect == 'extraField': rows[0]['unreviewed'] = 'retain'
                elif defect == 'notObject': rows[0] = 2
                else: bad[key] = {'type': 'BlockOfflineSignature', 'value': 2}
                with self.subTest(key=key, defect=defect), self.assertRaises(AssertionError):
                    require_signature_evidence(bad)

    def test_signature_completeness_requires_explicit_success_fields(self):
        for field, values in (('warnings', (None, '', False, ['incomplete'])),
                              ('blockSignatureServiceAvailable', (None, False, 1, 'true')),
                              ('completeness', (None, {}, {'scopeComplete': True}, {'truncated': False},
                                                {'scopeComplete': 1, 'truncated': False},
                                                {'scopeComplete': True, 'truncated': 0}))):
            for value in values:
                bad = copy.deepcopy(self.safety)
                if value is None: del bad[field]
                else: bad[field] = value
                with self.subTest(field=field, value=value), self.assertRaises(AssertionError):
                    require_signature_evidence(bad)
        for snapshot in (None, [], 'inspection'):
            with self.subTest(snapshot=snapshot), self.assertRaises(AssertionError):
                require_signature_evidence(snapshot)

    def test_matching_malformed_signatures_cannot_qualify_apply_or_equality(self):
        for value in (True, 1.0):
            applied = copy.deepcopy(self.applied)
            applied['safetyAfter']['signatures'][0]['value'] = value
            with self.subTest(reported=value), self.assertRaisesRegex(AssertionError, 'post-compile safety signatures'):
                require_applied_evidence(applied, self.safety, BLOCK)
        bad = copy.deepcopy(self.safety)
        bad['programSignatures'][0]['value'] = True
        applied = copy.deepcopy(self.applied)
        applied['safetyAfter']['signatures'] = bad['programSignatures']
        with self.assertRaisesRegex(AssertionError, 'UInt64'):
            require_applied_evidence(applied, bad, BLOCK)
        for expected, actual in ((bad, bad), (bad, self.safety), (self.safety, bad)):
            with self.assertRaisesRegex(AssertionError, 'UInt64'):
                require_safety_equal(expected, actual)

    def test_recovery_refuses_unbound_archive_and_incomplete_signatures_before_host(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'artifacts') as folder:
            root = pathlib.Path(folder)
            original = source_fixture(root, self.safety, self.applied)
            archive_bytes = original['archive'].read_bytes()
            for defect in ('hash', 'boundary', 'truncated', 'warnings', 'signatureValue', 'signatureType'):
                fixture = copy.deepcopy(original); safety = next(row['result'] for row in fixture['calls'] if row['tool'] == 'safety_inspect')
                if defect == 'hash': fixture['journal']['recoveryArchiveSha256'] = '0' * 64
                if defect == 'boundary': fixture['calls'][-1]['result']['recoveryArchive'] = str(pathlib.Path(__file__).resolve())
                if defect == 'truncated': safety['completeness'] = {'truncated': True}
                if defect == 'warnings': safety['warnings'] = ['incomplete']
                if defect == 'signatureValue': safety['programSignatures'][0]['value'] = True
                if defect == 'signatureType': safety['blockSignatures'][0]['type'] = 'Unknown'
                write_fixture(fixture)
                args = ['recovery', '--source-report', str(fixture['sourcePath']), '--report', str(root/'result.json'),
                        '--host', __file__, '--worker', __file__]
                with self.subTest(defect=defect), patch('sys.argv', args), patch.object(safety_lifecycle, 'new_session') as factory:
                    with self.assertRaisesRegex(AssertionError, 'checkpoint differs|artifact root|[Tt]runcated|[Ii]ncomplete|UInt64|Unknown'):
                        recovery.main()
                    factory.assert_not_called()
                self.assertEqual(archive_bytes, original['archive'].read_bytes())

    def test_incomplete_scratch_stops_before_catalog_and_retains_failure(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'artifacts') as folder:
            root = pathlib.Path(folder)
            client = FakeSafetyClient({'tia_start': {'processId': 1},
                'tia_create_scratch': {'completed': False, 'project': {'name': 'Fixture'}}, 'tia_detach': {}})
            with patch.object(scratch, 'ROOT', root), patch.object(safety_lifecycle, 'new_session', side_effect=client.as_session):
                with self.assertRaisesRegex(AssertionError, 'scratch.get'):
                    scratch.main()
            self.assertEqual(['tia_start', 'tia_create_scratch', 'tia_detach'], client.calls)
            report = json.loads((root/'artifacts/safety-scratch-smoke.json').read_text(encoding='utf-8'))
            self.assertFalse(report['completed'])
            self.assertIn('failure', report)
            self.assertTrue(report['cleanupVerified'])

    def test_scratch_entrypoints_cannot_complete_after_cleanup_failure(self):
        with tempfile.TemporaryDirectory(dir=ROOT/'artifacts') as directory:
            root = pathlib.Path(directory)
            for module in (direct, scratch):
                client = FakeSafetyClient({'tia_start': {'processId': 1}, 'tia_detach': {},
                    'tia_create_scratch': {'completed': True, 'compile': clean_compile(PLC), 'processId': 1, 'project': {'name': 'Fixture', 'path': str(root/'scratch/fixture.ap21')}},
                    'project_inventory': {'plcs': [{'name': 'SafetyScratchPLC', 'path': PLC}], 'processId': 1, 'project': {'path': str(root/'scratch/fixture.ap21')}},
                    'hardware_catalog_search': {'entries': [{'typeName': '1511F-1 PN', 'typeIdentifier': 'synthetic', 'version': 'V1'}]},
                    'change_prepare': {'changeId': 'hardware', 'previewHash': 'synthetic'}, 'change_apply': self.applied,
                    'safety_inspect': self.safety, 'software_inspect': {'blocks': [], 'completeness': {'scopeComplete': True}}})
                if module is scratch:
                    client.replies['change_apply'] = {**self.applied, 'compile': device_compile(DEVICES)}
                    client.replies['device_inventory'] = device_page(str(root/'scratch/fixture.ap21'), DEVICES)
                client.cleanup_error = True
                output = root/'direct.json' if module is direct else root/'artifacts/safety-scratch-smoke.json'
                with self.subTest(module=module.__name__), patch.object(module, 'ROOT', root), patch.object(safety_lifecycle, 'new_session', side_effect=client.as_session), patch('sys.argv', ['direct', '--report', str(output), '--host', __file__]):
                    with self.assertRaisesRegex(RuntimeError, 'terminal cleanup failure'): module.main()
                report = json.loads(output.read_text())
                self.assertTrue(report['qualificationChecksPassed'])
                self.assertFalse(report['completed']); self.assertFalse(report['cleanupVerified'])
                self.assertIn('cleanupFailure', report)

    def test_all_entrypoints_preserve_existing_reports_before_start(self):
        with tempfile.TemporaryDirectory(dir=ROOT/'artifacts') as directory:
            root = pathlib.Path(directory)
            for module in (direct, scratch, recovery):
                output = root/'artifacts/safety-scratch-smoke.json' if module is scratch else root/(module.__name__+'.json')
                output.parent.mkdir(parents=True, exist_ok=True); output.write_bytes(b'original evidence')
                argv = ['safety', '--report', str(output), '--host', __file__]
                if module is recovery: argv += ['--source-report', __file__]
                with self.subTest(module=module.__name__), patch.object(module, 'ROOT', root), patch('sys.argv', argv), patch.object(safety_lifecycle, 'new_session') as factory:
                    with self.assertRaisesRegex(AssertionError, 'immutable'): module.main()
                    factory.assert_not_called()
                self.assertEqual(b'original evidence', output.read_bytes())

    def test_ambiguous_direct_inventory_stops_before_inspection_and_retains_failure(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'artifacts') as folder:
            output = pathlib.Path(folder)/'result.json'
            client = FakeSafetyClient({'tia_start': {'processId': 1}, 'tia_create_scratch': {'completed': True, 'compile': clean_compile(PLC)},
                'project_inventory': {'plcs': [{'path': PLC}, {'path': PLC + 'Other'}]}, 'tia_detach': {}})
            with patch('sys.argv', ['direct', '--report', str(output), '--host', __file__]), patch.object(safety_lifecycle, 'new_session', side_effect=client.as_session):
                with self.assertRaisesRegex(AssertionError, 'unique newly created'):
                    direct.main()
            self.assertEqual(['tia_start', 'tia_create_scratch', 'project_inventory', 'tia_detach'], client.calls)
            report = json.loads(output.read_text(encoding='utf-8'))
            self.assertFalse(report['completed'])
            self.assertIn('failure', report)
            self.assertTrue(report['cleanupVerified'])

    def test_applied_safety_requires_complete_successful_diagnostics(self):
        for receipt in invalid_compiles():
            applied = {**self.applied, 'compile': receipt}
            original = copy.deepcopy(applied)
            with self.subTest(receipt=receipt), self.assertRaises(RuntimeError):
                require_applied_evidence(applied, self.safety, BLOCK)
            self.assertEqual(original, applied)
        for state in ('Success', 'Information', 'Warning'):
            applied = copy.deepcopy(self.applied)
            applied['compile']['targets'][0]['state'] = state
            require_applied_evidence(applied, self.safety, BLOCK)

    def test_safety_apply_rejects_clean_diagnostics_for_other_or_duplicate_plcs(self):
        for targets in ([PLC + '-other'], [PLC, PLC + '-other'], [PLC, PLC]):
            applied = copy.deepcopy(self.applied)
            applied['compile']['targets'] = [clean_compile(path)['targets'][0] for path in targets]
            with self.subTest(targets=targets), self.assertRaises(RuntimeError):
                require_applied_evidence(applied, self.safety, BLOCK)

    def test_both_scratch_harnesses_stop_on_invalid_baseline_compile(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'artifacts') as folder:
            root = pathlib.Path(folder)
            for module in (scratch, direct):
                for receipt in invalid_compiles():
                    client = FakeSafetyClient({'tia_start': {'processId': 1}, 'tia_detach': {},
                        'tia_create_scratch': {'completed': True, 'compile': receipt, 'project': {'name': 'Fixture'}}})
                    output = root/'direct.json' if module is direct else root/'artifacts/safety-scratch-smoke.json'
                    if output.exists(): output.unlink()
                    args = ['direct', '--report', str(output), '--host', __file__]
                    with self.subTest(module=module.__name__, receipt=receipt), patch.object(module, 'ROOT', root), patch.object(safety_lifecycle, 'new_session', side_effect=client.as_session), patch('sys.argv', args):
                        with self.assertRaises(RuntimeError): module.main()
                    self.assertEqual(['tia_start', 'tia_create_scratch', 'tia_detach'], client.calls)
                    report = json.loads(output.read_text(encoding='utf-8'))
                    self.assertFalse(report['completed'])
                    self.assertIn('failure', report)

    def test_error_envelope_cannot_admit_success_shaped_compile_receipt(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'artifacts') as folder:
            root = pathlib.Path(folder)
            for module, failed in ((direct, 'tia_create_scratch'), (scratch, 'tia_create_scratch'), (scratch, 'change_apply')):
                client = FakeSafetyClient({'tia_start': {'processId': 1}, 'tia_detach': {},
                    'tia_create_scratch': {'completed': True, 'compile': clean_compile(PLC), 'project': {'name': 'Fixture'}},
                    'hardware_catalog_search': {'entries': [{'typeName': '1511F-1 PN', 'typeIdentifier': 'synthetic', 'version': 'V1'}]},
                    'change_prepare': {'changeId': 'hardware', 'previewHash': 'synthetic'}, 'change_apply': self.applied}, failed_tool=failed)
                output = root/'direct.json' if module is direct else root/'artifacts/safety-scratch-smoke.json'
                if output.exists(): output.unlink()
                with self.subTest(module=module.__name__, failed=failed), patch.object(module, 'ROOT', root), patch.object(safety_lifecycle, 'new_session', side_effect=client.as_session), patch('sys.argv', ['direct', '--report', str(output), '--host', __file__]):
                    with self.assertRaises(RuntimeError): module.main()
                self.assertEqual([], client.calls[client.calls.index(failed)+1:])
                report = json.loads(output.read_text(encoding='utf-8'))
                self.assertFalse(report['completed'])
                self.assertTrue(next(row for row in report['steps'] if row['name'] == failed)['isError'])

    def test_hardware_fixture_requires_applied_compiled_and_saved_receipt(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'artifacts') as folder:
            root = pathlib.Path(folder)
            invalid = [{**self.applied, 'compile': value} for value in invalid_compiles()]
            invalid += [{**self.applied, 'state': 'failed'}, {**self.applied, 'stages': ['compile_finished']}]
            for applied in [self.applied, *invalid]:
                client = FakeSafetyClient({'tia_start': {'processId': 1}, 'tia_detach': {},
                    'tia_create_scratch': {'completed': True, 'compile': clean_compile(PLC), 'project': {'name': 'Fixture', 'path': str(root/'fixture.ap21')}},
                    'hardware_catalog_search': {'entries': [{'typeName': '1511F-1 PN', 'typeIdentifier': 'synthetic', 'version': 'V1'}]},
                    'change_prepare': {'changeId': 'hardware', 'previewHash': 'synthetic'}, 'change_apply': applied,
                    'project_inventory': {'plcs': [{'name': 'SafetyScratchPLC', 'path': PLC}]},
                    'safety_inspect': self.safety, 'software_inspect': {'blocks': []}})
                if applied is self.applied:
                    client.replies['change_apply'] = {**applied, 'compile': device_compile(DEVICES)}
                    client.replies['device_inventory'] = device_page(str(root/'fixture.ap21'), DEVICES)
                output = root/'artifacts/safety-scratch-smoke.json'
                if output.exists(): output.unlink()
                with self.subTest(applied=applied), patch.object(scratch, 'ROOT', root), patch.object(safety_lifecycle, 'new_session', side_effect=client.as_session):
                    if applied is self.applied: scratch.main()
                    else:
                        with self.assertRaises((RuntimeError, AssertionError)): scratch.main()
                report = json.loads((root/'artifacts/safety-scratch-smoke.json').read_text(encoding='utf-8'))
                self.assertEqual(applied is self.applied, report['completed'])
                self.assertEqual(applied is self.applied, 'project_inventory' in client.calls)
                self.assertEqual(1, client.calls.count('change_apply'))

    def test_hardware_fixture_refuses_missing_device_despite_successful_compile(self):
        with tempfile.TemporaryDirectory(dir=ROOT/'artifacts') as directory:
            root = pathlib.Path(directory); project = str(root/'fixture.ap21')
            client = FakeSafetyClient({'tia_start': {'processId': 1}, 'tia_detach': {},
                'tia_create_scratch': {'completed': True, 'compile': clean_compile(PLC), 'project': {'name': 'Fixture', 'path': project}},
                'hardware_catalog_search': {'entries': [{'typeName': '1511F-1 PN', 'typeIdentifier': 'synthetic', 'version': 'V1'}]},
                'change_prepare': {'changeId': 'hardware', 'previewHash': 'synthetic'},
                'change_apply': {**self.applied, 'compile': device_compile(DEVICES[:1])},
                'device_inventory': device_page(project, DEVICES)})
            with patch.object(scratch, 'ROOT', root), patch.object(safety_lifecycle, 'new_session', side_effect=client.as_session):
                with self.assertRaisesRegex(RuntimeError, 'required selection'): scratch.main()
            self.assertEqual(1, client.calls.count('change_apply'))
            self.assertNotIn('project_inventory', client.calls)
            self.assertFalse(json.loads((root/'artifacts/safety-scratch-smoke.json').read_text())['completed'])

    def test_recovery_checks_compile_before_further_readback_and_reopen(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'artifacts') as folder:
            root = pathlib.Path(folder)
            fixture = source_fixture(root, self.safety, self.applied)
            archive, source, journal = fixture['archive'], fixture['sourcePath'], fixture['journalPath']
            native = fixture['native']; export = pathlib.Path(native['artifact']['path'])
            original = {p: p.read_bytes() for p in (archive, export, journal, source)}
            compile_prepared, compile_applied = compile_only_records(PLC, str(root/'recovered.ap21'), 1, 'recompile')
            for receipt, cleanup_error in [(r, False) for r in [self.applied['compile'], *invalid_compiles()]] + [(self.applied['compile'], True)]:
                client = FakeSafetyClient({'tia_start': [{'ownsPortalProcess': True, 'processId': 1}, {'ownsPortalProcess': True, 'processId': 2}],
                    'tia_detach': {}, 'project_retrieve': {'retrieved': True, 'project': {'path': str(root/'recovered.ap21')}, 'archive': {'path': str(archive), 'bytes': archive.stat().st_size, 'sha256': hashlib.sha256(archive.read_bytes()).hexdigest()}},
                    'safety_inspect': fixture['safety'], 'block_read': native, 'project_open': {},
                    'change_prepare': compile_prepared, 'change_apply': {**self.applied, **compile_applied, 'compile': receipt}})
                client.cleanup_error = cleanup_error
                client.reply_context = True
                output = root/'result.json'
                if output.exists(): output.unlink()
                args = ['recovery', '--source-report', str(source), '--report', str(output), '--host', __file__, '--worker', __file__]
                with self.subTest(receipt=receipt), patch('sys.argv', args), patch.object(safety_lifecycle, 'new_session', side_effect=client.as_session):
                    if receipt is self.applied['compile'] and not cleanup_error: recovery.main()
                    else:
                        with self.assertRaises(RuntimeError): recovery.main()
                report = json.loads(output.read_text(encoding='utf-8'))
                self.assertEqual(receipt is self.applied['compile'] and not cleanup_error, report['completed'])
                if receipt is not self.applied['compile']:
                    self.assertNotIn('compiledReadback', report)
                    self.assertNotIn('project_open', client.calls)
                    self.assertEqual(['tia_detach'], client.calls[client.calls.index('change_apply')+1:])
                self.assertEqual(1, client.calls.count('change_apply'))
                self.assertEqual(original, {p: p.read_bytes() for p in original})

    def test_direct_roundtrip_compiles_fresh_before_exports_and_audits_original_calls(self):
        with tempfile.TemporaryDirectory(dir=ROOT/'artifacts') as directory:
            root = pathlib.Path(directory); fixture = source_fixture(root, self.safety, self.applied)
            project = root/'scratch/TiaMcpScratch_Baseline/TiaMcpScratch_Baseline.ap21'
            project.parent.mkdir(parents=True); project.write_bytes(b'synthetic project')
            prepared = fixture['calls'][-2]['result']; applied = fixture['calls'][-1]['result']
            for value in (prepared, applied): value['preview']['project']['path'] = str(project)
            fixture['journal']['projectPath'] = str(project)
            fixture['journal']['preview']['project']['path'] = str(project)
            fixture['journalPath'].write_text(json.dumps(fixture['journal']), encoding='utf-8')
            baseline_prepared, baseline_applied = compile_only_records(PLC, str(project), 1, 'fresh-baseline')
            baseline_applied = {**self.applied, **baseline_applied}
            recovered = root/'recovered.ap21'
            recovery_prepared, recovery_applied = compile_only_records(PLC, str(recovered), 1, 'recovery-compile')
            recovery_applied = {**self.applied, **recovery_applied}
            class Client(FakeSafetyClient):
                def request(self, method, params):
                    if method == 'tools/call' and params['name'] == 'change_prepare' and params['arguments']['kind'] == 'source_upsert':
                        self.calls.append('change_prepare')
                        return {'isError': True, 'structuredContent': {'error': {'code': 'SafetyBoundary'}}, 'content': []}
                    value = super().request(method, params)
                    if method == 'tools/call' and params['name'] == 'tia_create_scratch': self.current_project = str(project)
                    if method == 'tools/call' and params['name'] == 'change_restore': self.current_project = str(recovered)
                    return value
            client = Client({'tia_start': [{'ownsPortalProcess': True, 'processId': 1}, {'ownsPortalProcess': True, 'processId': 2}, {'ownsPortalProcess': True, 'processId': 3}],
                'tia_create_scratch': {'completed': True, 'processId': 1, 'project': {'path': str(project)}, 'compile': clean_compile(PLC)},
                'project_inventory': {'plcs': [{'path': PLC}], 'processId': 1, 'project': {'path': str(project)}},
                'safety_inspect': self.safety, 'block_read': fixture['native'],
                'software_inspect': {'completeness': {'scopeComplete': True}, 'blocks': [{'path': BLOCK, 'name': 'Main_Safety_RTG1',
                    'programmingLanguage': 'F_LAD', 'isConsistent': True, 'isKnowHowProtected': False}]},
                'change_prepare': [baseline_prepared, prepared, recovery_prepared], 'change_apply': [baseline_applied, applied, recovery_applied],
                'change_status': applied, 'change_restore': {'state': 'restored', 'recoveredProject': {'path': str(recovered)}},
                'project_open': [{'project': {'path': str(recovered)}}, {'project': {'path': str(project)}}], 'tia_detach': {}})
            client.reply_context = True
            output = root/'direct-baseline.json'
            argv = ['direct', '--roundtrip', '--verify-recovery', '--report', str(output), '--artifacts', str(root), '--host', __file__, '--worker', __file__]
            gc.collect()
            with warnings.catch_warnings(record=True) as observed:
                warnings.simplefilter('always', ResourceWarning)
                with patch('sys.argv', argv), patch.object(safety_lifecycle, 'new_session', side_effect=client.as_session): direct.main()
                gc.collect()
            self.assertEqual([], [str(w.message) for w in observed if issubclass(w.category, ResourceWarning)])
            report = json.loads(output.read_text(encoding='utf-8'))
            self.assertTrue(report['completed']); self.assertLess(client.calls.index('change_apply'), client.calls.index('block_read'))
            checked = audit(report); self.assertTrue(checked['freshBaselineCompilationVerified'])
            original = copy.deepcopy(report)
            for defect in ('payload', 'dispatch', 'target', 'summary', 'phaseOrder', 'sameValueRecovery'):
                bad = copy.deepcopy(original); calls = bad['sessions'][0]['calls']
                baseline = next(row for row in calls if row['tool'] == 'change_apply')
                if defect == 'payload': next(row for row in calls if row['tool'] == 'change_prepare')['arguments']['payload'] = {'xml': 'unchanged'}
                if defect == 'dispatch': baseline['result']['stages'].remove('compiler_dispatching')
                if defect == 'target': baseline['result']['compile']['targets'][0]['target'] += '-other'
                if defect == 'summary': bad['baselineCompile']['applied']['processId'] = 99
                if defect == 'sameValueRecovery':
                    preparations = [row for row in calls if row['tool'] == 'change_prepare' and row['arguments']['kind'] == 'plc_compile']
                    preparations[-1]['arguments']['kind'] = 'safety_import_xml'
                if defect == 'phaseOrder': calls.remove(baseline); calls.append(baseline)
                safety_lifecycle.sync_steps(bad)
                with self.subTest(defect=defect), self.assertRaises((RuntimeError, AssertionError)): audit(bad)

    def test_compile_and_save_without_durable_fingerprint_is_not_qualified(self):
        require_applied_evidence(self.applied, self.safety, BLOCK)
        for fingerprint in (None, '', 'x' * 64, 'a' * 63):
            failed = copy.deepcopy(self.applied)
            failed['afterFingerprint'] = fingerprint
            with self.assertRaisesRegex(AssertionError, 'fingerprint'):
                require_applied_evidence(failed, self.safety, BLOCK)

    def test_precompile_zero_signatures_cannot_be_reported_as_final(self):
        self.applied['safetyAfter'] = {'signatures': [{'type': 'CollectiveOfflineSignature', 'value': 0}]}
        with self.assertRaisesRegex(AssertionError, 'post-compile safety signatures'):
            require_applied_evidence(self.applied, self.safety, BLOCK)

    def test_correction_requires_generated_exclusion_with_signature_coverage(self):
        for mutate in (
            lambda row: row['fingerprintCoverage'].update(complete=True),
            lambda row: row['fingerprintCoverage'].update(protectedGeneratedPayloadExclusions=[]),
            lambda row: row['fingerprintCoverage']['protectedGeneratedPayloadExclusions'][0].update(path=BLOCK),
            lambda row: row['fingerprintCoverage'].update(safetyCoverage=[]),
            lambda row: row.update(stages=['compile_finished']),
            lambda row: row.update(compile={'errorCount': 1}),
        ):
            failed = copy.deepcopy(self.applied)
            mutate(failed)
            with self.assertRaises((AssertionError, RuntimeError)):
                require_applied_evidence(failed, self.safety, BLOCK)

    def test_fingerprint_signature_coverage_requires_exact_owning_plc(self):
        for rows in (None, [], [None], [{'plcPath': PLC + '-other', 'available': True, 'signatureCount': 4}],
                     self.applied['fingerprintCoverage']['safetyCoverage'] * 2,
                     *[[{'plcPath': PLC, 'available': available, 'signatureCount': count}]
                       for available, count in ((1, 4), (False, 4), (True, True), (True, 1), (True, 5), (True, '4'))]):
            bad = copy.deepcopy(self.applied)
            bad['fingerprintCoverage']['safetyCoverage'] = rows
            with self.subTest(rows=rows), self.assertRaisesRegex(AssertionError, 'owning-PLC'):
                require_applied_evidence(bad, self.safety, BLOCK)
        for field, value in (('plcPath', PLC + '-other'), ('block', None),
                             ('block', {'path': BLOCK + '-other', 'plcPath': PLC}),
                             ('block', {'path': BLOCK, 'plcPath': PLC + '-other'})):
            with self.subTest(field=field, value=value), self.assertRaisesRegex(AssertionError, 'selected PLC and block'):
                require_applied_evidence(self.applied, {**self.safety, field: value}, BLOCK)
        other = copy.deepcopy(self.applied)
        other['fingerprintCoverage']['safetyCoverage'].insert(0, {'plcPath': PLC + '-other', 'available': False, 'signatureCount': 0})
        require_applied_evidence(other, self.safety, BLOCK)

    def test_safety_reopen_checks_runtime_groups_and_both_signature_sets(self):
        require_safety_equal(self.safety, copy.deepcopy(self.safety))
        for key in ('runtimeGroups', 'programSignatures', 'blockSignatures'):
            actual = copy.deepcopy(self.safety)
            actual[key] = []
            with self.assertRaises(AssertionError):
                require_safety_equal(self.safety, actual)

    def test_native_equality_removes_only_top_level_export_provenance(self):
        before = '<Document><DocumentInfo><Created>before</Created></DocumentInfo><SW.Blocks.FB><Comment>keep</Comment><DocumentInfo>native</DocumentInfo></SW.Blocks.FB></Document>'
        after = before.replace('<Created>before</Created>', '<Created>after</Created>')
        require_native_equal({BLOCK: {'xml': before}}, {BLOCK: {'xml': after}})
        self.assertEqual(native_payload_hash(before), native_payload_hash(after))
        for changed in (after.replace('>keep<', '>lost<'), after.replace('>native<', '>lost<')):
            with self.assertRaisesRegex(AssertionError, 'payload differs'):
                require_native_equal({BLOCK: {'xml': before}}, {BLOCK: {'xml': changed}})
        with self.assertRaisesRegex(AssertionError, 'selection changed'):
            require_native_equal({BLOCK: {'xml': before}}, {})

    def test_native_equality_rejects_raw_comments_and_namespace_binding_loss(self):
        before = '<Document><Block xmlns:q="urn:original" ref="q:Type"><?check preserve?><!--retain--><Text>value</Text></Block></Document>'
        for changed in (before.replace('<!--retain-->', ''), before.replace('<?check preserve?>', ''),
                        before.replace('urn:original', 'urn:other')):
            with self.assertRaisesRegex(AssertionError, '[Pp]ayload differs'):
                require_native_equal({BLOCK: {'xml': before}}, {BLOCK: {'xml': changed}})

    def test_retained_audit_catches_xml_comment_loss_and_missing_reopen(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'artifacts') as folder:
            directory = pathlib.Path(folder)
            baseline = '<Document><Block><!--retain--><Text>original</Text></Block></Document>'
            changed = baseline.replace('>original<', '>changed<')

            def export(xml, label):
                target = directory / (label + '.xml')
                target.write_text(xml, encoding='utf-8')
                return {'xml': xml, 'processId': 1, 'artifact': {'path': str(target), 'sha256': hashlib.sha256(target.read_bytes()).hexdigest()}}

            before = export(baseline, 'before')
            after = export(changed, 'after')
            reopened = export(changed, 'reopened')
            reopened['processId'] = 2
            project = str(directory / 'fixture.ap21')
            report = {'completed': True, 'projectPath': project,
                      'nativePreservation': {'before': {BLOCK: {'artifact': before['artifact']}}}, 'steps': [
                          {'name': 'tia_start', 'result': {'ownsPortalProcess': True, 'processId': 1}},
                          {'name': 'block_read', 'arguments': {'blockPath': BLOCK}, 'result': before},
                          {'name': 'change_prepare', 'arguments': {'kind': 'safety_import_xml', 'payload': {'blockPath': BLOCK, 'xml': changed}}, 'result': {'changeId': 'first'}},
                          {'name': 'change_apply', 'result': {**self.applied, 'changeId': 'first'}},
                          {'name': 'block_read', 'arguments': {'blockPath': BLOCK}, 'result': after},
                          {'name': 'tia_start', 'result': {'ownsPortalProcess': True, 'processId': 2}},
                          {'name': 'project_open', 'arguments': {'path': project}, 'result': {}},
                          {'name': 'block_read', 'arguments': {'blockPath': BLOCK}, 'result': reopened},
                      ]}
            audited = audit(with_safety_observations(report, self.safety))
            self.assertEqual(3, len(audited['checks']))
            self.assertTrue(audited['compileDiagnosticsVerified'])
            self.assertFalse(audited['baselineCompilationObserved'])
            self.assertFalse(audited['compileTargetCoverageVerified'])
            self.assertFalse(audited['compilations'][0]['beforeMutation'])
            self.assertTrue(audited['signaturePhaseCoverageVerified'])
            self.assertFalse(audited['signatureNativeAuthenticityVerified'])
            unchanged_report = copy.deepcopy(report)
            unchanged_report['steps'][2]['arguments']['payload']['xml'] = baseline
            unchanged_report['steps'][4]['result'] = copy.deepcopy(before)
            unchanged_report['steps'][7]['result'] = {**copy.deepcopy(before), 'processId': 2}
            unchanged_report = with_safety_observations(unchanged_report, self.safety)
            unchanged_audit = audit(unchanged_report)
            for check in unchanged_audit['signatureChecks']:
                if check['stage'] != 'before':
                    unchanged_report['steps'][check['step']]['result']['programSignatures'][0]['value'] += 1
            next(row for row in unchanged_report['steps'] if row['name'] == 'change_apply')['result']['safetyAfter']['signatures'][0]['value'] += 1
            with self.assertRaisesRegex(AssertionError, 'readback differs'):
                audit(unchanged_report)
            for phase in ('scratch', 'apply', 'recoveryApply'):
                complete = copy.deepcopy(report)
                if phase == 'scratch':
                    complete['steps'].insert(1, {'name': 'tia_create_scratch', 'result': {'completed': True, 'compile': clean_compile(PLC)}})
                    selected = complete['steps'][1]
                elif phase == 'apply': selected = complete['steps'][3]
                else:
                    selected = {'name': 'change_apply', 'result': {**self.applied, 'changeId': 'recompile'}}
                    complete['recovery'] = {'status': 'passed'}
                    changed_reopen = copy.deepcopy(complete['steps'][5:])
                    changed_reopen[0]['result']['processId'] = 3
                    changed_reopen[-1]['result']['processId'] = 3
                    recovered_reopen = copy.deepcopy(complete['steps'][1])
                    recovered_reopen['result']['processId'] = 2
                    complete['steps'] = complete['steps'][:5] + [
                        {'name': 'change_restore', 'result': {'state': 'restored', 'recoveredProject': {'path': str(directory/'recovered.ap21')}}},
                        copy.deepcopy(report['steps'][1]),
                        {'name': 'change_prepare', 'arguments': {'kind': 'safety_import_xml', 'payload': {'blockPath': BLOCK, 'xml': baseline}}, 'result': {'changeId': 'recompile'}},
                        selected, copy.deepcopy(report['steps'][1]),
                        {'name': 'tia_start', 'result': {'ownsPortalProcess': True, 'processId': 2}},
                        {'name': 'project_open', 'arguments': {'path': str(directory/'recovered.ap21')}, 'result': {}},
                        recovered_reopen, *changed_reopen]
                compiled_audit = audit(with_safety_observations(complete, self.safety))
                self.assertTrue(compiled_audit['compileDiagnosticsVerified'])
                self.assertEqual(phase == 'scratch', compiled_audit['baselineCompilationObserved'])
                self.assertTrue(all(not row['beforeMutation'] for row in compiled_audit['compilations'] if row['tool'] == 'change_apply'))
                if phase == 'recoveryApply':
                    observed = with_safety_observations(complete, self.safety)
                    self.assertEqual(6, len(compiled_audit['signatureChecks']))
                    changed_signatures = copy.deepcopy(observed)
                    for check in compiled_audit['signatureChecks']:
                        if check['stage'] in ('after', 'changed-reopened'):
                            changed_signatures['steps'][check['step']]['result']['programSignatures'][0]['value'] += 1
                    next(row for row in changed_signatures['steps'] if row['name'] == 'change_apply')['result']['safetyAfter']['signatures'][0]['value'] += 1
                    self.assertEqual(6, len(audit(changed_signatures)['signatureChecks']))
                    for check in compiled_audit['signatureChecks']:
                        for defect in ('missing', 'failed', 'value', 'changedValue', 'plc', 'block', 'blockOwner', 'project', 'process', 'boolProcess', 'capture', 'selector'):
                            bad = copy.deepcopy(observed)
                            selected_signature = bad['steps'][check['step']]
                            result = selected_signature['result']
                            if defect == 'missing': del bad['steps'][check['step']]
                            elif defect == 'failed': selected_signature['isError'] = True
                            elif defect == 'value': result['programSignatures'][0]['value'] = True
                            elif defect == 'changedValue': result['programSignatures'][0]['value'] += 1
                            elif defect == 'plc': result['plcPath'] += '-other'
                            elif defect == 'block': result['block']['path'] += '-other'
                            elif defect == 'blockOwner': result['block']['plcPath'] += '-other'
                            elif defect == 'project': result['project']['path'] += '-other'
                            elif defect == 'process': result['processId'] = 99
                            elif defect == 'boolProcess': result['processId'] = True
                            elif defect == 'capture': result['completeness']['scopeComplete'] = False
                            else: selected_signature['arguments']['blockPath'] += '-other'
                            with self.subTest(stage=check['stage'], defect=defect), self.assertRaises(AssertionError):
                                audit(bad)
                    for step in observed['steps']:
                        if step['name'] == 'project_open' and step['arguments']['path'] != project:
                            step['arguments']['path'] = str(directory/'unrelated.ap21')
                    with self.assertRaisesRegex(AssertionError, 'recorded recovery copy'):
                        audit(observed)
                for receipt in invalid_compiles():
                    selected['result']['compile'] = receipt
                    with self.subTest(phase=phase, receipt=receipt), self.assertRaises(RuntimeError):
                        audit(with_safety_observations(complete, self.safety))
                selected['result']['compile'] = clean_compile(PLC)
                selected['isError'] = True
                with self.assertRaisesRegex(AssertionError, 'Failed compiler-bearing'):
                    audit(with_safety_observations(complete, self.safety))
            for defect in ('incomplete', 'originalHash', 'exportHash', 'unowned', 'wrongProcess', 'reusedProcess'):
                bad = copy.deepcopy(report)
                if defect == 'incomplete': bad['completed'] = False
                elif defect == 'originalHash': bad['nativePreservation']['before'][BLOCK]['artifact']['sha256'] = '0' * 64
                elif defect == 'exportHash': bad['steps'][-1]['result']['artifact']['sha256'] = '0' * 64
                elif defect == 'unowned': bad['steps'][0]['result']['ownsPortalProcess'] = False
                elif defect == 'wrongProcess': bad['steps'][-1]['result']['processId'] = 99
                else:
                    bad['steps'][5]['result']['processId'] = 1
                    bad['steps'][-1]['result']['processId'] = 1
                with self.subTest(defect=defect), self.assertRaises(AssertionError):
                    audit(with_safety_observations(bad, self.safety))
            bad = copy.deepcopy(report)
            bad['steps'][-1]['result'] = export(changed.replace('<!--retain-->', ''), 'lost-comment')
            bad['steps'][-1]['result']['processId'] = 2
            with self.assertRaisesRegex(AssertionError, 'Full native payload differs'):
                audit(with_safety_observations(bad, self.safety))
            bad = copy.deepcopy(report)
            bad['steps'] = bad['steps'][:-2]
            with self.assertRaisesRegex(AssertionError, 'Missing required native audit stages'):
                audit(with_safety_observations(bad, self.safety))


if __name__ == '__main__':
    unittest.main()
