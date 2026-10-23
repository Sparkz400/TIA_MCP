"""Closed scratch admission and producer tests with synthetic files and sessions."""
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import safety_lifecycle
import safety_scratch_admission as admission
import smoke_safety_direct as direct
from smoke_mcp import ROOT
from test_compilation_evidence import clean_compile
from test_safety_lifecycle import terminal_session


class SafetyScratchAdmissionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=ROOT/'artifacts'); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name); self.artifacts = self.root/'artifacts'
        self.project = self.artifacts/'scratch/TiaMcpScratch_test/TiaMcpScratch_test.ap21'
        self.project.parent.mkdir(parents=True)
        self.project.write_bytes(b'synthetic project')
        self.sidecar = self.project.parent/'database.bin'; self.sidecar.write_bytes(b'synthetic database')
        (self.project.parent/'empty').mkdir()
        self.source_path = self.artifacts/'source.json'
        session = terminal_session(1)
        session['calls'] = [{'tool': 'tia_create_scratch', 'arguments': {'cpuTypeIdentifier': admission.CPU},
            'status': 'passed', 'result': {'completed': True, 'processId': 1, 'project': {'path': str(self.project)}, 'compile': clean_compile('/projects/Fixture/devices/Station/items/CPU/plc/PLC')}}]
        session['calls'].append({'tool': 'project_inventory', 'arguments': {}, 'status': 'passed', 'result': {
            'project': {'path': str(self.project)}, 'processId': 1, 'plcs': [{'path': '/projects/Fixture/devices/Station/items/CPU/plc/PLC'}]}})
        self.source = {'completed': True, 'cleanupVerified': True, 'lifetimePolicy': safety_lifecycle.POLICY,
            'sessions': [session], 'projectPath': str(self.project),
            'closedScratch': admission.closed_snapshot(self.project, self.artifacts, self.root)}
        self.write()

    def write(self):
        safety_lifecycle.sync_steps(self.source)
        self.source_path.write_text(json.dumps(self.source), encoding='utf-8')
        self.digest = hashlib.sha256(self.source_path.read_bytes()).hexdigest()

    def load(self):
        return admission.load_source(self.project, self.source_path, self.digest, self.artifacts, self.root)

    def args(self, output, reuse=True):
        args = ['direct', '--report', str(output), '--artifacts', str(self.artifacts)]
        return args + (['--project', str(self.project), '--source-report', str(self.source_path),
                        '--source-report-sha256', self.digest] if reuse else [])

    def client(self):
        from test_safety_qualification import FakeSafetyClient, PLC
        return FakeSafetyClient({'tia_start': {'processId': 2}, 'tia_detach': {},
            'tia_create_scratch': {'completed': True, 'compile': clean_compile('/projects/Fixture/devices/Station/items/CPU/plc/PLC'), 'processId': 2, 'project': {'path': str(self.project)}},
            'project_open': {'project': {'path': str(self.project)}, 'processId': 2},
            'project_inventory': {'project': {'path': str(self.project)}, 'processId': 2, 'plcs': [{'path': PLC}]},
            'safety_inspect': {'runtimeGroups': [], 'programSignatures': []},
            'software_inspect': {'blocks': [], 'completeness': {'scopeComplete': True}}})

    def run_direct(self, client, output, reuse=True):
        with patch.object(direct, 'ROOT', self.root), patch('sys.argv', self.args(output, reuse)), \
                patch.object(safety_lifecycle, 'new_session', side_effect=client.as_session):
            direct.main()

    def test_complete_tree_detects_changed_added_missing_files_and_empty_directories(self):
        packet = self.load(); self.assertFalse(packet['sourceAuthenticityVerified'])
        self.sidecar.write_bytes(b'changed')
        with self.assertRaisesRegex(AssertionError, 'files differ'): self.load()
        self.sidecar.write_bytes(b'synthetic database')
        self.sidecar.unlink()
        with self.assertRaisesRegex(AssertionError, 'files differ'): self.load()
        self.sidecar.write_bytes(b'synthetic database')
        extra = self.project.parent/'extra'; extra.write_bytes(b'extra')
        with self.assertRaisesRegex(AssertionError, 'files differ'): self.load()
        extra.unlink(); (self.project.parent/'empty').rmdir()
        with self.assertRaisesRegex(AssertionError, 'files differ'): self.load()
        (self.project.parent/'empty').mkdir()
        admission.recheck(packet, self.artifacts, self.root, before_open=True)

    def test_invalid_source_refuses_before_host_creation(self):
        original = copy.deepcopy(self.source)
        for defect in ('hash', 'incomplete', 'lifetime', 'origin', 'cpu', 'process', 'snapshot', 'projection'):
            self.source = copy.deepcopy(original)
            create = self.source['sessions'][0]['calls'][0]
            if defect == 'incomplete': self.source['completed'] = False
            if defect == 'lifetime': self.source['cleanupVerified'] = False
            if defect == 'origin': create['result']['project']['path'] += '.other'
            if defect == 'cpu': create['arguments']['cpuTypeIdentifier'] = 'Other CPU'
            if defect == 'process': create['result']['processId'] = True
            if defect == 'snapshot': del self.source['closedScratch']
            self.write()
            if defect == 'hash': self.digest = '0'*64
            if defect == 'projection':
                self.source['steps'] = []; self.source_path.write_text(json.dumps(self.source))
                self.digest = hashlib.sha256(self.source_path.read_bytes()).hexdigest()
            with self.subTest(defect=defect), patch.object(direct, 'ROOT', self.root), \
                    patch('sys.argv', self.args(self.artifacts/'result.json')), patch.object(safety_lifecycle, 'new_session') as factory:
                with self.assertRaises((AssertionError, KeyError)): direct.main()
                factory.assert_not_called()

    def test_missing_options_refuse_before_host_creation(self):
        with patch.object(direct, 'ROOT', self.root), patch('sys.argv', ['direct', '--project', str(self.project)]), \
                patch.object(safety_lifecycle, 'new_session') as factory:
            with self.assertRaises(SystemExit): direct.main()
            factory.assert_not_called()

    def test_startup_substitution_stops_before_open(self):
        client = self.client(); start = client.start
        def changed():
            value = start(); self.sidecar.write_bytes(b'substitution'); return value
        client.start = changed
        output = self.artifacts/'startup.json'
        with self.assertRaisesRegex(AssertionError, 'changed before opening'): self.run_direct(client, output)
        self.assertNotIn('project_open', client.calls)
        self.assertFalse(json.loads(output.read_text())['completed'])

    def test_wrong_returned_owner_and_close_time_report_changes_fail(self):
        for defect in ('openPath', 'openProcess', 'inventoryPath', 'inventoryProcess', 'closeReport'):
            self.write(); client = self.client(); output = self.artifacts/(defect+'.json')
            if defect == 'openPath': client.replies['project_open']['project']['path'] += '.other'
            if defect == 'openProcess': client.replies['project_open']['processId'] = True
            if defect == 'inventoryPath': client.replies['project_inventory']['project']['path'] += '.other'
            if defect == 'inventoryProcess': client.replies['project_inventory']['processId'] = 3
            if defect == 'closeReport':
                close = client.close
                def changed():
                    close(); self.source_path.write_bytes(b'changed source')
                client.close = changed
            with self.subTest(defect=defect), self.assertRaises(AssertionError): self.run_direct(client, output)
            self.assertFalse(json.loads(output.read_text())['completed'])
            if defect != 'closeReport': self.assertNotIn('safety_inspect', client.calls)

    def test_successful_fresh_and_reused_runs_publish_closed_snapshots_with_lineage(self):
        for reuse in (False, True, True):
            client = self.client(); output = self.artifacts/('result'+str(len(list(self.artifacts.glob('result*'))))+'.json')
            self.run_direct(client, output, reuse)
            report = json.loads(output.read_text()); self.assertTrue(report['completed'])
            self.assertEqual(admission.closed_snapshot(self.project, self.artifacts, self.root), report['closedScratch'])
            self.source_path = output; self.digest = hashlib.sha256(output.read_bytes()).hexdigest()
            packet = self.load(); admission.recheck(packet, self.artifacts, self.root, before_open=True)
        self.assertEqual(3, len(packet['inputs']))

    def test_snapshot_failure_cannot_leave_completed_report(self):
        client = self.client(); close = client.close
        def changed():
            close(); self.project.unlink()
        client.close = changed
        output = self.artifacts/'snapshot-failure.json'
        with self.assertRaisesRegex(AssertionError, 'exact generated'): self.run_direct(client, output)
        report = json.loads(output.read_text()); self.assertFalse(report['completed'])
        self.assertIn('scratchAdmissionFailure', report)

    def test_wrong_baseline_compile_target_blocks_new_run_and_retained_reuse(self):
        client = self.client(); client.replies['tia_create_scratch']['compile'] = clean_compile('/other/plc')
        output = self.artifacts/'wrong-baseline.json'
        with self.assertRaisesRegex(RuntimeError, 'required selection'): self.run_direct(client, output, reuse=False)
        self.assertNotIn('safety_inspect', client.calls)
        self.source['sessions'][0]['calls'][0]['result']['compile'] = clean_compile('/other/plc'); self.write()
        with self.assertRaisesRegex(RuntimeError, 'required selection'): self.load()


if __name__ == '__main__':
    unittest.main()
