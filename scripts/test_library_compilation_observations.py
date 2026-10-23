import copy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import validate_library_observations as validation
import test_library_operation_observations as operation_fixtures

FIXTURE = validation.ROOT / 'fixtures/libraries/observation-contracts'


class LibraryCompilationObservations(unittest.TestCase):
    def setUp(self):
        def read(name): return json.loads((FIXTURE / (name + '.json')).read_bytes())
        self.legacy, self.modern, self.hmi = (read(name) for name in ('compiler-legacy', 'compiler-device', 'compiler-hmi'))
        self.lock = read('lock')
        self.operations = operation_fixtures.LibraryOperationObservations(); self.operations.setUp()

    def validate(self, value, kind='compiler'):
        return validation.validate_bytes(json.dumps(value).encode(), kind)

    def reject(self, value, kind='compiler'):
        with self.assertRaises(ValueError): self.validate(value, kind)

    def change(self, modern=True, state='applied', result=True, compilation=True):
        p = self.operations.plan('library_instantiate')
        p.update(projectPath='D:/Synthetic/Project.ap21', compileScope=['Station'] if modern else self.legacy_labels())
        if modern: p['compileTargetPaths'] = self.modern['requiredTargets'][:]
        else: p.pop('compileTargetPaths', None)
        value = self.operations.response(p, self.operations.result('library_instantiate') if result else None, state)
        if state == 'applied': value['journal']['engineeringVerified'] = True
        if compilation: value['journal']['compile'] = copy.deepcopy(self.modern if modern else self.legacy)
        return value

    def legacy_labels(self): return [row['target'] for row in self.legacy['targets']]

    def test_original_compiler_shapes_do_not_grant_native_authority(self):
        for value in (self.modern, self.legacy, self.hmi):
            result = self.validate(value)
            self.assertTrue(result['compilerReportsSuccess'])
            self.assertTrue(result['compilerMessagesComplete'])
            self.assertFalse(result['nativeExecuted'])
            self.assertFalse(result['qualificationGranted'])
        self.assertEqual('device-path-v1', self.validate(self.modern)['compilerTargetIdentity'])
        self.assertEqual('target-labels', self.validate(self.legacy)['compilerTargetIdentity'])

    def test_zero_summary_never_hides_error_or_unknown_diagnostic_states(self):
        for state in ('Error', 'Unknown', 'Cancelled', 'FutureState'):
            for location in ('target', 'message'):
                value = copy.deepcopy(self.legacy)
                row = value['targets'][0] if location == 'target' else value['targets'][0]['messages'][0]
                row['state'] = state
                result = self.validate(value)
                self.assertFalse(result['compilerReportsSuccess'])
                self.assertTrue(result['compilerMessagesComplete'])
        value = copy.deepcopy(self.hmi); value['errorCount'] = value['targets'][0]['errorCount'] = 1
        self.assertFalse(self.validate(value)['compilerReportsSuccess'])

    def test_summary_counts_bind_target_counts_without_counting_message_severity(self):
        for field in ('errorCount', 'warningCount'):
            value = copy.deepcopy(self.modern); value[field] += 1; self.reject(value)
            for wrong in (-1, True, 1.0, '1', 2147483648):
                value = copy.deepcopy(self.modern); value[field] = wrong; self.reject(value)
        # Multiple ancestor Warning messages can describe a single native warning.
        self.assertGreater(len([m for m in self.legacy['targets'][0]['messages'] if m['state'] == 'Warning']), self.legacy['warningCount'])
        self.validate(self.legacy)

    def test_flattened_message_depth_has_valid_preorder(self):
        for depth in (1, 2, -1, 65, True):
            value = copy.deepcopy(self.hmi)
            value['targets'][0]['messages'] = [{'state': 'Information', 'description': None, 'path': None, 'depth': depth}]
            self.reject(value)
        value['targets'][0]['messages'] = [{'state': 'Information', 'description': '', 'path': None, 'depth': d} for d in (0, 1, 2, 0, 1)]
        self.validate(value)
        value['targets'][0]['messages'][1]['depth'] = 3; self.reject(value)

    def test_truncation_preserves_depth_and_count_boundary_uncertainty(self):
        value = copy.deepcopy(self.hmi); row = value['targets'][0]
        row['messages'] = [{'state': 'Information', 'description': '', 'path': None, 'depth': 0} for _ in range(500)]
        self.assertTrue(self.validate(value)['compilerReportsSuccess'])
        row['messagesTruncated'] = True
        self.assertFalse(self.validate(value)['compilerReportsSuccess'])
        self.assertFalse(self.validate(value)['compilerMessagesComplete'])
        row['messages'].pop(); self.reject(value)
        row['messages'] = [{'state': 'Information', 'description': '', 'path': None, 'depth': d} for d in range(65)]
        self.validate(value)
        row['messagesTruncated'] = False; self.reject(value)
        row['messages'] *= 8; self.reject(value)

    def test_exact_device_paths_allow_equal_labels_but_never_duplicate_identity(self):
        value = copy.deepcopy(self.modern)
        other = copy.deepcopy(value['targets'][0]); other['targetPath'] = '/projects/Synthetic/ungrouped/Station'
        value['targets'].append(other); value['requiredTargets'].append(other['targetPath']); value['warningCount'] *= 2
        self.validate(value)
        value['targets'][1]['targetPath'] = value['targets'][0]['targetPath']; self.reject(value)
        value = copy.deepcopy(self.legacy); value['targets'] *= 2; value['warningCount'] *= 2; self.reject(value)

    def test_declared_device_coverage_and_route_cannot_be_fabricated(self):
        for field in ('requiredTargets', 'targetIdentityPolicy', 'scopeComplete'):
            value = copy.deepcopy(self.modern); del value[field]; self.reject(value)
        value = copy.deepcopy(self.modern); value['requiredTargets'].append('/projects/Synthetic/devices/Other'); self.reject(value)
        value = copy.deepcopy(self.modern); value['requiredTargets'] *= 2; self.reject(value)
        value = copy.deepcopy(self.hmi); value['runtimeVerified'] = True; self.reject(value)
        value = copy.deepcopy(self.hmi); value['targets'] *= 2; self.reject(value)

    def test_unknown_fields_and_partial_diagnostics_do_not_become_complete(self):
        for field in ('warningCount', 'state', 'messages', 'messagesTruncated'):
            value = copy.deepcopy(self.legacy); del value['targets'][0][field]; self.reject(value)
        value = copy.deepcopy(self.legacy); value['error'] = None; self.reject(value)
        value = copy.deepcopy(self.legacy); value['targets'][0]['messages'][0]['children'] = []; self.reject(value)

    def test_applied_project_requires_its_compiler_result(self):
        self.reject(self.change(compilation=False), 'compiler-change')
        value = self.change(state='prepared', result=False, compilation=False)
        self.assertFalse(self.validate(value, 'compiler-change')['compilerObservationValidated'])
        value['journal']['compile'] = None; self.reject(value, 'compiler-change')

    def test_prepared_device_path_and_name_pairs_bind_the_result(self):
        value = self.change()
        self.assertEqual('prepared-device-paths', self.validate(value, 'compiler-change')['compilerScopeBinding'])
        value['journal']['compile']['targets'][0]['target'] = 'Other'; self.reject(value, 'compiler-change')
        value = self.change(); value['journal']['compile'] = copy.deepcopy(self.legacy); self.reject(value, 'compiler-change')
        value = self.change(); value['journal']['compile']['targets'][0]['targetPath'] += 'Other'; self.reject(value, 'compiler-change')
        value = self.change(modern=False)
        self.assertEqual('prepared-target-labels', self.validate(value, 'compiler-change')['compilerScopeBinding'])

    def test_failed_compiler_remains_evidence_for_recovery_not_applied_success(self):
        value = self.change(state='recovery_required')
        value['journal']['compile']['targets'][0]['messages'][0]['state'] = 'Error'
        result = self.validate(value, 'compiler-change')
        self.assertFalse(result['compilerReportsSuccess'])
        self.assertEqual('recovery_required', value['state'])
        value['state'] = value['journal']['state'] = 'applied'; self.reject(value, 'compiler-change')
        value['state'] = value['journal']['state'] = 'recovery_required'
        value['journal']['engineeringVerified'] = True; self.reject(value, 'compiler-change')

    def test_applied_verification_flag_is_explicit_boolean_and_scope_consistent(self):
        for wrong in (False, None, 'true', 1):
            value = self.change(); value['journal']['engineeringVerified'] = wrong
            self.reject(value, 'compiler-change')
        value = self.change(); del value['journal']['engineeringVerified']; self.reject(value, 'compiler-change')

    def test_recovered_unknown_state_is_not_promoted_by_successful_compiler(self):
        original = self.change(state='recovery_required')
        value = {'schemaVersion': 1, 'changeId': original['changeId'], 'state': 'outcome_unknown',
                 'journal': original['journal'], 'automaticRetryAllowed': False, 'recoveredFromDisk': True, 'note': 'Synthetic'}
        self.assertTrue(self.validate(value, 'compiler-change')['compilerReportsSuccess'])
        self.assertEqual('outcome_unknown', value['state'])

    def test_global_operation_can_have_no_compiler_without_becoming_engineering_verified(self):
        plan = self.operations.plan('library_create')
        for field in ('projectPath', 'compileScope', 'compileTargetPaths'): plan.pop(field, None)
        value = self.operations.response(plan, self.operations.result('library_create'))
        value['journal']['engineeringVerified'] = False
        result = self.validate(value, 'compiler-change')
        self.assertIsNone(result['compilerReportsSuccess'])
        value['journal']['engineeringVerified'] = True; self.reject(value, 'compiler-change')

    def test_classic_and_unified_bind_different_compiler_scopes(self):
        for family in ('classic', 'unified'):
            plan = self.operations.hmi('tag_create' if family == 'unified' else 'instantiate', family)
            plan['projectPath'] = 'D:/Synthetic/Project.ap21'
            plan['payload']['target']['hmiPath'] = '/devices/Synthetic/items/HMI/hmi/Synthetic'
            for field in ('compileScope', 'compileTargetPaths'): plan.pop(field, None)
            value = self.operations.response(plan, state='recovery_required')
            value['journal']['compile'] = copy.deepcopy(self.hmi)
            if family == 'classic': value['journal']['compile']['targets'][0]['target'] = plan['payload']['target']['hmiPath']
            self.assertEqual('hmi-target', self.validate(value, 'compiler-change')['compilerScopeBinding'])
            value['journal']['compile']['targets'][0]['target'] += '/wrong'; self.reject(value, 'compiler-change')

    def test_lock_receipt_checks_compilation_without_claiming_independent_scope(self):
        value = copy.deepcopy(self.lock)
        value.update(projectPath='D:/Synthetic/Project.ap21', target=None, compilation=copy.deepcopy(self.modern))
        self.assertEqual('lock-declared-targets', self.validate(value, 'compiler-lock')['compilerScopeBinding'])
        value['compilation']['targets'][0]['state'] = 'Error'; self.reject(value, 'compiler-lock')
        value['compilation'] = None; self.reject(value, 'compiler-lock')
        value['projectPath'] = None
        self.assertFalse(self.validate(value, 'compiler-lock')['compilerObservationValidated'])

    def test_original_cli_bytes_and_legacy_modes_remain_unchanged(self):
        self.operations.validate(self.operations.change, 'change')
        self.operations.validate(self.lock, 'lock')
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'compile.json'
            raw = b'\xef\xbb\xbf' + json.dumps(self.modern).encode(); path.write_bytes(raw)
            args = [sys.executable, '-B', str(validation.ROOT / 'scripts/validate_library_observations.py'), '--kind', 'compiler',
                    '--path', str(path), '--expected-sha256', hashlib.sha256(raw).hexdigest()]
            result = subprocess.run(args, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertEqual(raw, path.read_bytes())
            path.write_bytes(raw + b' ')
            self.assertEqual(1, subprocess.run(args, capture_output=True).returncode)


if __name__ == '__main__':
    unittest.main()
