import copy
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest

import validate_library_observations as validation


class LibraryChangeObservations(unittest.TestCase):
    def setUp(self):
        self.root = validation.ROOT / 'fixtures/libraries/observation-contracts'
        self.change = json.loads((self.root / 'change.json').read_bytes())
        self.plan = self.change['preview']

    def validate(self, value, kind='change'):
        return validation.validate_bytes(json.dumps(value).encode(), kind)

    def reject(self, value, kind='change'):
        with self.assertRaises(ValueError):
            self.validate(value, kind)

    def recovered(self, state='prepared', attempts=None):
        journal = copy.deepcopy(self.change['journal'])
        journal['state'] = state
        if attempts is not None:
            journal['recoveryAttempts'] = attempts
        return {'schemaVersion': 1, 'changeId': journal['changeId'], 'state': state,
                'journal': journal, 'recoveredFromDisk': True, 'automaticRetryAllowed': False,
                'note': 'Synthetic recovery status; no replay.'}

    def attempt(self, state):
        result = {'attemptId': '4' * 32, 'startedUtc': '2026-10-23T00:00:00Z',
                  'state': state, 'resources': []}
        if state == 'completed':
            result['completedUtc'] = '2026-10-23T00:01:00Z'
        return result

    def test_fixtures_and_explicit_no_authority(self):
        for kind in ('plan', 'change'):
            raw = (self.root / (kind + '.json')).read_bytes()
            result = validation.validate_bytes(raw, kind, hashlib.sha256(raw).hexdigest())
            for flag in ('qualificationGranted', 'nativeExecuted', 'referencedFilesRead', 'previewHashRecomputed'):
                self.assertIs(result[flag], False)

    def test_required_envelopes_and_unknown_fields(self):
        for fixture, kind in ((self.plan, 'plan'), (self.change, 'change')):
            for key in validation.validator(kind).schema.get('required', self.change.keys()):
                value = copy.deepcopy(fixture); del value[key]
                self.reject(value, kind)
            value = copy.deepcopy(fixture); value['runtimeVerified'] = True
            self.reject(value, kind)

    def test_kind_catalog_matches_producer(self):
        source = (validation.ROOT / 'src/TiaMcp.Openness.V21/OpennessSession.LibraryChanges.cs').read_text(encoding='utf-8')
        line = next(x for x in source.splitlines() if 'LibraryChangeKinds =' in x)
        kinds = re.findall(r'"(library_[a-z_]+)"', line)
        self.assertEqual(kinds, validation.validator('plan').schema['properties']['kind']['enum'])
        for kind in kinds:
            value = copy.deepcopy(self.plan); value['kind'] = kind
            self.validate(value, 'plan')

    def test_all_live_states_must_match_journal(self):
        for state in ('prepared', 'checkpointing', 'applying', 'applied', 'recovery_required'):
            value = copy.deepcopy(self.change)
            value['state'] = value['journal']['state'] = state
            self.validate(value)
            value['journal']['state'] = 'applying' if state != 'applying' else 'prepared'
            self.reject(value)

    def test_repeated_identity_and_preview_pins_must_match(self):
        for field, replacement in (('changeId', 'lib-' + '9' * 32), ('previewHash', '9' * 64),
                                   ('kind', 'library_archive'), ('sessionGeneration', '9' * 32)):
            value = copy.deepcopy(self.change); value['journal'][field] = replacement
            with self.subTest(field=field): self.reject(value)
        for field in ('revision', 'sourceHash'):
            value = copy.deepcopy(self.change)
            value['preview']['resources'][0][field] = '9' * 64
            self.reject(value)

    def test_preview_comparison_is_typed_and_order_aware(self):
        value = copy.deepcopy(self.change)
        value['preview']['payload']['synthetic'] = 1
        self.reject(value)
        value = copy.deepcopy(self.change)
        value['preview'] = dict(reversed(list(value['preview'].items())))
        self.validate(value)
        value['preview']['effects'] = [{'first': 1}, {'second': 2}]
        value['journal']['preview']['effects'] = list(reversed(value['preview']['effects']))
        self.reject(value)

    def test_recovered_interrupted_work_stays_unknown(self):
        for state in ('prepared', 'applied', 'checkpointing', 'applying', 'recovery_required', 'restored_to_new_locations'):
            value = self.recovered(state, [])
            value['state'] = state if state in ('prepared', 'applied') else 'outcome_unknown'
            self.validate(value)
            value['state'] = 'applied' if value['state'] != 'applied' else 'prepared'
            self.reject(value)

    def test_recovery_attempt_latest_state_controls_response(self):
        for state in ('dispatching', 'failed', 'completed'):
            for earlier in ('dispatching', 'completed'):
                value = self.recovered('applied', [self.attempt(earlier), self.attempt(state)])
                value['state'] = 'restored_to_new_locations' if state == 'completed' else 'recovery_outcome_unknown'
                self.validate(value)
                value['state'] = 'applied'
                self.reject(value)

    def test_malformed_recovery_never_looks_completed(self):
        for replacement in (None, {}, [None], [{'state': 'completed'}], [self.attempt('unrecognized')]):
            value = self.recovered('applied', [])
            value['journal']['recoveryAttempts'] = replacement
            self.reject(value)
        value = self.recovered('applied', [self.attempt('completed')])
        value['state'] = 'restored_to_new_locations'
        del value['journal']['recoveryAttempts'][0]['completedUtc']
        self.reject(value)

    def test_live_and_recovered_shapes_cannot_be_mixed(self):
        value = self.recovered(); value['previewHash'] = 'b' * 64
        self.reject(value)
        value = copy.deepcopy(self.change); value['recoveredFromDisk'] = True
        self.reject(value)
        for fixture in (self.recovered(), copy.deepcopy(self.change)):
            for target in (fixture, fixture['journal']):
                target['automaticRetryAllowed'] = True
                self.reject(fixture)
                target['automaticRetryAllowed'] = False

    def test_document_snapshot_hash_and_compile_scope_coherence(self):
        value = copy.deepcopy(self.plan)
        value['documentsHash'] = 'a' * 64
        value['documentSource'] = {'originalDirectory': '/not/read', 'snapshotDirectory': '/not/read/either',
                                   'sha256': 'a' * 64, 'hashScope': 'synthetic', 'ownedSnapshot': True}
        self.validate(value, 'plan')
        value['documentSource']['sha256'] = 'b' * 64
        self.reject(value, 'plan')
        del value['documentSource']; self.reject(value, 'plan')
        value = copy.deepcopy(self.plan); value['compileTargetPaths'].append('/extra')
        self.reject(value, 'plan')
        del value['compileTargetPaths']; self.validate(value, 'plan')  # historical producer

    def test_integration_observation_cannot_promote_apply(self):
        value = copy.deepcopy(self.plan)
        value.update(integrationPlan={'synthetic': True}, applySupported=False, applyError='NotQualified')
        self.validate(value, 'plan')
        value['applySupported'] = True; self.reject(value, 'plan')
        del value['applySupported']; self.reject(value, 'plan')

    def test_full_string_pins_and_invalid_journal_dates(self):
        for field in ('changeId', 'previewHash', 'sessionGeneration'):
            value = copy.deepcopy(self.change); value['journal'][field] += '\n'
            self.reject(value)
        value = copy.deepcopy(self.change)
        value['journal']['updatedUtc'] = '2026-02-30T00:00:00Z'
        self.reject(value)
        value = copy.deepcopy(self.plan); value['fingerprint'] += '\n'
        self.reject(value, 'plan')

    def test_float_overflow_in_opaque_payload_is_rejected(self):
        for text in ('1e999', '-1e999', 'NaN', 'Infinity'):
            raw = json.dumps(self.plan).replace('true', text).encode()
            with self.subTest(text=text), self.assertRaises(ValueError):
                validation.validate_bytes(raw, 'plan')

    def decimal_change(self, left, right):
        value = copy.deepcopy(self.change)
        value['preview']['payload'] = {'measurement': 'LEFT_NUMBER'}
        value['journal']['preview']['payload'] = {'measurement': 'RIGHT_NUMBER'}
        return json.dumps(value).replace('"LEFT_NUMBER"', left).replace('"RIGHT_NUMBER"', right).encode()

    def test_distinct_decimal_observations_cannot_collapse_to_one_float(self):
        for left, right in (('9007199254740992.0', '9007199254740993.0'),
                            ('0.123456789012345678901', '0.123456789012345678902'),
                            ('1e-400', '2e-400'), ('0.0', '1e-400')):
            with self.subTest(left=left, right=right), self.assertRaises(ValueError):
                validation.validate_bytes(self.decimal_change(left, right), 'change')

    def test_equal_exact_decimal_values_keep_structural_comparison(self):
        for left, right in (('1.25', '125e-2'), ('1e-400', '0.1e-399'),
                            ('9007199254740993.0', '9007199254740993.00')):
            with self.subTest(left=left, right=right):
                self.assertTrue(validation.validate_bytes(self.decimal_change(left, right), 'change')['valid'])
        for left, right in (('true', '1'), ('1', '1.0'), ('false', '0.0')):
            with self.subTest(left=left, right=right), self.assertRaises(ValueError):
                validation.validate_bytes(self.decimal_change(left, right), 'change')

    def test_opaque_native_results_do_not_grant_qualification(self):
        value = copy.deepcopy(self.change)
        value['journal']['result'] = {'unrecognizedNativeObject': True}
        value['journal']['compile'] = {'errorCount': 42}
        self.assertFalse(self.validate(value)['qualificationGranted'])
        value['journal']['persistenceVerified'] = True
        self.reject(value)

    def test_actual_cli_pins_original_bytes_and_preserves_input(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'change.json'
            raw = b'\xef\xbb\xbf' + json.dumps(self.change).encode()
            path.write_bytes(raw)
            command = [sys.executable, str(validation.ROOT / 'scripts/validate_library_observations.py'),
                       '--kind', 'change', '--path', str(path), '--expected-sha256', hashlib.sha256(raw).hexdigest()]
            run = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(0, run.returncode, run.stderr + run.stdout)
            self.assertFalse(json.loads(run.stdout)['previewHashRecomputed'])
            self.assertEqual(raw, path.read_bytes())
            path.write_bytes(raw + b' ')
            self.assertEqual(1, subprocess.run(command, capture_output=True).returncode)


if __name__ == '__main__':
    unittest.main()
