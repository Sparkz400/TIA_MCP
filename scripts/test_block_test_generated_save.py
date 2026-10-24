"""Generated-state save sequencing and exact per-case authority; synthetic SDK evidence."""
import copy
import pathlib
import unittest

from block_test_runtime import McpRuntimeBackend, RuntimeCleanupUncertain, save_generated_scratch_project
from block_test_program import generate_program, inspect_interface
from run_block_tests import CaseSessionAudit, load_suites
from test_block_test_runtime import Simulator, Clock
from test_compilation_evidence import compile_only_records


class GeneratedStateSaveTests(unittest.TestCase):
    def setUp(self):
        self.project = str(pathlib.Path('artifacts/generated-save-test.ap21').resolve())
        self.plc = '/projects/P/devices/S/items/CPU/plc/PLC'
        self.download = dict(project={'path': self.project}, plcPath=self.plc, processId=7)
        self.prepared, self.applied = compile_only_records(self.plc, self.project, 7)
        self.calls = []

    def call(self, name, arguments):
        self.calls.append((name, copy.deepcopy(arguments)))
        return copy.deepcopy(self.prepared if name == 'change_prepare' else self.applied)

    def test_explicit_checkpoint_compile_save_is_bound_to_original_preview(self):
        result = save_generated_scratch_project(self.call, self.download)
        self.assertEqual({'prepared': self.prepared, 'applied': self.applied}, result)
        self.assertEqual(('change_prepare', dict(kind='plc_compile', plcPath=self.plc, payload={})), self.calls[0])
        self.assertTrue(all(self.calls[1][1][k] is True for k in ('checkpointSave', 'compile', 'save')))
        self.assertEqual(self.prepared['previewHash'], self.calls[1][1]['previewHash'])

    def test_missing_download_identity_never_prepares_a_save(self):
        for field, value in (('plcPath', None), ('plcPath', ''), ('processId', True), ('processId', 0),
                             ('project', {'path': 'relative.ap21'}), ('project', {'path': self.project + '.txt'})):
            with self.subTest(field=field, value=value), self.assertRaises(RuntimeError):
                save_generated_scratch_project(self.call, {**self.download, field: value})
        self.assertEqual([], self.calls)

    def test_wrong_preparation_never_reaches_apply(self):
        original = copy.deepcopy(self.prepared)
        for fault in ('project', 'plc', 'payload', 'kind', 'state', 'process', 'boolProcess', 'id', 'hash'):
            self.prepared = copy.deepcopy(original)
            self.calls.clear()
            if fault == 'project': self.prepared['preview']['project']['path'] += '-other'
            if fault == 'plc': self.prepared['preview']['effects'][0]['plcPath'] += '-other'
            if fault == 'payload': self.prepared['preview']['payload'] = {'source': 'change'}
            if fault == 'kind': self.prepared['preview']['kind'] = 'source_upsert'
            if fault == 'state': self.prepared['state'] = 'applied'
            if fault == 'process': self.prepared['processId'] = 8
            if fault == 'boolProcess': self.prepared['processId'] = True
            if fault == 'id': self.prepared['preview']['changeId'] += '-other'
            if fault == 'hash': self.prepared['previewHash'] = ''
            with self.subTest(fault=fault), self.assertRaises(RuntimeError):
                save_generated_scratch_project(self.call, self.download)
            self.assertEqual(['change_prepare'], [n for n,_ in self.calls])

    def test_incomplete_save_or_compile_evidence_fails_without_retry(self):
        original = copy.deepcopy(self.applied)
        for fault in ('save', 'error', 'truncated', 'changedPreview'):
            self.applied = copy.deepcopy(original)
            self.calls.clear()
            if fault == 'save': self.applied['stages'].remove('changed_project_saved')
            if fault == 'error': self.applied['compile']['targets'][0]['errorCount'] = 1
            if fault == 'truncated': self.applied['compile']['targets'][0]['messagesTruncated'] = True
            if fault == 'changedPreview': self.applied['preview']['project']['path'] += '-other'
            with self.subTest(fault=fault), self.assertRaises(RuntimeError):
                save_generated_scratch_project(self.call, self.download)
            self.assertEqual(['change_prepare', 'change_apply'], [n for n,_ in self.calls])

    def audit(self, enabled=True, **overrides):
        owner = self
        class Client:
            def request(self, method, params):
                name, args = params['name'], params['arguments']
                if name == 'simulation_download': result = {**owner.download, **overrides}
                else: result = owner.call(name, args)
                return {'structuredContent': result}
        return CaseSessionAudit(Client(), [], lambda: None, self.project, allow_generated_state_save=enabled)

    def test_case_save_requires_opt_in_exact_download_and_single_use(self):
        for enabled in (False, True):
            audit = self.audit(enabled)
            with self.assertRaises(ValueError):
                audit('change_prepare', dict(kind='plc_compile', plcPath=self.plc, payload={}))
            audit('simulation_download', {})
            if not enabled:
                with self.assertRaises(ValueError): save_generated_scratch_project(audit, self.download)
                continue
            for args in (dict(kind='source_upsert', plcPath=self.plc, payload={}),
                         dict(kind='plc_compile', plcPath=self.plc + '-other', payload={})):
                with self.assertRaises(ValueError): audit('change_prepare', args)
            save_generated_scratch_project(audit, self.download)
            apply_args = self.calls[-1][1]
            with self.assertRaises(ValueError): audit('change_apply', apply_args)
            audit('simulation_download', {})
            with self.assertRaises(ValueError): save_generated_scratch_project(audit, self.download)
        with self.assertRaises(ValueError):
            self.audit(project={'path': self.project + '-other'})('simulation_download', {})

    def test_case_apply_refuses_foreign_preview_and_non_save_flags(self):
        audit = self.audit()
        audit('simulation_download', {})
        audit('change_prepare', dict(kind='plc_compile', plcPath=self.plc, payload={}))
        valid = dict(changeId=self.prepared['changeId'], previewHash=self.prepared['previewHash'],
                     idempotencyKey='a' * 32, checkpointSave=True, compile=True, save=True)
        for key, value in (('changeId', 'foreign'), ('previewHash', 'foreign'), ('save', False),
                           ('checkpointSave', 1), ('compile', False), ('idempotencyKey', '')):
            with self.subTest(key=key), self.assertRaises(ValueError): audit('change_apply', {**valid, key:value})
        audit('change_apply', valid)

    def backend(self, fail_save=False):
        suite = load_suites(['runtime-boolean'])[0]
        program = generate_program(inspect_interface(suite['interface']))
        native = Simulator(program)
        state = {'dirty': False}
        def call(name, args):
            if name == 'simulation_download': state['dirty'] = True
            if name == 'change_apply':
                if fail_save: raise RuntimeError('Save outcome unknown')
                state['dirty'] = False
            if name == 'simulation_provision_scratch_credential' and state['dirty']:
                raise RuntimeError('UnsavedProject')
            return native(name, args)
        clock = Clock()
        backend = McpRuntimeBackend(call, program, {'sourceRevision':'known'}, 'credential.json',
            clock=clock.clock, sleep=clock.sleep, timeout_ms=100, poll_ms=20,
            provision_runtime_credential=True, card_verifier=lambda value: {'programRevision':value['programRevision']})
        return backend, native

    def test_dirty_card_generation_is_saved_before_provisioning(self):
        backend, native = self.backend()
        evidence = backend.start()
        self.assertIn('generatedProjectSave', evidence)
        names = [n for n,_ in native.calls]
        self.assertLess(names.index('simulation_download'), names.index('change_apply'))
        self.assertLess(names.index('change_apply'), names.index('simulation_provision_scratch_credential'))
        self.assertTrue(backend.close()['completed'])

    def test_failed_save_stops_provisioning_and_retains_uncertainty_after_cleanup(self):
        backend, native = self.backend(fail_save=True)
        with self.assertRaisesRegex(RuntimeError, 'Save outcome unknown'): backend.start()
        self.assertNotIn('simulation_provision_scratch_credential', [n for n,_ in native.calls])
        self.assertNotIn('sim_state', [n for n,_ in native.calls])
        with self.assertRaises(RuntimeCleanupUncertain): backend.close()
        self.assertEqual('sim_release', native.calls[-1][0])
        with self.assertRaisesRegex(RuntimeError, 'retried'): backend.start()


if __name__ == '__main__':
    unittest.main()
