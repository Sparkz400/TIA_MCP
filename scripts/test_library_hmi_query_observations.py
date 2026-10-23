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
from library_hmi_query_observations import OPERATIONS

FIXTURE = validation.ROOT / 'fixtures/libraries/observation-contracts'


class LibraryHmiQueryObservations(unittest.TestCase):
    def setUp(self):
        def read(name): return json.loads((FIXTURE / (name + '.json')).read_bytes())
        self.absent = read('hmi-capabilities-missing')
        self.present = read('hmi-capabilities-installed')
        self.targets = read('hmi-targets')
        self.classic = read('hmi-inventory-classic')
        self.unified = read('hmi-inventory-unified')

    def validate(self, value, kind='hmi-inventory'):
        return validation.validate_bytes(json.dumps(value).encode(), kind)

    def reject(self, value, kind='hmi-inventory'):
        with self.assertRaises(ValueError): self.validate(value, kind)

    def test_original_fixture_bytes_and_no_qualification(self):
        for name, kind in [('hmi-capabilities-missing', 'hmi-capabilities'), ('hmi-capabilities-installed', 'hmi-capabilities'),
                           ('hmi-targets', 'hmi-targets'), ('hmi-inventory-classic', 'hmi-inventory'), ('hmi-inventory-unified', 'hmi-inventory')]:
            raw = (FIXTURE / (name + '.json')).read_bytes()
            result = validation.validate_bytes(raw, kind, hashlib.sha256(raw).hexdigest())
            for key in ('nativeExecuted', 'referencedFilesRead', 'qualificationGranted', 'previewHashRecomputed'):
                self.assertFalse(result[key])
            self.assertEqual(raw, (FIXTURE / (name + '.json')).read_bytes())

    def test_query_requests_preserve_native_defaults_and_target_selection_forms(self):
        for action in ('hmi_capabilities', 'hmi_targets'):
            self.validate({'action': action}, 'hmi-request')
        for limit in (None, 1, 200):
            selected = {'family': 'unified', 'hmiPath': '/devices/Synthetic/items/HMI/hmi/Synthetic'}
            self.validate({'action': 'hmi_inventory', 'target': selected, 'limit': limit}, 'hmi-request')
            self.validate({'action': 'hmi_inventory', **selected, 'limit': limit, 'target': None}, 'hmi-request')
        # Capabilities do not inspect unused project/target fields.
        self.validate({'action': 'hmi_capabilities', 'projectName': {}, 'target': []}, 'hmi-request')

    def test_query_requests_refuse_invalid_bounds_and_missing_target_fields(self):
        request = {'action': 'hmi_inventory', 'target': {'family': 'classic', 'hmiPath': '/Synthetic'}}
        for limit in (0, 201, True, 1.5, '10'):
            self.reject({**request, 'limit': limit}, 'hmi-request')
        for selected in ({}, {'family': 'classic'}, {'family': 'hmi', 'hmiPath': '/Synthetic'},
                         {'family': 'classic', 'hmiPath': ' '}, {'family': 'classic', 'hmiPath': '/x\n'},
                         {'family': 'classic', 'hmiPath': '/x\x00'}, {'family': 'classic', 'hmiPath': '/x\x7f'},
                         {'family': 'classic', 'hmiPath': '😀' * 2049}):
            self.reject({**request, 'target': selected}, 'hmi-request')
        self.reject({'action': 'hmi_unknown'}, 'hmi-request')
        self.reject({'request': request}, 'hmi-request')

    def test_explicit_modes_cannot_accept_other_response_shapes(self):
        for value, correct in [(self.absent, 'hmi-capabilities'), (self.targets, 'hmi-targets'), (self.classic, 'hmi-inventory')]:
            for kind in ('hmi-capabilities', 'hmi-targets', 'hmi-inventory'):
                if kind != correct: self.reject(value, kind)

    def test_absent_modules_cannot_claim_metadata_or_availability(self):
        for field, value in [('apiAvailability', 'available'), ('assemblyVersion', '21.0.0.0'),
                             ('assemblyIdentity', self.present['modules'][0]['assemblyIdentity']), ('installedApi', True)]:
            result = copy.deepcopy(self.absent); result['modules'][0][field] = value
            self.reject(result, 'hmi-capabilities')
        result = copy.deepcopy(self.absent); result['modules'][1] = copy.deepcopy(result['modules'][0])
        self.reject(result, 'hmi-capabilities')

    def test_accepted_modules_require_matching_identity_and_version(self):
        for field, value in [('assembly', 'Other.dll'), ('apiAvailability', 'unavailable'), ('assemblyVersion', '20.0.0.0'),
                             ('installedApi', False), ('nativeQualified', True), ('runtimeQualified', True)]:
            result = copy.deepcopy(self.present); result['modules'][0][field] = value
            self.reject(result, 'hmi-capabilities')
        result = copy.deepcopy(self.present); result['modules'][0]['assemblyIdentity']['observedMajorVersion'] = 20
        self.reject(result, 'hmi-capabilities')

    def test_rejection_reason_matches_original_assembly_observations(self):
        cases = [('observedAssemblyName', 'Other', 'HmiAssemblyIdentityMismatch'),
                 ('observedPublicKeyToken', '0' * 16, 'HmiAssemblyIdentityMismatch'),
                 ('requiredPublicKeyToken', '', 'HmiAssemblyIdentityMismatch'),
                 ('observedCulture', 'de', 'HmiAssemblyIdentityMismatch'),
                 ('observedMajorVersion', 20, 'HmiVersionMismatch')]
        for field, value, code in cases:
            result = copy.deepcopy(self.present); module = result['modules'][0]; identity = module['assemblyIdentity']
            identity[field] = value
            if field == 'observedMajorVersion': module['assemblyVersion'] = identity['assemblyVersion'] = '20.0.0.0'
            identity.update(accepted=False, code=code)
            module.update(apiAvailability='unavailable', diagnostic=copy.deepcopy(identity))
            self.validate(result, 'hmi-capabilities')
            module['diagnostic']['accepted'] = True
            self.reject(result, 'hmi-capabilities')
        result = copy.deepcopy(self.present); result['modules'][0]['assemblyIdentity']['assemblyVersion'] = '21.65536.0.0'
        self.reject(result, 'hmi-capabilities')

    def test_unreadable_installed_module_stays_unavailable(self):
        result = copy.deepcopy(self.absent); module = result['modules'][0]
        module.update(installedApi=True, diagnostic={'code': 'HmiNativeReadFailed', 'operation': 'optionalAssembly',
                      'nativeException': 'System.BadImageFormatException', 'reason': 'Synthetic malformed module', 'retrySafe': True})
        self.validate(result, 'hmi-capabilities')
        module['apiAvailability'] = 'available'; self.reject(result, 'hmi-capabilities')
        module['apiAvailability'] = 'unavailable'; module['assemblyVersion'] = '21.0.0.0'
        self.reject(result, 'hmi-capabilities')

    def test_capability_pairs_and_false_flags_match_producer(self):
        source = (validation.ROOT / 'src/TiaMcp.Openness.V21/OpennessSession.LibraryHmi.cs').read_text()
        actual = {(family, operation): implementation for family, operation, implementation in
                  re.findall(r'HmiCapability\("(classic|unified)", "([a-z_]+)", "([a-z]+)"', source)}
        self.assertEqual(OPERATIONS, actual)
        for mutate in (lambda rows: rows.__setitem__(1, copy.deepcopy(rows[0])),
                       lambda rows: rows[0].update(engineeringQualified=True),
                       lambda rows: rows[-1].update(implementation='typed'),
                       lambda rows: rows[0].update(family='unified')):
            result = copy.deepcopy(self.absent); mutate(result['operations']); self.reject(result, 'hmi-capabilities')

    def test_target_identity_and_compiler_scope_are_bound(self):
        self.validate({'targets': [self.unified['target']], 'complete': True}, 'hmi-targets')
        for kind in ('classic', 'unified'):
            value = copy.deepcopy(self.classic if kind == 'classic' else self.unified)
            value['target']['compileScope'] += '/wrong'; self.reject(value)
        value = copy.deepcopy(self.unified); value['target']['compileScopeKind'] = 'hmiSoftware'; self.reject(value)
        value['target']['hmiPath'] = '/not/an/owner'; self.reject(value)
        value = copy.deepcopy(self.targets); value['targets'] *= 2; self.reject(value, 'hmi-targets')
        value = {'targets': [], 'complete': True}; self.validate(value, 'hmi-targets')
        value['complete'] = False; self.reject(value, 'hmi-targets')

    def test_classic_top_level_truncation_does_not_require_200_rows(self):
        value = copy.deepcopy(self.classic); value['items'] = value['items'][:1]; value['truncated'] = True
        self.validate(value)
        value['items'] = []; self.reject(value)
        value = copy.deepcopy(self.classic); value['items'][0]['path'] = '/tagTables/Wrong'; self.reject(value)
        value = copy.deepcopy(self.classic); value['items'].append(copy.deepcopy(value['items'][0])); self.reject(value)

    def test_classic_child_truncation_is_fixed_to_200(self):
        for key in ('tags', 'connections'):
            value = copy.deepcopy(self.classic)
            row = value['items'][1] if key == 'tags' else value
            flag = 'tagsTruncated' if key == 'tags' else 'connectionsTruncated'
            row[key] = [{'name': str(i)} for i in range(200)]; row[flag] = True
            self.validate(value)
            row[key].pop(); self.reject(value)
            row[flag] = False; row[key][1] = copy.deepcopy(row[key][0]); self.reject(value)

    def test_unified_screen_truncation_counts_all_items_not_only_faceplates(self):
        value = copy.deepcopy(self.unified)
        value['screens'][0].update(faceplates=[], screenItemsTruncated=True)
        self.assertFalse(value['truncated'])
        self.validate(value)
        value['screens'][0]['width'] = -1; self.reject(value)

    def test_unified_link_observations_do_not_verify_a_selected_version(self):
        value = copy.deepcopy(self.unified); face = value['screens'][0]['faceplates'][0]
        face.update(linkStatus='unavailable', linkReadError='missingVersion')
        self.validate(value)
        face.update(linkStatus='linked', typeGuid='11111111-1111-1111-1111-111111111111',
                    versionGuid='22222222-2222-2222-2222-222222222222', version='1.0.0')
        del face['linkReadError']; self.validate(value)
        face['nativeLinkVerified'] = True; self.reject(value)
        face['nativeLinkVerified'] = False; face['runtimeVerified'] = True; self.reject(value)
        face['runtimeVerified'] = False; face['typeGuid'] = None; self.reject(value)

    def test_unified_interface_truncation_and_duplicate_properties(self):
        value = copy.deepcopy(self.unified); face = value['screens'][0]['faceplates'][0]
        face.update(interface=[{'property': str(i), 'value': {'opaque': [i, None, False]}} for i in range(200)], interfaceTruncated=True)
        self.validate(value)
        face['interface'].pop(); self.reject(value)
        face['interfaceTruncated'] = False; face['interface'][1] = copy.deepcopy(face['interface'][0]); self.reject(value)

    def test_unified_top_level_truncation_does_not_invent_request_limits(self):
        value = copy.deepcopy(self.unified); value['truncated'] = True
        self.validate(value)
        value.update(screens=[], tags=[], connections=[]); self.reject(value)
        value['truncated'] = False; self.validate(value)

    def test_unified_nullable_connection_metadata_and_localized_labels_are_retained(self):
        value = copy.deepcopy(self.unified)
        for key in ('dataType', 'hmiDataType', 'connection', 'plcTag', 'table'): value['tags'][0][key] = None
        self.validate(value)
        value['tags'][0]['connection'] = '<Interne Variable>'; self.validate(value)
        value['tags'][0]['connection'] = {'guessed': True}; self.reject(value)

    def test_each_inventory_collection_is_bounded_and_unique(self):
        for key in ('screens', 'tags', 'connections'):
            value = copy.deepcopy(self.unified); value[key] *= 201; self.reject(value)
            value = copy.deepcopy(self.unified); value[key] *= 2; self.reject(value)
        value = copy.deepcopy(self.unified); value['screens'][0]['faceplates'] *= 2; self.reject(value)
        value = copy.deepcopy(self.classic); value['items'] *= 101; self.reject(value)

    def test_original_cli_bytes_bom_and_hash_are_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'observation.json'
            raw = b'\xef\xbb\xbf' + json.dumps(self.unified).encode(); path.write_bytes(raw)
            args = [sys.executable, '-B', str(validation.ROOT / 'scripts/validate_library_observations.py'),
                    '--kind', 'hmi-inventory', '--path', str(path), '--expected-sha256', hashlib.sha256(raw).hexdigest()]
            result = subprocess.run(args, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertEqual(raw, path.read_bytes())
            path.write_bytes(raw + b' ')
            self.assertEqual(1, subprocess.run(args, capture_output=True).returncode)

    def test_extra_completion_and_unknown_family_claims_refuse(self):
        value = copy.deepcopy(self.unified); value['complete'] = True; self.reject(value)
        value = copy.deepcopy(self.classic); value['target']['family'] = 'hmi'; self.reject(value)
        value = copy.deepcopy(self.unified); value['screens'][0]['faceplates'][0]['linkStatus'] = 'noService'
        value['screens'][0]['faceplates'][0]['linkReadError'] = 'unexplained'; self.reject(value)


if __name__ == '__main__':
    unittest.main()
