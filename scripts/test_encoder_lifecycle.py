"""Run encoder orchestration against exact-controller fake processes, never Siemens."""
import base64
import copy
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import encoder_lifecycle as lifetime
import qualify_encoder_hardware as qualifier
import verify_encoder_preservation as verifier
import audit_encoder_preservation as auditor
from component_exact_lifetime import ComponentOwnedScratch
from smoke_mcp import ROOT
import test_encoder_hardware_qualification as encoder_tests
import test_encoder_software_preservation as peer_tests
from test_engineering_preservation import ENCODER
from test_translation_exact_lifetime import FakeProcess, FakeLifetime
from test_block_test_project_open import lock_error
from test_compilation_evidence import clean_compile, device_compile, device_page, compile_only_records
from test_encoder_checkpoint import checkpoint_result, artifact


STATION = '/projects/Fixture/devices/Station'
RAIL = STATION + '/items/Rail'
CPU_PATH = RAIL + '/items/CPU'
MODULE = RAIL + '/items/TiaMcp_EncoderHardware'
PLC = CPU_PATH + '/plc/PLC'


class EncoderLifecycleTests(unittest.TestCase):
    def setUp(self):
        (ROOT / 'artifacts').mkdir(exist_ok=True)
        self.folder = tempfile.TemporaryDirectory(prefix='encoder-lifecycle-', dir=ROOT / 'artifacts')
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)
        # Run directories stay inside this test's folder instead of piling up in artifacts/.
        for runner in (qualifier, verifier):
            runs = patch.object(runner, 'RUNS', self.root); runs.start(); self.addCleanup(runs.stop)
        self.binary = self.root / 'synthetic.exe'; self.binary.write_bytes(b'never executed')
        self.report_path = self.root / 'report.json'
        self.profile = json.loads((ROOT / 'fixtures/encoder-hardware/cpu1511-fw41-tm-posinput2-v10.json').read_text(encoding='utf-8'))
        self.profile['dependencies'] = {'Sensor.Type': 0}
        self.profile['parameterEdit'] = {'Sensor.InverseDirection': True}
        self.configuration = self.root / 'profile.json'; self.configuration.write_text(json.dumps(self.profile), encoding='utf-8')
        fixture = encoder_tests.EncoderEvidenceTests(); fixture.setUp()
        self.before, self.after = fixture.before, fixture.after
        self.current = copy.deepcopy(self.before)
        self.calls, self.events, self.trees = [], [], []
        self.path = None; self.prepared = None; self.fault = None
        self.helper = None; self.serial = 0; self.close_count = 0; self.locked = set()
        self.peer_fault = None
        self.response_transform = None
        self.compile_fault = None; self.change_serial = 0; self.target_fault = None

    def compilation(self, phase):
        value = device_compile([STATION]) if phase in ('hardware', 'seeder') else clean_compile(PLC)
        if self.target_fault == phase:
            if phase in ('hardware', 'seeder'):
                value['requiredTargets'] = ['/projects/Other/devices/Station']; value['targets'][0]['targetPath'] = value['requiredTargets'][0]
            else: value['targets'][0]['target'] = '/unrelated/plc'
        if self.compile_fault == phase: value['targets'][0]['messages'].append({'state': 'Error'})
        return value

    def controller(self, *args, **kwargs):
        test = self
        class Host:
            def __init__(self, *args):
                test.serial += 1
                self.pid = test.serial * 10 + 2
                self.process = SimpleNamespace(pid=self.pid - 2, returncode=0)
                self.errors = []; self.error_reader = SimpleNamespace(is_alive=lambda: False)
                self.artifacts = Path(args[2][args[2].index('--artifacts') + 1])
                test.path = None
            def notify(self, *args): pass
            def close(self): test.fail('The automatic-kill Client.close path must never run')
            def request(self, method, arguments):
                if method == 'initialize': return {}
                name, supplied = arguments['name'], arguments['arguments']; test.calls.append((self.pid, name, supplied))
                if name == 'tia_discover': value = {'processes': []}
                elif name == 'tia_start':
                    if test.fault == 'startLost': raise TimeoutError('startup reply lost')
                    value = {'attached': True, 'ownsPortalProcess': True, 'processId': self.pid, 'projects': []}
                elif name == 'tia_session_status':
                    value = {'attached': True, 'ownsPortalProcess': True, 'processId': self.pid,
                             'projects': [{'path': test.path}] if test.path else []}
                elif name == 'tia_create_scratch':
                    test.original = str(self.artifacts / 'original' / 'TiaMcpScratch_Test' / 'TiaMcpScratch_Test.ap21')
                    test.recovered = str(self.artifacts / 'recovered' / 'TiaMcpScratch_Test' / 'TiaMcpScratch_Test.ap21')
                    test.path = test.original
                    Path(test.path).parent.mkdir(parents=True); Path(test.path).write_bytes(b'baseline')
                    (Path(test.path).parent/'database.bin').write_bytes(b'synthetic database')
                    (Path(test.path).parent/'empty').mkdir()
                    compile = test.compilation('scratch')
                    if test.fault == 'compile': compile['errorCount'] = 1
                    value = {'completed': True, 'compile': compile, 'project': {'path': test.path, 'name': 'Fixture'}, 'processId': self.pid}
                elif name == 'project_inventory':
                    value = {'project': {'path': test.path, 'name': 'Fixture'}, 'processId': self.pid, 'warnings': [],
                        'completeness': {'scopeComplete': True, 'truncated': False, 'complete': False},
                        'plcs': [{'path': PLC, 'hardwarePath': CPU_PATH, 'name': 'PLC'}],
                        'hardware': [{'positionNumber': 0, 'path': RAIL, 'name': 'Rail', 'typeIdentifier': None},
                            {'positionNumber': 1, 'path': CPU_PATH, 'name': 'CPU', 'typeIdentifier': test.profile['cpuTypeIdentifier']},
                            {'positionNumber': 2, 'path': MODULE, 'name': 'TiaMcp_EncoderHardware', 'typeIdentifier': test.profile['moduleTypeIdentifier']},
                            {'positionNumber': 1, 'path': MODULE + '/items/' + test.profile['channelItemName'], 'name': test.profile['channelItemName'], 'typeIdentifier': None}]}
                    value['hardware'].append({'positionNumber': 3, 'path': RAIL + '/items/UnrelatedIo', 'name': 'UnrelatedIo', 'typeIdentifier': 'SyntheticIo'})
                    for item in value['hardware']:
                        item['classification'] = 'CPU' if item['path'] == CPU_PATH else 'Hardware'
                        item['addresses'] = ([{'ioType': 'Input', 'startAddress': 512, 'length': 16, 'lengthUnit': 'bits', 'byteSpan': 2}]
                                             if item['path'] == CPU_PATH else [])
                    self.inventory = copy.deepcopy(value)
                elif name == 'device_inventory':
                    value = device_page(test.path, ['/projects/Fixture/devices/Station'])
                elif name == 'hardware_inspect':
                    value = encoder_tests.hardware_inspection(self.inventory, supplied['hardwarePath'], test.profile)
                elif name == 'hardware_catalog_search':
                    value = {'entries': [{'articleNumber': '6ES7 551', 'typeIdentifier': test.profile['moduleTypeIdentifier']}]}
                elif name == 'change_prepare':
                    test.prepared = supplied; test.change_serial += 1
                    value = {'changeId': 'change-' + str(test.change_serial), 'previewHash': 'hash'}
                    if supplied['kind'] == 'plc_compile':
                        value, test.compile_applied = compile_only_records(PLC, test.path, self.pid, value['changeId'])
                elif name == 'change_apply':
                    if test.fault == 'applyLost' or test.fault == 'baselineLost' and test.prepared['kind'] == 'plc_compile' and test.path == test.original:
                        raise TimeoutError('apply reply lost')
                    if test.prepared['kind'] == 'to_parameters':
                        test.edit_archive = self.artifacts/'changes'/'before.zap21'; test.edit_archive.parent.mkdir(parents=True, exist_ok=True)
                        test.edit_archive.write_bytes(b'synthetic pre-edit archive')
                        Path(test.path).write_bytes(b'changed')
                        for name_, val in test.prepared['payload']['parameters'].items(): test.current[name_]['value'] = val
                    phase = ('hardware' if test.prepared['kind'] == 'hardware_plug' else
                             'baseline' if test.prepared['kind'] == 'plc_compile' and test.path == test.original else
                             'edit' if test.current['Sensor.InverseDirection']['value'] else 'recovery')
                    value = {'state': 'applied', 'compile': test.compilation(phase), 'stages': ['changed_project_saved']}
                    if test.prepared['kind'] == 'plc_compile': value = {**test.compile_applied, 'compile': test.compilation(phase)}
                    if test.prepared['kind'] == 'to_parameters':
                        value.update(recoveryArchive=str(test.edit_archive), recoveryArchiveSha256=hashlib.sha256(test.edit_archive.read_bytes()).hexdigest())
                elif name == 'change_status': value = {'state': 'applied'}
                elif name == 'change_restore':
                    test.current = copy.deepcopy(test.before); test.path = test.recovered
                    archive = test.edit_archive
                    Path(test.path).parent.mkdir(parents=True, exist_ok=True); Path(test.path).write_bytes(b'baseline')
                    (Path(test.path).parent/'database.bin').write_bytes(b'synthetic database')
                    (Path(test.path).parent/'empty').mkdir(exist_ok=True)
                    value = {'state': 'restored', 'recoveredProject': {'path': test.path}, 'recoveryArchive': str(archive),
                             'recoveryArchiveSha256': hashlib.sha256(archive.read_bytes()).hexdigest()}
                elif name == 'software_inspect':
                    selected = peer_tests.technology(PLC, qualifier.NAME)
                    self.software = peer_tests.inventory({'path': test.path, 'name': 'Fixture'}, self.pid, PLC, selected)
                    value = copy.deepcopy(self.software)
                elif name in ('block_read', 'type_read', 'tag_table_read') or name == 'technology_read' and 'technologyPath' in supplied:
                    families = {'block_read': ('blocks', 'blockPath', 'block'), 'type_read': ('types', 'typePath', 'type'),
                                'tag_table_read': ('tagTables', 'tagTablePath', 'tagTable'), 'technology_read': ('technologyObjects', 'technologyPath', 'technologyObject')}
                    family, argument, field = families[name]
                    row = next(copy.deepcopy(row) for row in self.software[family] if row['path'] == supplied[argument])
                    xml = '<Document><Peer family="' + family + '"><Value>7</Value></Peer></Document>'
                    if family == 'blocks' and row['technologyPath'] is not None:
                        xml = ENCODER
                        if test.current['Sensor.InverseDirection']['value']: xml = auditor.expected_encoder_edit(xml)
                    elif test.peer_fault == family and test.current['Sensor.InverseDirection']['value']:
                        xml = xml.replace('>7<', '>8<')
                    file = self.artifacts / ('peer-' + str(len(test.calls)) + '.xml'); file.write_text(xml, encoding='utf-8')
                    value = {'project': {'path': test.path, 'name': 'Fixture'}, 'processId': self.pid, 'plcPath': PLC, field: row,
                             'artifact': {'path': str(file), 'sha256': hashlib.sha256(file.read_bytes()).hexdigest()}}
                elif name == 'technology_read':
                    xml = ENCODER
                    if test.current['Sensor.InverseDirection']['value']:
                        xml = xml.replace('<Member Name="InverseDirection" Datatype="Bool"/>',
                            '<Member Name="InverseDirection" Datatype="Bool"><StartValue>true</StartValue></Member>')
                    self.xml = xml.encode(); file = self.artifacts / ('export-' + str(len(test.calls)) + '.xml'); file.write_bytes(self.xml)
                    value = {'warnings': [], 'completeness': {'scopeComplete': True}, 'processId': self.pid,
                        **encoder_tests.connected_native(test.profile, test.path, self.pid, PLC, MODULE, self.inventory),
                        'project': {'path': test.path, 'name': 'Fixture'}, 'plcPath': PLC, 'parameters': list(copy.deepcopy(test.current).values()),
                        'artifact': {'path': str(file), 'sha256': hashlib.sha256(self.xml).hexdigest()}}
                    old_path = value['technologyObject']['path']
                    new_path = PLC + '/technology/' + qualifier.NAME
                    def replace_path(item):
                        if isinstance(item, dict): return {key: replace_path(val) for key, val in item.items()}
                        if isinstance(item, list): return [replace_path(val) for val in item]
                        if isinstance(item, str) and item.startswith(old_path): return new_path + item[len(old_path):]
                        return item
                    value = replace_path(value)
                    value['technologyObject'] = peer_tests.technology(PLC, qualifier.NAME)
                elif name == 'artifact_read':
                    data = Path(supplied['path']).read_bytes()
                    value = {'complete': True, 'data': base64.b64encode(data).decode(), 'sha256': hashlib.sha256(data).hexdigest()}
                elif name == 'engineering_read':
                    value = {'scopeComplete': True, 'truncated': False, 'processId': self.pid, 'project': {'path': test.path, 'name': 'Fixture'}, 'technologyObjects': [{'path': PLC + '/technology/' + qualifier.NAME,
                        'typeIdentifier': 'TO_ExternalEncoder', 'version': '10.0', 'parametersComplete': True, 'parametersTruncated': False,
                        'parameters': list(copy.deepcopy(test.current).values())}]}
                elif name == 'tia_detach':
                    value = {'attached': False, 'disposedOwnedPortalConnection': test.fault != 'detach'}
                elif name == 'project_open':
                    if test.fault in ('knownLock', 'unknownLock') and self.pid not in test.locked:
                        test.locked.add(self.pid)
                        value = lock_error(supplied['path'], user=os.environ['USERNAME'],
                            computer=os.environ['COMPUTERNAME'] if test.fault == 'knownLock' else 'another-host')
                        return {'structuredContent': value, 'isError': True}
                    if test.fault == 'inspectionWrites': (Path(supplied['path']).parent/'runtime.tmp').write_bytes(b'copy-only native metadata')
                    test.path = supplied['path']; test.current = copy.deepcopy(test.after if Path(test.path).read_bytes() == b'changed' else test.before)
                    value = {'opened': True, 'saved': False, 'project': {'path': test.path}, 'processId': self.pid + (1 if test.fault == 'openForeignProcess' else 0)}
                elif name == 'project_retrieve':
                    test.assertEqual(test.checkpoint['sha256'], supplied['expectedSha256'])
                    if test.fault == 'retrieveLost': raise TimeoutError('retrieval reply lost')
                    test.path = str(self.artifacts / 'retrieved' / 'TiaMcpScratch_Test.ap21')
                    value = {'retrieved': True, 'processId': self.pid, 'project': {'path': test.path}, 'archive': dict(test.checkpoint)}
                    if test.fault == 'retrieveWrongReceipt': value['archive']['sha256'] = '0' * 64
                else: test.fail('Unexpected tool ' + name)
                if test.response_transform is not None:
                    value = test.response_transform(name, supplied, copy.deepcopy(value))
                    if name == 'project_inventory': self.inventory = copy.deepcopy(value)
                return {'structuredContent': value}
        class Tree:
            def __init__(self, process, host, worker, directory):
                self.unknown = False; self.pending_start = None
                def row(pid, role):
                    proof = {'processId': pid, 'handleCaptured': True, 'birthAndImageVerified': True}
                    watcher = SimpleNamespace(evidence=proof, api=SimpleNamespace(wait=lambda *_: 258), _handle=pid)
                    return {'identity': {'pid': pid, 'parentPid': process.pid + (1 if role == 'native' else 0), 'created': 0},
                            'role': role, 'watcher': watcher}
                self.anchor = row(process.pid, 'host'); self.worker = row(process.pid + 1, 'worker'); self.native = row(process.pid + 2, 'native')
                test.trees.append(self)
            def snapshot(self): return [self.anchor, self.worker, self.native]
            def one(self, role): return self.worker
            def await_capture_complete(self): test.events.append('capture-complete')
            def begin_start(self): self.pending_start = True
            def accept_start(self, pid):
                test.assertEqual(self.native['identity']['pid'], pid); self.pending_start = None; return self.native
            def close_record(self, row):
                return {'completed': True, 'processId': row['identity']['pid'], 'exitCode': 0, 'processExitVerified': True,
                    'handleClosed': True, 'sameOsHandleRetained': True, 'forcedTermination': False, 'deadlineOrCancellationObserved': False}
            def close_all(self):
                test.events.append('same-tree-terminal')
                return {'completed': self.pending_start is None, 'forcedTermination': False, 'startupOutcomeUnknown': self.pending_start is not None,
                    'exits': [{'role': row['role'], 'identity': row['identity'], 'proof': self.close_record(row)} for row in self.snapshot()]}
        def close(client, tree):
            test.close_count += 1
            test.events.append('host-eof')
            if test.fault == 'checkpointDuringClose': Path(test.checkpoint['path']).write_bytes(b'changed during close')
            if test.fault == 'closeInterrupted': raise KeyboardInterrupt()
            return {'hostExitCode': -1 if test.fault == 'hostExit' or test.fault == 'finalHostExit' and test.close_count == 3 else 0, 'forcedHostStop': test.fault == 'forced',
                'stderrEofObserved': True, 'closeInterrupted': None, 'tree': tree.close_all(),
                'workerExits': [] if test.fault == 'workerReceipt' else [{'processId': tree.worker['identity']['pid'],
                    'startedAtUtc': '1970-01-01T00:00:00Z', 'processExitVerified': True, 'exitCode': 0, 'forcedTermination': False}]}
        kwargs.update(client_factory=Host, tree_factory=Tree, closer=close, manifest_factory=lambda *_: {'synthetic': True})
        return ComponentOwnedScratch(*args, **kwargs)

    def seeder_process(self, command, **kwargs):
        auth_path = Path(command[2]); auth = json.loads(auth_path.read_text(encoding='utf-8'))
        value = {'completed': True, 'compile': self.compilation('seeder'), **{key: auth[key] for key in ('projectPath', 'processId', 'plcPath', 'cpuHardwarePath', 'moduleHardwarePath')}}
        if command[1] == 'connect':
            auth_path = Path(command[2]); auth = json.loads(auth_path.read_text())
            value.update(checkpoint_result(auth_path, auth))
            self.checkpoint = artifact(Path(value['checkpointArchive']))
            if self.fault in ('seederFailed', 'checkpointTamper', 'checkpointMissing', 'checkpointWrongReceipt', 'checkpointDuringClose', 'retrieveWrongReceipt', 'retrieveLost'):
                value['completed'] = False
            if self.fault == 'checkpointTamper': Path(value['checkpointArchive']).write_bytes(b'replaced after checkpoint')
            if self.fault == 'checkpointMissing': value.pop('checkpointReceipt')
            if self.fault == 'checkpointWrongReceipt': value['checkpointReceipt']['sha256'] = '0' * 64
        kwargs['stdout'].write(b'not json' if self.fault == 'helperOutput' else
            json.dumps(value).encode())
        outcomes = [subprocess.TimeoutExpired('synthetic', 180), 0] if self.fault == 'helperTimeout' else [0]
        self.helper = FakeProcess(self.events, outcomes)
        return self.helper, RuntimeError('constructor failed after spawn') if self.fault == 'helperConstruct' else None

    def run_qualifier(self, fault=None, discovery=False):
        self.fault = fault
        argv = ['qualify', '--host', str(self.binary), '--worker', str(self.binary), '--seeder', str(self.binary), '--report', str(self.report_path)]
        if not discovery: argv += ['--configuration', str(self.configuration)]
        class SeederLifetime(FakeLifetime):
            def __init__(inner, *args):
                if self.fault == 'helperCapture': raise OSError('identity capture failed')
                super().__init__(*args)
            def finish(self):
                return {**super().finish(), 'identity': self.proof['identity'],
                        'sameCreateProcessHandle': True, 'birthAndImageVerified': True}
        with patch.object(lifetime, 'ComponentOwnedScratch', side_effect=self.controller), \
             patch.object(lifetime, 'launch_helper', side_effect=self.seeder_process), \
             patch.object(lifetime, 'CreatedProcess', side_effect=SeederLifetime), \
             patch('sys.argv', argv):
            qualifier.main()
        return json.loads(self.report_path.read_text(encoding='utf-8'))

    def test_success_requires_all_three_exact_session_cleanups(self):
        result = self.run_qualifier()
        self.assertTrue(result['completed']); self.assertTrue(result['cleanupVerified'])
        self.assertEqual(3, self.close_count); self.assertEqual(3, len(result['sessions']))
        self.assertEqual(3, len({s['processId'] for s in result['sessions']}))
        lifetime.verify_lifecycle(result)

    def test_final_cleanup_failure_overrides_successful_qualification_checks(self):
        with self.assertRaises((AssertionError, RuntimeError)): self.run_qualifier('finalHostExit')
        report = json.loads(self.report_path.read_text())
        self.assertTrue(report['qualificationChecksPassed']); self.assertFalse(report['completed'])
        self.assertFalse(report['cleanupVerified']); self.assertEqual(3, self.close_count)

    def test_cleanup_failures_stop_before_reopen_and_cannot_complete(self):
        for fault in ('detach', 'hostExit', 'workerReceipt', 'closeInterrupted', 'forced'):
            self.calls.clear(); self.report_path = self.root / (fault + '.json')
            with self.subTest(fault=fault), self.assertRaises((AssertionError, RuntimeError)):
                self.run_qualifier(fault)
            report = json.loads(self.report_path.read_text(encoding='utf-8'))
            self.assertFalse(report['completed']); self.assertFalse(report['cleanupVerified'])
            self.assertNotIn('project_open', [name for _, name, _ in self.calls])

    def test_lost_apply_never_replays_or_dispatches_recovery(self):
        with self.assertRaises(Exception): self.run_qualifier('applyLost')
        tools = [name for _, name, _ in self.calls]
        self.assertEqual(1, tools.count('change_apply'))
        self.assertNotIn('change_restore', tools); self.assertNotIn('change_status', tools); self.assertNotIn('tia_detach', tools)
        self.assertFalse(json.loads(self.report_path.read_text())['completed'])

    def test_lost_start_cannot_create_or_report_completion(self):
        with self.assertRaises(Exception): self.run_qualifier('startLost')
        tools = [name for _, name, _ in self.calls]
        self.assertEqual(1, tools.count('tia_start')); self.assertNotIn('tia_create_scratch', tools)
        self.assertFalse(json.loads(self.report_path.read_text())['completed'])

    def test_recovered_endpoints_have_distinct_owners_and_ids_but_same_hardware(self):
        from encoder_endpoints import endpoint, same
        source = self.run_qualifier()
        before, recovered = source['nativeBefore'], source['recovery']['nativeRecovered']
        self.assertNotEqual(before['hardwareConnections'], recovered['hardwareConnections'])
        self.assertTrue(same(endpoint(before, self.profile), endpoint(recovered, self.profile)))
        result = {}; auditor.audit(source, result)
        self.assertTrue(result['endpointCorrespondenceVerified'])
        companion = self.run_companion(); self.audit_companion(companion)

    def test_foreign_hardware_inspection_stops_before_parameter_mutation(self):
        real = encoder_tests.hardware_inspection
        def foreign(*args):
            value = real(*args); value['project']['path'] = 'foreign.ap21'; return value
        with patch.object(encoder_tests, 'hardware_inspection', side_effect=foreign):
            with self.assertRaisesRegex(AssertionError, 'another owner'): self.run_qualifier()
        self.assertNotIn('to_parameters', [args['kind'] for _, name, args in self.calls if name == 'change_prepare'])

    def test_original_endpoint_evidence_tamper_blocks_companion_before_startup(self):
        self.run_qualifier()
        source = json.loads(self.report_path.read_text())
        original = next(row for row in source['sessions'][0]['calls'] if row['tool'] == 'hardware_inspect')
        original['arguments']['hardwarePath'] += '/wrong'
        lifetime.sync_steps(source)
        self.report_path.write_text(json.dumps(source), encoding='utf-8')
        prior = len(self.trees)
        with self.assertRaisesRegex(AssertionError, 'original exact inspection'): self.run_companion()
        self.assertEqual(prior, len(self.trees))
        with self.assertRaisesRegex(AssertionError, 'original exact inspection'): auditor.audit(source, {})

    def test_companion_endpoint_summaries_are_bound_to_original_inspections(self):
        self.run_qualifier(); companion = self.run_companion()
        damaged = copy.deepcopy(companion)
        damaged['copies']['recovered']['nativeReadback']['endpointEvidence']['hardware'][0]['items'][0]['positionNumber'] = 99
        with self.assertRaises(AssertionError): self.audit_companion(damaged)
        damaged = copy.deepcopy(companion)
        row = next(row for row in damaged['sessions'][0]['calls'] if row['tool'] == 'hardware_inspect')
        row['arguments']['projectName'] = 'foreign'
        lifetime.sync_steps(damaged)
        with self.assertRaises(AssertionError): self.audit_companion(damaged)

    def test_failed_compile_precedes_hardware_work(self):
        with self.assertRaises(Exception): self.run_qualifier('compile')
        self.assertNotIn('change_prepare', [name for _, name, _ in self.calls])
        self.assertFalse(json.loads(self.report_path.read_text())['completed'])

    def test_each_compile_phase_refuses_hidden_errors_and_preserves_failed_outcome(self):
        for phase in ('scratch', 'hardware', 'seeder', 'baseline', 'edit', 'recovery'):
            self.compile_fault = phase; self.report_path = self.root / (phase + '.json'); self.calls.clear()
            self.current = copy.deepcopy(self.before)
            with self.subTest(phase=phase), self.assertRaises(RuntimeError): self.run_qualifier()
            result = json.loads(self.report_path.read_text()); self.assertFalse(result['completed'])
            self.assertNotIn('qualificationChecksPassed', result)
            if phase == 'scratch': self.assertNotIn('change_prepare', [name for _, name, _ in self.calls])
            if phase in ('hardware', 'seeder', 'baseline'): self.assertNotIn('nativeBefore', result)

    def test_helper_deadline_waits_for_terminal_without_replay(self):
        with self.assertRaises(Exception): self.run_qualifier('helperTimeout')
        self.assertEqual(0, self.helper.returncode)
        self.assertLess(self.events.index('original-handle-terminal'), self.events.index('host-eof'))
        self.assertNotIn('change_restore', [name for _, name, _ in self.calls])
        report = json.loads(self.report_path.read_text()); self.assertFalse(report['completed'])
        self.assertFalse(report['seederLifetimes'][0]['terminal']['completed'])

    def test_partial_helper_construction_waits_for_original_terminal(self):
        with self.assertRaises(Exception): self.run_qualifier('helperConstruct')
        self.assertEqual(0, self.helper.returncode)
        self.assertLess(self.events.index('original-handle-terminal'), self.events.index('host-eof'))
        self.assertNotIn('change_restore', [name for _, name, _ in self.calls])
        self.assertFalse(json.loads(self.report_path.read_text())['completed'])

    def test_discovery_remains_distinct_from_qualification(self):
        report = self.run_qualifier(discovery=True)
        self.assertTrue(report['discoveryCompleted']); self.assertTrue(report['cleanupVerified']); self.assertFalse(report['completed'])

    def test_explicit_empty_configuration_and_existing_report_refuse_before_start(self):
        self.configuration.write_text('{}', encoding='utf-8')
        with self.assertRaises(AssertionError): self.run_qualifier()
        self.assertEqual([], self.calls)
        self.report_path.write_text('original evidence', encoding='utf-8')
        with self.assertRaises(AssertionError): self.run_qualifier(discovery=True)
        self.assertEqual('original evidence', self.report_path.read_text())
        self.assertEqual([], self.calls)

    def test_helper_capture_and_output_failure_finish_original_process_without_recovery(self):
        for fault in ('helperCapture', 'helperOutput'):
            self.report_path = self.root / (fault + '.json'); self.events.clear(); self.calls.clear()
            with self.subTest(fault=fault), self.assertRaises(Exception): self.run_qualifier(fault)
            self.assertEqual(0, self.helper.returncode)
            self.assertLess(self.events.index('original-handle-terminal'), self.events.index('host-eof'))
            self.assertNotIn('change_restore', [name for _, name, _ in self.calls])
            self.assertFalse(json.loads(self.report_path.read_text())['completed'])

    def test_known_failed_seeder_retrieves_only_its_pre_mutation_archive(self):
        with self.assertRaises(AssertionError): self.run_qualifier('seederFailed')
        result = json.loads(self.report_path.read_text()); recovery = result['failureRecovery']
        self.assertFalse(result['completed']); self.assertTrue(result['cleanupVerified'])
        self.assertEqual('passed', recovery['status']); self.assertFalse(recovery['fullNativePreservationVerified'])
        self.assertEqual(self.checkpoint, recovery['checkpoint'])
        self.assertEqual(2, self.close_count)
        calls = [(pid, args) for pid, name, args in self.calls if name == 'project_retrieve']
        self.assertEqual(1, len(calls)); self.assertNotEqual(result['sessions'][0]['processId'], calls[0][0])
        self.assertEqual(self.checkpoint['sha256'], calls[0][1]['expectedSha256'])

    def test_changed_or_missing_checkpoint_refuses_retrieval(self):
        for fault in ('checkpointTamper', 'checkpointMissing', 'checkpointWrongReceipt', 'checkpointDuringClose'):
            self.report_path = self.root / (fault + '.json'); self.calls.clear()
            with self.subTest(fault=fault), self.assertRaises(AssertionError): self.run_qualifier(fault)
            result = json.loads(self.report_path.read_text()); self.assertFalse(result['completed'])
            self.assertNotIn('failureRecovery', result)
            self.assertNotIn('project_retrieve', [name for _, name, _ in self.calls])

    def test_lost_or_mismatched_retrieval_receipt_cannot_complete_or_replay(self):
        for fault in ('retrieveLost', 'retrieveWrongReceipt'):
            self.report_path = self.root / (fault + '.json'); self.calls.clear()
            with self.subTest(fault=fault), self.assertRaises(AssertionError): self.run_qualifier(fault)
            result = json.loads(self.report_path.read_text()); self.assertFalse(result['completed'])
            self.assertNotIn('failureRecovery', result)
            tools = [name for _, name, _ in self.calls]; index = tools.index('project_retrieve')
            self.assertEqual(1, tools.count('project_retrieve')); self.assertNotIn('project_inventory', tools[index + 1:])

    def test_legacy_and_incomplete_cleanup_receipts_cannot_authorize_verifier(self):
        report = self.run_qualifier()
        for defect in ('legacy', 'flag', 'tree', 'handle', 'native', 'birth', 'workerBirth', 'hostPid', 'helper'):
            damaged = copy.deepcopy(report)
            if defect == 'legacy': damaged.pop('lifetimePolicy')
            elif defect == 'flag': damaged['cleanupVerified'] = False
            elif defect == 'tree': damaged['sessions'][0]['cleanup']['launcherShutdown']['tree']['completed'] = False
            elif defect == 'handle': damaged['sessions'][0]['cleanup']['launcherShutdown']['tree']['exits'][0]['proof']['handleClosed'] = False
            elif defect == 'native': damaged['sessions'][0]['processId'] += 1
            elif defect == 'birth': damaged['sessions'][0]['handleCapture']['birthAndImageVerified'] = False
            elif defect == 'workerBirth': damaged['sessions'][0]['cleanup']['launcherShutdown']['workerExits'][0]['startedAtUtc'] = '1970-01-01T00:00:01Z'
            elif defect == 'hostPid': damaged['sessions'][0]['hostProcessId'] += 100
            else: damaged['seederLifetimes'][0]['terminal']['handleClosed'] = False
            with self.subTest(defect=defect), self.assertRaises(AssertionError): lifetime.verify_lifecycle(damaged)

    def run_companion(self):
        result_path = self.root / 'companion.json'
        open_project = verifier.open_qualified_project
        with patch.object(lifetime, 'ComponentOwnedScratch', side_effect=self.controller), \
             patch.object(verifier, 'open_qualified_project', side_effect=lambda *a: open_project(*a, sleep=lambda _: None)), \
             patch.dict(os.environ, {'USERNAME': 'test-user', 'COMPUTERNAME': 'TEST-HOST'}), \
             patch('sys.argv', ['verify', '--evidence', str(self.report_path),
                 '--evidence-sha256', hashlib.sha256(self.report_path.read_bytes()).hexdigest(),
                 '--host', str(self.binary), '--worker', str(self.binary), '--report', str(result_path)]):
            verifier.main()
        return json.loads(result_path.read_text())

    def audit_companion(self, result):
        source = json.loads(self.report_path.read_text())
        audit = {'sourceReportSha256': hashlib.sha256(self.report_path.read_bytes()).hexdigest()}
        auditor.audit(source, audit); auditor.audit_companion(source, result, audit)
        self.assertTrue(audit['exactLifecycleVerified'])

    def test_readonly_companion_closes_both_fresh_sessions_before_completion(self):
        self.run_qualifier(); initial_closes = self.close_count
        result = self.run_companion(); self.assertTrue(result['completed']); lifetime.verify_lifecycle(result)
        self.assertEqual(2, self.close_count - initial_closes)
        self.audit_companion(result)

    def test_companion_and_auditor_refuse_incomplete_compile_receipts(self):
        original = self.run_qualifier(); initial_serial = self.serial
        for phase in ('seeder', 'edit', 'recovery', 'scratch', 'hardware', 'scratchStatus', 'hardwareStatus', 'hiddenRecord'):
            damaged = copy.deepcopy(original)
            if phase in ('scratch', 'hardware', 'scratchStatus', 'hardwareStatus', 'hiddenRecord'):
                tool = 'tia_create_scratch' if phase.startswith('scratch') else 'change_apply'
                container = next(row['result'] for row in damaged['sessions'][0]['calls'] if row['tool'] == tool)
            else:
                container = damaged['seederConnect'] if phase == 'seeder' else damaged['edit']['applied'] if phase == 'edit' else damaged['recovery']
            if phase == 'scratchStatus': container['completed'] = False
            elif phase == 'hardwareStatus': container['state'] = 'failed'
            else: container['compile']['targets'][0]['messages'] = [{}]
            lifetime.sync_steps(damaged)
            if phase == 'hiddenRecord': damaged['steps'] = [step for step in damaged['steps'] if step['name'] != 'change_apply']
            self.report_path.write_text(json.dumps(damaged), encoding='utf-8')
            error = AssertionError if phase in ('scratchStatus', 'hardwareStatus', 'hiddenRecord') else RuntimeError
            with self.subTest(phase=phase), self.assertRaises(error): self.run_companion()
            self.assertEqual(initial_serial, self.serial)
            result = {}
            with self.assertRaises(error): auditor.audit(damaged, result)
            self.assertFalse(result['compilationEvidenceVerified'])

    def test_changed_checkpoint_blocks_companion_and_independent_audit(self):
        source = self.run_qualifier(); serial = self.serial
        Path(source['seederConnect']['checkpointArchive']).write_bytes(b'changed after successful run')
        with self.assertRaisesRegex(AssertionError, 'bytes changed'): self.run_companion()
        self.assertEqual(serial, self.serial)
        with self.assertRaisesRegex(AssertionError, 'bytes changed'): auditor.audit(source, {})

    def test_successful_but_wrong_compiler_targets_refuse_during_production_run(self):
        for phase in ('scratch', 'hardware', 'seeder', 'baseline', 'edit', 'recovery'):
            with self.subTest(phase=phase):
                case = EncoderLifecycleTests(); case.setUp()
                try:
                    case.target_fault = phase
                    with case.assertRaises((RuntimeError, AssertionError)): case.run_qualifier()
                    result = json.loads(case.report_path.read_text(encoding='utf-8'))
                    case.assertFalse(result['completed']); case.assertNotIn('qualificationChecksPassed', result)
                    case.assertTrue(result['cleanupVerified'])
                finally: case.doCleanups()

    def test_lost_baseline_compile_reply_blocks_edit_and_never_replays(self):
        with self.assertRaisesRegex(RuntimeError, 'TimeoutError'): self.run_qualifier('baselineLost')
        preparations = [args['kind'] for _, name, args in self.calls if name == 'change_prepare']
        self.assertEqual(['hardware_plug', 'plc_compile'], preparations)
        self.assertEqual(2, sum(name == 'change_apply' for _, name, _ in self.calls))
        self.assertNotIn('change_restore', [name for _, name, _ in self.calls])
        report = json.loads(self.report_path.read_text(encoding='utf-8'))
        self.assertFalse(report['completed']); self.assertNotIn('nativeBefore', report)

    def test_fresh_baseline_and_recovery_cannot_be_replaced_by_parameter_writes(self):
        source = self.run_qualifier()
        preparations = [args for _, name, args in self.calls if name == 'change_prepare']
        self.assertEqual(['hardware_plug', 'plc_compile', 'to_parameters', 'plc_compile'], [row['kind'] for row in preparations])
        for defect in ('missingBaseline', 'oldPolicy', 'sameValueBaseline', 'sameValueRecovery', 'mutatedBaseline', 'baselineAfterEdit'):
            damaged = copy.deepcopy(source); calls = damaged['sessions'][0]['calls']
            compiles = [row for row in calls if row['tool'] == 'change_prepare' and row['arguments']['kind'] == 'plc_compile']
            if defect == 'missingBaseline': damaged.pop('baselineCompile')
            if defect == 'oldPolicy': damaged['compilerTargetPolicy'] = 'encoder-exact-compiler-targets-v1'
            if defect == 'sameValueBaseline': compiles[0]['arguments']['kind'] = 'to_parameters'
            if defect == 'sameValueRecovery': compiles[1]['arguments']['kind'] = 'to_parameters'
            if defect == 'mutatedBaseline': damaged['baselineCompile']['applied']['stages'].append('mutation_applied')
            if defect == 'baselineAfterEdit':
                start = calls.index(compiles[0]); outcome = next(i for i in range(start, len(calls)) if calls[i]['tool'] == 'change_apply')
                moved = calls[start:outcome+1]; del calls[start:outcome+1]; calls.extend(moved)
            lifetime.sync_steps(damaged)
            with self.subTest(defect=defect), self.assertRaises((RuntimeError, AssertionError, KeyError)):
                qualifier.require_source_compiles(damaged)

    def test_retained_compiler_targets_bind_original_intents_inventory_and_phases(self):
        source = self.run_qualifier(); initial_serial = self.serial
        for defect in ('scratchTarget', 'editTarget', 'recoveryTarget', 'missingInventory', 'filteredInventory', 'wrongOwner',
                       'duplicatePlc', 'prepareSelector', 'repeatedPreparation', 'nativeSummary', 'seederBoundary',
                       'seederSummary', 'seederInventoryRequest', 'legacyPolicy'):
            damaged = copy.deepcopy(source); calls = damaged['sessions'][0]['calls']
            applies = [row for row in calls if row['tool'] == 'change_apply']
            inventory = next(row for row in calls if row['tool'] == 'project_inventory')
            if defect == 'scratchTarget': next(row for row in calls if row['tool'] == 'tia_create_scratch')['result']['compile']['targets'][0]['target'] = '/unrelated/plc'
            if defect == 'editTarget':
                applies[2]['result']['compile']['targets'][0]['target'] = '/unrelated/plc'
                damaged['edit']['applied'] = copy.deepcopy(applies[2]['result'])
            if defect == 'recoveryTarget':
                applies[3]['result']['compile']['targets'][0]['target'] = '/unrelated/plc'
                damaged['recovery']['compile'] = copy.deepcopy(applies[3]['result']['compile'])
            if defect == 'missingInventory': calls.remove(inventory)
            if defect == 'filteredInventory': inventory['arguments']['plcPath'] = PLC
            if defect == 'wrongOwner': inventory['result']['project']['path'] += '.foreign'
            if defect == 'duplicatePlc': inventory['result']['plcs'].append(copy.deepcopy(inventory['result']['plcs'][0]))
            if defect == 'prepareSelector': next(row for row in calls if row['tool'] == 'change_prepare')['arguments']['plcPath'] = '/unrelated/plc'
            if defect == 'repeatedPreparation':
                preparations = [row for row in calls if row['tool'] == 'change_prepare']; preparations[1]['result']['changeId'] = preparations[0]['result']['changeId']
            if defect == 'nativeSummary': damaged['nativeBefore']['project']['path'] += '.foreign'
            packet = next(row for row in damaged['seederLifetimes'] if row['mode'] == 'connect')
            if defect == 'seederBoundary': packet['endCallCount'] = packet['startCallCount']
            if defect == 'seederSummary': damaged['seederConnect']['compile']['targets'][0]['targetPath'] = '/projects/Other/devices/Station'; damaged['seederConnect']['compile']['requiredTargets'] = ['/projects/Other/devices/Station']
            if defect == 'seederInventoryRequest': calls[packet['endCallCount']]['arguments']['devicePath'] = STATION
            if defect == 'legacyPolicy': damaged.pop('compilerTargetPolicy')
            lifetime.sync_steps(damaged)
            self.report_path.write_text(json.dumps(damaged), encoding='utf-8')
            with self.subTest(defect=defect), self.assertRaises((RuntimeError, AssertionError)): self.run_companion()
            self.assertEqual(initial_serial, self.serial)
            with self.assertRaises((RuntimeError, AssertionError)): auditor.audit(damaged, {})

    def test_companion_target_selection_is_independently_audited(self):
        self.run_qualifier(); companion = self.run_companion()
        for defect in ('inventoryOwner', 'selector', 'nativeOwner', 'parameterObject'):
            damaged = copy.deepcopy(companion); calls = damaged['sessions'][0]['calls']
            if defect == 'inventoryOwner': next(row for row in calls if row['tool'] == 'project_inventory')['result']['project']['path'] += '.foreign'
            if defect == 'selector': next(row for row in calls if row['tool'] == 'technology_read')['arguments']['plcPath'] = '/unrelated/plc'
            if defect == 'nativeOwner': damaged['copies']['recovered']['nativeReadback']['plcPath'] = '/unrelated/plc'
            if defect == 'parameterObject': next(row for row in calls if row['tool'] == 'engineering_read')['result']['technologyObjects'][0]['path'] = '/unrelated/technology/encoder'
            lifetime.sync_steps(damaged)
            with self.subTest(defect=defect), self.assertRaises((AssertionError, RuntimeError)): self.audit_companion(damaged)

    def test_retained_seeder_output_is_pinned_before_later_auditing(self):
        source = self.run_qualifier(); initial_serial = self.serial
        packet = next(row for row in source['seederLifetimes'] if row['mode'] == 'connect')
        path = Path(packet['stdout']); path.write_bytes(path.read_bytes() + b' ')
        with self.assertRaisesRegex(AssertionError, 'bytes changed'): self.run_companion()
        self.assertEqual(initial_serial, self.serial)
        with self.assertRaisesRegex(AssertionError, 'bytes changed'): auditor.audit(source, {})

    def test_hardware_compiler_scope_requires_independent_original_inventory(self):
        source = self.run_qualifier(); serial = self.serial
        for defect in ('missingInventory', 'wrongProject', 'unrelatedTarget', 'legacyLabels'):
            damaged = copy.deepcopy(source)
            calls = damaged['sessions'][0]['calls']
            compiled = next(row['result']['compile'] for row in calls if row['tool'] == 'change_apply')
            inventory = next(row for row in calls if row['tool'] == 'device_inventory')
            if defect == 'missingInventory': calls.remove(inventory)
            if defect == 'wrongProject': inventory['result']['snapshot']['projectPath'] += '.other'
            if defect == 'unrelatedTarget':
                compiled['requiredTargets'] = ['/projects/Other/devices/Station']
                compiled['targets'][0]['targetPath'] = compiled['requiredTargets'][0]
            if defect == 'legacyLabels':
                compiled.pop('targetIdentityPolicy'); compiled.pop('requiredTargets'); compiled['targets'][0].pop('targetPath')
            lifetime.sync_steps(damaged)
            self.report_path.write_text(json.dumps(damaged), encoding='utf-8')
            with self.subTest(defect=defect), self.assertRaises((RuntimeError, AssertionError)): self.run_companion()
            self.assertEqual(serial, self.serial)
            with self.assertRaises((RuntimeError, AssertionError)): auditor.audit(damaged, {})

    def test_known_lock_retry_retains_failures_and_passes_independent_audit(self):
        self.run_qualifier(); self.fault = 'knownLock'
        result = self.run_companion(); self.assertTrue(result['completed'])
        self.assertEqual([2, 2], [row['openAttempts'] for row in result['copies'].values()])
        self.assertEqual(2, sum(row['isError'] for row in result['steps']))
        self.audit_companion(result)
        for defect in ('wrongOwner', 'missingEmptyCheck', 'hiddenFailure'):
            damaged = copy.deepcopy(result)
            if defect == 'wrongOwner': damaged['lockOwner']['user'] = 'different-user'
            elif defect == 'missingEmptyCheck':
                calls = damaged['sessions'][0]['calls']
                index = next(i for i, row in enumerate(calls) if row['status'] == 'failed')
                calls[index + 1]['result']['projects'] = [{'path': 'unexpected'}]
                lifetime.sync_steps(damaged)
            else: damaged['steps'] = [row for row in damaged['steps'] if not row['isError']]
            with self.subTest(defect=defect), self.assertRaises(AssertionError): self.audit_companion(damaged)

    def test_unknown_open_failure_and_companion_cleanup_failure_cannot_complete(self):
        self.run_qualifier()
        for fault in ('unknownLock', 'hostExit'):
            result_path = self.root / 'companion.json'
            if result_path.exists(): result_path.unlink()
            self.calls.clear(); self.fault = fault
            with self.subTest(fault=fault), self.assertRaises(Exception): self.run_companion()
            result = json.loads(result_path.read_text()); self.assertFalse(result['completed'])
            self.assertEqual(1, sum(name == 'project_open' for _, name, _ in self.calls))

    def test_helper_constructor_retains_partial_process_and_holds_unknown_start(self):
        class UnknownHold(BaseException): pass
        for case in ('success', 'retained', 'missing', 'unknown'):
            class Constructor:
                def __init__(self, *a, **kw):
                    if case in ('success', 'retained'): self._handle = object()
                    if case == 'retained': raise RuntimeError('after spawn')
                    if case == 'missing': raise FileNotFoundError('before spawn')
                    if case == 'unknown': raise KeyboardInterrupt()
            with self.subTest(case=case), patch.object(lifetime.subprocess, 'Popen', Constructor), \
                 patch.object(lifetime, 'hold_unknown_startup', side_effect=UnknownHold) as hold:
                if case in ('success', 'retained'):
                    process, error = lifetime.launch_helper(['synthetic'])
                    self.assertIsNotNone(process._handle)
                    self.assertEqual(case == 'retained', isinstance(error, RuntimeError))
                    hold.assert_not_called()
                elif case == 'missing':
                    with self.assertRaises(FileNotFoundError): lifetime.launch_helper(['synthetic'])
                    hold.assert_not_called()
                else:
                    with self.assertRaises(UnknownHold): lifetime.launch_helper(['synthetic'])
                    hold.assert_called_once()


if __name__ == '__main__': unittest.main()
