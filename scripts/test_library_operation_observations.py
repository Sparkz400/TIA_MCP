import copy
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

import validate_library_observations as validation


class LibraryOperationObservations(unittest.TestCase):
    def setUp(self):
        root = validation.ROOT / 'fixtures/libraries/observation-contracts'
        self.change = json.loads((root / 'change.json').read_bytes())
        self.base = self.change['preview']
        self.pin = copy.deepcopy(self.base['selection'][0])
        self.source = {key: self.base['resources'][0][key] for key in ('libraryId', 'revision')}
        self.version = copy.deepcopy(self.base['dependencies'][0]['versions'][0])
        del self.version['requiredVersionGuids']
        self.identity = json.loads((root / 'identity.json').read_bytes())
        self.identity['sessionGeneration'] = self.base['sessionGeneration']
        self.identity['name'] = 'Synthetic'
        self.destination = 'library-' + '9' * 32

    def plan(self, kind, **changes):
        value = copy.deepcopy(self.base)
        value['kind'] = kind
        payload = {'schemaVersion': 1, 'source': self.source, 'selection': [self.pin]}
        if kind in ('library_create', 'library_upgrade'):
            payload = {'schemaVersion': 1}
        elif kind in ('library_organize', 'library_save', 'library_archive'):
            payload.pop('selection')
        elif kind in ('library_cleanup', 'library_harmonize'):
            payload.pop('selection'); payload['types'] = [self.pin['typeGuid']]
        elif kind in ('library_create_type', 'library_create_master_copy'):
            payload = {'schemaVersion': 1, 'destinationLibraryId': self.destination}
        if kind == 'library_create': payload['name'] = 'Synthetic'
        elif kind == 'library_upgrade': payload.update(path='D:/Synthetic/old.zal20', expectedSha256='a' * 64)
        elif kind in ('library_copy_elements', 'library_publish'): payload['destinationLibraryId'] = self.destination
        elif kind == 'library_update_project': payload['updateScope'] = 'entireSelectedPlc'
        elif kind == 'library_create_type': payload.update(documentDirectory='D:/Synthetic/Documents', documentName='Synthetic')
        elif kind == 'library_create_master_copy': payload['object'] = {'objectPath': '/Synthetic/Block'}
        elif kind == 'library_edit_version': payload['testInstanceName'] = 'Synthetic'
        elif kind == 'library_release_version': payload.update(versionNumber='1.0.0', author='Author', comment='Release')
        elif kind == 'library_type_policy': payload['doNotUse'] = False
        elif kind == 'library_organize': payload.update(action='create', folderKind='types', name='Synthetic')
        elif kind == 'library_cleanup': payload['cleanUpMode'] = 'DeleteUnusedTypes'
        elif kind == 'library_harmonize': payload.update(target={'projectName': 'Synthetic', 'plcPath': '/PLC'}, harmonizeNames=True)
        elif kind == 'library_integrate':
            payload.update(action='plc_component_plan', planOnly=True, target={'projectName': 'Synthetic', 'plcPath': '/PLC'},
                           integration={'dependencyPins': [], 'instanceDb': {'name': 'Instance', 'numberPolicy': 'firstAvailable'},
                                        'caller': {'mode': 'createStandaloneFc', 'name': 'Caller', 'language': 'FBD', 'numberPolicy': 'firstAvailable'},
                                        'mappings': [{'parameter': 'Enable', 'direction': 'Input', 'dataType': 'Bool', 'tagPath': '/PLC/tags/Enable'}]})
            value.update(integrationPlan={'synthetic': True}, applySupported=False, applyError='NotQualified')
        payload.update(changes)
        value['payload'] = copy.deepcopy(payload)
        value['selection'] = copy.deepcopy(payload.get('selection', []))
        if 'destinationLibraryId' in payload:
            value['resources'].append({**copy.deepcopy(value['resources'][0]), 'libraryId': self.destination})
        return value

    def response(self, plan, result=None, state='applied'):
        value = copy.deepcopy(self.change)
        value.update(preview=copy.deepcopy(plan), state=state)
        value['journal'].update(preview=copy.deepcopy(plan), kind=plan['kind'], state=state)
        if result is not None: value['journal']['result'] = copy.deepcopy(result)
        return value

    def result(self, kind):
        result = {'objects': []}
        if kind in ('library_create', 'library_upgrade'):
            result['library'] = self.identity
            if kind == 'library_upgrade': result['sourceSnapshotOwned'] = True
        elif kind in ('library_copy_elements', 'library_publish'):
            result.update(objects=[self.version], typeSynchronization=[{'typeGuid': self.pin['typeGuid'],
                          'defaultVersion': self.version, 'versions': [self.version], 'exactVersionsAndDefaultVerified': True}])
        elif kind == 'library_instantiate': result['objects'] = [{'object': {'engineeringType': 'Synthetic'}, 'version': self.version, 'linkVerified': True}]
        elif kind == 'library_create_type': result['objects'] = [{'typeGuid': self.pin['typeGuid'], 'name': 'Synthetic', 'transferState': 'Unknown'}]
        elif kind == 'library_create_master_copy': result['objects'] = [{'masterCopy': 'Synthetic'}]
        elif kind == 'library_discard_version': result.update(discardedVersionGuid=self.pin['versionGuid'], remainingType=None)
        elif kind in ('library_update_project', 'library_edit_version', 'library_release_version', 'library_set_default', 'library_type_policy'):
            result['objects'] = [self.version]
        elif kind == 'library_cleanup': result.update(removedVersionGuids=[self.pin['versionGuid']], candidatesRetainedByNative=[], removedTypeGuids=[])
        elif kind == 'library_harmonize': result['objects'] = [{'objectPath': '/PLC/blocks/Synthetic', 'name': 'Synthetic', 'groupPath': '', 'versionGuid': self.pin['versionGuid']}]
        elif kind == 'library_archive': result['archive'] = {'path': 'D:/Synthetic/published.zal21', 'bytes': 12, 'sha256': 'a' * 64, 'mediaType': 'application/octet-stream'}
        return copy.deepcopy(result)

    def validate(self, value, kind='operation-plan'):
        return validation.validate_bytes(json.dumps(value).encode(), kind)

    def reject(self, value, kind='operation-plan'):
        with self.assertRaises(ValueError): self.validate(value, kind)

    def test_all_producer_kinds_have_payload_and_result_branches(self):
        source = (validation.ROOT / 'src/TiaMcp.Openness.V21/OpennessSession.LibraryChanges.cs').read_text(encoding='utf-8')
        kinds = re.findall(r'"(library_[a-z_]+)"', next(line for line in source.splitlines() if 'LibraryChangeKinds =' in line))
        self.assertEqual(kinds, validation.validator('operation').schema['properties']['kind']['enum'])
        for kind in kinds:
            with self.subTest(kind=kind):
                plan = self.plan(kind)
                observed = self.validate(plan)
                self.assertTrue(observed['operationPayloadValidated'])
                self.assertFalse(observed['operationResultValidated'])
                if kind != 'library_integrate':
                    observed = self.validate(self.response(plan, self.result(kind)), 'operation-change')
                    self.assertTrue(observed['operationResultValidated'])
                    self.assertFalse(observed['qualificationGranted'])

    def test_legacy_envelope_modes_still_accept_opaque_payloads(self):
        self.validate(self.base, 'plan')
        self.reject(self.base)
        self.validate(self.change, 'change')
        self.reject(self.change, 'operation-change')

    def test_plan_selection_and_original_source_resources_must_agree(self):
        for mutate in (lambda p: p['selection'].clear(), lambda p: p['resources'].clear(),
                       lambda p: p['payload']['source'].update(revision='b' * 64),
                       lambda p: p['resources'].append(copy.deepcopy(p['resources'][0]))):
            value = self.plan('library_instantiate'); mutate(value); self.reject(value)
        value = self.plan('library_publish'); value['payload']['destinationLibraryId'] = self.source['libraryId']; self.reject(value)
        value = self.plan('library_publish'); value['resources'].pop(); self.reject(value)

    def test_duplicate_and_conflicting_selections_refuse(self):
        for second in (self.pin, {**self.pin, 'versionGuid': '3' * 8 + '-3333-3333-3333-' + '3' * 12}):
            self.reject(self.plan('library_instantiate', selection=[self.pin, second]))
        # The native selector explicitly prioritizes masterCopyPath. Extra type
        # metadata is retained but cannot turn it into a version selection.
        self.validate(self.plan('library_instantiate', selection=[{**self.pin, 'masterCopyPath': '/masterCopies/A'}], destinationComposition='blocks'))
        self.reject(self.plan('library_instantiate', selection=[{'masterCopyPath': '/masterCopies/A'}] * 2, destinationComposition='blocks'))

    def test_every_supplied_type_policy_flag_must_be_boolean(self):
        for bad in (None, 0, 'true', [], {}):
            for field in ('doNotUse', 'setForUpdate'):
                self.reject(self.plan('library_type_policy', **{field: bad}))
        value = self.plan('library_type_policy'); del value['payload']['doNotUse']; self.reject(value)

    def test_release_version_and_utf16_text_boundaries(self):
        for bad in ('1', '1.2.3.4.5', '1.-2', '1.2147483648', '1.0\nextra'):
            self.reject(self.plan('library_release_version', versionNumber=bad))
        self.validate(self.plan('library_release_version', versionNumber='+1. 0.0'))
        self.reject(self.plan('library_release_version', author='😀' * 65))
        self.validate(self.plan('library_release_version', author='😀' * 64))
        self.reject(self.plan('library_create', name=' '))
        self.reject(self.plan('library_create', name='../outside'))
        self.validate(self.plan('library_create_type', documentName='x' * 256))

    def test_upgrade_and_companion_manifest_boundaries(self):
        for suffix in ('.zal21', '.zal13', '.txt', '.al20\n'):
            self.reject(self.plan('library_upgrade', path='D:/Synthetic/old' + suffix))
        for path in ('old.al20', '/old.al20', 'D:old.al20', '\\\\?\\D:\\old.al20'):
            self.reject(self.plan('library_upgrade', path=path))
        self.validate(self.plan('library_upgrade', path='\\\\server\\share\\old.al20'))
        file = {'relativePath': 'a.xml', 'bytes': 1, 'sha256': 'a' * 64}
        self.validate(self.plan('library_upgrade', expectedFiles=[file]))
        for path in ('../a.xml', 'D:/a.xml', '/a.xml', '\\a.xml', ' '):
            self.reject(self.plan('library_upgrade', expectedFiles=[{**file, 'relativePath': path}]))
        self.reject(self.plan('library_upgrade', expectedFiles=[file, {**file, 'relativePath': 'A.xml'}]))

    def test_update_no_op_matches_captured_impact(self):
        plan = self.plan('library_update_project')
        plan['updateImpact'] = {'noOp': True}
        result = {'objects': [], 'noOp': True, 'reason': 'Already exact.'}
        self.validate(self.response(plan, result), 'operation-change')
        self.reject(self.response(plan, self.result('library_update_project')), 'operation-change')
        plan['updateImpact']['noOp'] = False
        self.reject(self.response(plan, result), 'operation-change')

    def test_unused_payload_fields_remain_opaque(self):
        plan = self.plan('library_save', name=None, bindings={'unused': True}, target={'family': 'unified'})
        self.validate(self.response(plan, self.result('library_save')), 'operation-change')

    def test_plc_recipe_remains_plan_only_and_preserves_exact_mappings(self):
        value = self.plan('library_integrate')
        self.reject(self.response(value, {'objects': []}), 'operation-change')
        value['payload']['target']['scopePath'] = '/PLC/Unit'; self.reject(value)
        value = self.plan('library_integrate'); value['payload']['integration']['dependencyPins'] = [self.pin]; self.reject(value)
        value = self.plan('library_integrate'); value['payload']['integration']['mappings'] *= 2; self.reject(value)
        value = self.plan('library_integrate'); value['applySupported'] = True; self.reject(value)

    def test_applied_result_required_but_recovery_is_not_promoted(self):
        plan = self.plan('library_save')
        self.reject(self.response(plan), 'operation-change')
        value = self.response(plan, state='recovery_required')
        self.assertFalse(self.validate(value, 'operation-change')['operationResultValidated'])
        recovered = {'schemaVersion': 1, 'changeId': value['changeId'], 'journal': value['journal'],
                     'state': 'outcome_unknown', 'recoveredFromDisk': True, 'automaticRetryAllowed': False, 'note': 'Synthetic'}
        self.validate(recovered, 'operation-change')
        recovered['state'] = 'applied'; self.reject(recovered, 'operation-change')

    def test_result_version_policy_and_default_pins(self):
        for kind in ('library_instantiate', 'library_release_version', 'library_set_default', 'library_type_policy'):
            result = self.result(kind)
            row = result['objects'][0]['version'] if kind == 'library_instantiate' else result['objects'][0]
            row['typeGuid' if kind == 'library_release_version' else 'versionGuid'] = '33333333-3333-3333-3333-333333333333'
            self.reject(self.response(self.plan(kind), result), 'operation-change')
        result = self.result('library_set_default'); result['objects'][0]['isDefault'] = False
        self.reject(self.response(self.plan('library_set_default'), result), 'operation-change')
        result = self.result('library_type_policy'); result['objects'][0]['doNotUse'] = True
        self.reject(self.response(self.plan('library_type_policy'), result), 'operation-change')
        result = self.result('library_release_version'); result['objects'][0]['state'] = 'InWork'
        self.reject(self.response(self.plan('library_release_version'), result), 'operation-change')

    def test_release_can_rotate_version_guid_but_keeps_type_and_release_number(self):
        result = self.result('library_release_version')
        result['objects'][0]['versionGuid'] = '33333333-3333-3333-3333-333333333333'
        self.validate(self.response(self.plan('library_release_version'), result), 'operation-change')
        result['objects'][0]['version'] = '2.0.0'
        self.reject(self.response(self.plan('library_release_version'), result), 'operation-change')

    def test_result_shape_cannot_be_borrowed_from_another_operation(self):
        self.reject(self.response(self.plan('library_save'), self.result('library_create')), 'operation-change')
        self.reject(self.response(self.plan('library_archive'), {'objects': []}), 'operation-change')
        value = self.result('library_create'); value['library']['sessionGeneration'] = '9' * 32
        self.reject(self.response(self.plan('library_create'), value), 'operation-change')

    def test_synchronization_observations_reject_duplicate_and_wrong_defaults(self):
        for mutate in (lambda r: r['typeSynchronization'].append(copy.deepcopy(r['typeSynchronization'][0])),
                       lambda r: r['typeSynchronization'][0]['versions'].append(copy.deepcopy(self.version)),
                       lambda r: r['typeSynchronization'][0]['defaultVersion'].update(version='9.0.0')):
            result = self.result('library_publish'); mutate(result)
            self.reject(self.response(self.plan('library_publish'), result), 'operation-change')

    def hmi(self, action, family='unified', kind='library_instantiate'):
        target = {'family': family, 'hmiPath': '/Synthetic/HMI'}
        changes = {'target': target, 'hmiAction': action}
        if family == 'classic': target['composition'] = 'screens'
        elif action == 'tag_create':
            target['tagTable'] = 'Tags'; changes.update(selection=[], name='Tag', dataType='Bool')
        else:
            target.update(screen='Screen', container='Container')
            if action == 'faceplate_create':
                changes.update(placement={'left': 1, 'top': 2, 'width': 100, 'height': 80}, integration={'mode': 'instantiateOnly'}, bindings=[])
            else: changes['bindings'] = [{'property': 'Enabled', 'tag': 'Tag', 'dataType': 'Bool'}]
        return self.plan(kind, **changes)

    def faceplate(self):
        return {'nativeObjectType': 'Synthetic', **self.pin, 'version': '1.0.0', 'linkStatus': 'linked',
                'nativeLinkVerified': True, 'runtimeVerified': False, 'name': 'Container', 'containedType': 'V1.0.0\\Type',
                'left': 1, 'top': 2, 'width': 100, 'height': 80,
                'interface': [{'property': 'Enabled', 'value': 'Tag'}], 'interfaceTruncated': False}

    def test_classic_and_unified_payload_routes(self):
        self.validate(self.hmi('instantiate', 'classic'))
        self.reject(self.hmi('instantiate', 'classic', 'library_integrate'))
        for action in ('tag_create', 'faceplate_create', 'faceplate_bind'): self.validate(self.hmi(action))
        self.validate(self.hmi('faceplate_bind', kind='library_integrate'))
        self.reject(self.hmi('faceplate_create', kind='library_integrate'))
        value = self.hmi('tag_create'); value['payload']['connection'] = 'PLC'; self.reject(value)
        value = self.hmi('faceplate_create'); value['payload']['bindings'] = [{'property': 'P', 'tag': 'Tag', 'dataType': 'Bool'}]; self.reject(value)
        value = self.hmi('faceplate_bind'); value['payload']['bindings'] *= 2; self.reject(value)
        value = self.hmi('faceplate_create'); value['payload']['placement']['width'] = 0; self.reject(value)

    def test_hmi_text_rejects_non_whitespace_control_characters(self):
        for control in ('\x00', '\x01', '\x7f', '\x9f'):
            value = self.hmi('tag_create'); value['payload']['target']['tagTable'] = 'Tags' + control
            self.reject(value)
            value = self.hmi('tag_create'); value['payload']['target']['hmiPath'] += control
            self.reject(value)
            for field in ('tag', 'dataType'):
                value = self.hmi('faceplate_bind'); value['payload']['bindings'][0][field] += control
                self.reject(value)

    def test_hmi_result_link_and_observed_bindings(self):
        plan, result = self.hmi('faceplate_bind'), self.faceplate()
        self.validate(self.response(plan, result), 'operation-change')
        for field, bad in (('nativeLinkVerified', False), ('versionGuid', None), ('runtimeVerified', True), ('name', 'Other')):
            changed = copy.deepcopy(result); changed[field] = bad
            self.reject(self.response(plan, changed), 'operation-change')
        result['interface'][0]['value'] = 'Other'; self.reject(self.response(plan, result), 'operation-change')
        result = self.faceplate(); result['left'] = 9
        self.reject(self.response(self.hmi('faceplate_create'), result), 'operation-change')
        result = {'name': 'Tag', 'dataType': 'Bool', 'connection': '', 'runtimeVerified': False}
        self.validate(self.response(self.hmi('tag_create'), result), 'operation-change')
        result['connection'] = 'PLC'; self.reject(self.response(self.hmi('tag_create'), result), 'operation-change')

    def test_classic_master_metadata_does_not_become_a_version_link(self):
        plan = self.hmi('instantiate', 'classic')
        plan['payload']['selection'] = [{**self.pin, 'masterCopyPath': '/masterCopies/TagTable'}]
        plan['selection'] = copy.deepcopy(plan['payload']['selection'])
        plan['payload']['target']['composition'] = 'tagTables'
        result = {'nativeObjectType': 'Synthetic', 'typeGuid': None, 'versionGuid': None, 'version': None,
                  'nativeLinkVerified': False, 'runtimeVerified': False, 'linkStatus': 'noService'}
        self.validate(self.response(plan, result), 'operation-change')
        result.update(linkStatus='unavailable', linkReadError='missingVersion')
        self.validate(self.response(plan, result), 'operation-change')
        result['nativeLinkVerified'] = True
        self.reject(self.response(plan, result), 'operation-change')

    def test_internal_tag_requires_matching_original_sentinel_and_no_binding(self):
        plan = self.hmi('tag_create')
        result = {'name': 'Tag', 'dataType': 'Bool', 'connection': '<Interne Variable>',
                  'defaultConnection': '<Interne Variable>', 'plcTag': '',
                  'nativeConnectionResolved': False, 'runtimeVerified': False}
        self.validate(self.response(plan, result), 'operation-change')
        for field, value in (('defaultConnection', '<Internal tag>'), ('plcTag', 'Motor.State'),
                             ('nativeConnectionResolved', True)):
            changed = {**result, field: value}
            self.reject(self.response(plan, changed), 'operation-change')
        for field in ('defaultConnection', 'plcTag', 'nativeConnectionResolved'):
            changed = copy.deepcopy(result); del changed[field]
            self.reject(self.response(plan, changed), 'operation-change')
        for label in (None, ''):
            changed = {**result, 'connection': label, 'defaultConnection': label, 'plcTag': None}
            self.validate(self.response(plan, changed), 'operation-change')
        legacy = {key: result[key] for key in ('name', 'dataType', 'connection', 'runtimeVerified')}
        self.reject(self.response(plan, legacy), 'operation-change')

    def test_exact_original_cli_bytes_and_pin(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'operation.json'
            raw = b'\xef\xbb\xbf' + json.dumps(self.plan('library_save')).encode()
            path.write_bytes(raw)
            args = [sys.executable, str(validation.ROOT / 'scripts/validate_library_observations.py'),
                    '--kind', 'operation-plan', '--path', str(path), '--expected-sha256', hashlib.sha256(raw).hexdigest()]
            result = subprocess.run(args, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertFalse(json.loads(result.stdout)['nativeExecuted'])
            self.assertEqual(raw, path.read_bytes())
            path.write_bytes(raw + b' ')
            self.assertEqual(1, subprocess.run(args, capture_output=True).returncode)

    def test_relocated_validator_uses_only_bundled_resources(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'scripts').mkdir(); (root / 'contracts').mkdir()
            for name in ('validate_library_observations.py', 'library_operation_observations.py', 'library_hmi_query_observations.py', 'library_compilation_observations.py', 'library_update_observations.py', 'library_synchronization_observations.py', 'library_hmi_preview_observations.py', 'library_component_preview_observations.py', 'library_preview_effect_observations.py'):
                shutil.copyfile(validation.ROOT / 'scripts' / name, root / 'scripts' / name)
            for path in (validation.ROOT / 'contracts').glob('*.schema.json'):
                shutil.copyfile(path, root / 'contracts' / path.name)
            path = root / 'observation.json'
            path.write_text(json.dumps(self.response(self.plan('library_archive'), self.result('library_archive'))))
            environment = {key: value for key, value in os.environ.items() if key.upper() != 'PYTHONPATH'}
            run = subprocess.run([sys.executable, '-B', str(root / 'scripts/validate_library_observations.py'),
                                  '--kind', 'operation-change', '--path', str(path)], cwd=root,
                                 env=environment, capture_output=True, text=True)
            self.assertEqual(0, run.returncode, run.stdout + run.stderr)
            self.assertTrue(json.loads(run.stdout)['operationResultValidated'])
            path.write_bytes((validation.ROOT / 'fixtures/libraries/observation-contracts/hmi-inventory-unified.json').read_bytes())
            run = subprocess.run([sys.executable, '-B', str(root / 'scripts/validate_library_observations.py'),
                                  '--kind', 'hmi-inventory', '--path', str(path)], cwd=root,
                                 env=environment, capture_output=True, text=True)
            self.assertEqual(0, run.returncode, run.stdout + run.stderr)
            self.assertFalse(json.loads(run.stdout)['qualificationGranted'])
            path.write_bytes((validation.ROOT / 'fixtures/libraries/observation-contracts/update-plan.json').read_bytes())
            run = subprocess.run([sys.executable, '-B', str(root / 'scripts/validate_library_observations.py'),
                                  '--kind', 'update-plan', '--path', str(path)], cwd=root,
                                 env=environment, capture_output=True, text=True)
            self.assertEqual(0, run.returncode, run.stdout + run.stderr)
            self.assertTrue(json.loads(run.stdout)['updateAnalysisValidated'])
            path.write_bytes((validation.ROOT / 'fixtures/libraries/observation-contracts/synchronization-change.json').read_bytes())
            run = subprocess.run([sys.executable, '-B', str(root / 'scripts/validate_library_observations.py'),
                                  '--kind', 'sync-change', '--path', str(path)], cwd=root,
                                 env=environment, capture_output=True, text=True)
            self.assertEqual(0, run.returncode, run.stdout + run.stderr)
            self.assertTrue(json.loads(run.stdout)['synchronizationResultBound'])


if __name__ == '__main__':
    unittest.main()
