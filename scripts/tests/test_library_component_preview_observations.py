import copy
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import validate_library_observations as validation
import test_library_operation_observations as operation_fixtures


class LibraryComponentPreviewObservations(unittest.TestCase):
    def setUp(self):
        self.path = validation.ROOT / 'fixtures/libraries/observation-contracts/component-plan.json'
        self.preview = json.loads(self.path.read_bytes())

    def validate(self, value=None, kind='component-plan'):
        return validation.validate_bytes(json.dumps(self.preview if value is None else value).encode(), kind)

    def reject(self, change):
        value = copy.deepcopy(self.preview); change(value)
        with self.assertRaises(ValueError):
            self.validate(value)

    def graph(self, value):
        return value['integrationPlan']['steps'][1]['prepareArguments']['payload']['model']

    def sync_effect(self, value):
        next(e for e in value['effects'] if e.get('action') == 'planPlcComponent')['proposedSteps'] = copy.deepcopy(value['integrationPlan']['steps'])

    def existing(self):
        value = copy.deepcopy(self.preview); plan = value['integrationPlan']
        path = plan['caller']['blockPath']
        value['payload']['integration']['caller'] = dict(mode='existing', blockPath=path, insertAfterNetworkId='3')
        plan['caller'].update(mode='existing', initiallyAbsent=False)
        model = self.graph(value); model.pop('creation'); model.update(operation='insertNetwork', insertAfterNetworkId='3')
        plan['steps'][1]['prepareArguments']['payload']['blockPath'] = path
        self.sync_effect(value)
        return value

    def test_original_retained_preview_preserves_unverified_boundaries(self):
        raw = self.path.read_bytes(); result = self.validate()
        for key in ('componentPreviewValidated', 'componentStepGraphBound', 'componentContentPinsBound'):
            self.assertTrue(result[key])
        for key in ('componentArtifactBytesVerified', 'componentInventoryVerified', 'componentInterfaceExportVerified',
                    'componentNativeSelectorsVerified', 'componentPreservationVerified', 'inventoryFingerprintRecomputed',
                    'referencedFilesRead', 'nativeExecuted', 'qualificationGranted'):
            self.assertFalse(result[key])
        self.assertEqual(raw, self.path.read_bytes())

    def test_operation_only_positive_control_accepts_changed_call(self):
        value = copy.deepcopy(self.preview)
        self.graph(value)['nodes'][0]['calleePath'] = '/other/FB'
        self.sync_effect(value)
        self.validate(value, 'operation-plan')
        with self.assertRaises(ValueError): self.validate(value)

    def test_content_and_descriptor_hashes_must_agree(self):
        self.reject(lambda p: p['integrationPlan']['versions'][0].update(contentSha256='a' * 64))
        self.reject(lambda p: p['integrationPlan']['versions'][0]['artifact'].update(sha256='a' * 64))

    def test_oracle_pins_and_fixed_policy_cannot_drift(self):
        self.reject(lambda p: p['integrationPlan']['versions'][0]['contentPolicy'].update(sourceOracleSha256='a' * 64))
        self.reject(lambda p: p['integrationPlan']['versions'][0]['contentPolicy']['properties'].update(MemoryLayout='Standard'))
        self.reject(lambda p: p['integrationPlan']['versions'][1].update(contentPolicy=copy.deepcopy(p['integrationPlan']['versions'][0]['contentPolicy'])))

    def test_artifact_sizes_are_bounded_exact_integers(self):
        for count in (-1, None, True, '1', 1.5, 4194305):
            with self.subTest(count=count):
                self.reject(lambda p: p['integrationPlan']['versions'][0]['artifact'].update(bytes=count))
        p = copy.deepcopy(self.preview); p['integrationPlan']['versions'][0]['artifact'].pop('bytes')
        self.validate(p)

    def test_artifact_paths_must_be_absolute(self):
        for path in ('relative.xml', 'C:relative.xml', ' '):
            self.reject(lambda p: p['integrationPlan']['versions'][0]['artifact'].update(path=path))

    def test_duplicate_and_missing_versions_refuse(self):
        self.reject(lambda p: p['integrationPlan']['versions'].append(copy.deepcopy(p['integrationPlan']['versions'][0])))
        self.reject(lambda p: p['integrationPlan']['versions'].pop())
        self.reject(lambda p: p['integrationPlan']['versions'][1].update(typeGuid=p['integrationPlan']['versions'][0]['typeGuid']))

    def test_dependency_graph_must_be_closed_and_reachable(self):
        self.reject(lambda p: p['integrationPlan']['versions'][0]['requiredVersionGuids'].append('00000000-0000-0000-0000-000000000001'))
        self.reject(lambda p: p['integrationPlan']['versions'][0]['requiredVersionGuids'].clear())

    def test_outer_dependency_capture_binds_every_version(self):
        self.reject(lambda p: p['dependencies'].clear())
        self.reject(lambda p: p['dependencies'][0]['versions'].pop())
        self.reject(lambda p: p['dependencies'][0]['versions'][0].update(author='Changed'))
        self.reject(lambda p: p['dependencies'][0]['versions'].append(copy.deepcopy(p['dependencies'][0]['versions'][0])))

    def test_only_committed_usable_native_plc_versions(self):
        self.reject(lambda p: p['integrationPlan']['versions'][0].update(state='InWork'))
        self.reject(lambda p: p['integrationPlan']['versions'][0].update(doNotUse=True))
        self.reject(lambda p: p['integrationPlan']['versions'][0].update(engineeringType='Siemens.Engineering.Hmi.Screen.ScreenLibraryTypeVersion'))

    def test_target_and_compile_scope_must_match(self):
        self.reject(lambda p: p.update(targetScope='/other'))
        self.reject(lambda p: p.update(compileTarget='/other'))
        self.reject(lambda p: p['integrationPlan']['steps'][1]['prepareArguments'].update(plcPath='/other'))

    def test_parameters_must_be_complete_unique_and_ordered(self):
        self.reject(lambda p: p['integrationPlan']['interface'].pop())
        self.reject(lambda p: p['integrationPlan']['interface'].reverse())
        self.reject(lambda p: p['integrationPlan']['interface'][1].update(name='enable'))
        self.reject(lambda p: p['integrationPlan']['mappings'][0].update(direction='InOut'))

    def test_native_tag_type_path_and_owner_are_bound(self):
        for key, value in (('dataType','Int'), ('path','/other'), ('tablePath','/other'), ('name','Other')):
            self.reject(lambda p: p['integrationPlan']['mappings'][0]['nativeTag'].update({key:value}))
        self.reject(lambda p: p['integrationPlan']['mappings'][0]['nativeTag'].update(isSafety=True))

    def test_writable_physical_input_refuses(self):
        for address in ('%I0.1', 'e0.1'):
            self.reject(lambda p: p['integrationPlan']['mappings'][1]['nativeTag'].update(logicalAddress=address))

    def shared_tag(self, direction):
        p = copy.deepcopy(self.preview); plan = p['integrationPlan']; graph = self.graph(p)
        tag = copy.deepcopy(plan['mappings'][0]['nativeTag'])
        for index in range(2):
            for row in (p['payload']['integration']['mappings'][index], plan['interface'][index], plan['mappings'][index]):
                row['direction'] = direction
            plan['mappings'][index]['nativeTag'] = copy.deepcopy(tag)
            for row in (p['payload']['integration']['mappings'][index], plan['mappings'][index]): row['tagPath'] = tag['path']
            graph['operands'][index]['components'] = [tag['name']]
            parameter = plan['interface'][index]['name']
            edges = ({'node':'$p'+str(index),'port':'value'}, {'node':'call','port':parameter})
            graph['connections'][index] = dict(zip(('from','to'), edges if direction == 'Input' else reversed(edges)))
        self.sync_effect(p)
        return p

    def test_shared_read_tag_is_allowed_but_multiple_writers_refuse(self):
        self.validate(self.shared_tag('Input'))
        with self.assertRaises(ValueError): self.validate(self.shared_tag('Output'))

    def test_repeated_tag_observations_cannot_disagree(self):
        p = self.shared_tag('Input'); p['integrationPlan']['mappings'][1]['nativeTag']['logicalAddress'] = '%M999.0'
        with self.assertRaises(ValueError): self.validate(p)

    def test_unicode_full_casing_does_not_merge_distinct_native_names(self):
        from urllib.parse import quote
        p = copy.deepcopy(self.preview); plan = p['integrationPlan']; graph = self.graph(p)
        for index, name in enumerate(('Straße', 'STRASSE')):
            tag = plan['mappings'][index]['nativeTag']; tag.update(name=name,path=tag['tablePath']+'/tags/'+quote(name,safe=''))
            for row in (p['payload']['integration']['mappings'][index], plan['mappings'][index]): row['tagPath'] = tag['path']
            graph['operands'][index]['components'] = [name]
        self.sync_effect(p); self.validate(p)

    def test_hmi_recipe_cannot_be_checked_as_a_plc_component(self):
        f = operation_fixtures.LibraryOperationObservations(); f.setUp()
        p = f.hmi('faceplate_create','unified')
        p['integrationPlan'] = copy.deepcopy(self.preview['integrationPlan'])
        p.update(applySupported=False,applyError='PlcIntegrationNotQualified')
        with self.assertRaises(ValueError): self.validate(p)

    def test_graph_direction_and_enable_are_bound(self):
        self.reject(lambda p: self.graph(p)['connections'][1].update({'from':{'node':'$p1','port':'value'}}))
        self.reject(lambda p: self.graph(p)['operands'][-1].update(value='false'))
        self.reject(lambda p: self.graph(p)['connections'].pop())
        self.reject(lambda p: self.graph(p)['nodes'].append({'key':'extra','kind':'call'}))

    def test_graph_operands_and_instances_are_bound(self):
        self.reject(lambda p: self.graph(p)['operands'][0].update(components=['Other']))
        self.reject(lambda p: p['integrationPlan']['steps'][1]['prepareArguments']['payload']['instances'][0].update(name='Other'))

    def test_step_order_freshness_and_import_selection(self):
        self.reject(lambda p: p['integrationPlan']['steps'].reverse())
        self.reject(lambda p: p['integrationPlan']['steps'][0].update(requiresFreshPreparation=False))
        self.reject(lambda p: p['integrationPlan']['steps'][1].pop('mustWaitFor'))
        self.reject(lambda p: p['integrationPlan']['steps'][0]['prepareArguments']['payload']['selection'].clear())

    def test_effect_cannot_drift_or_repeat(self):
        self.reject(lambda p: next(e for e in p['effects'] if e.get('action') == 'planPlcComponent').update(nativeMutation=True))
        self.reject(lambda p: p['effects'].append(copy.deepcopy(p['effects'][0])))

    def test_provisional_number_is_observed_not_recomputed(self):
        p = copy.deepcopy(self.preview); p['integrationPlan']['instanceDb']['provisionalNumber'] = 123
        self.assertFalse(self.validate(p)['componentInventoryVerified'])
        for number in (0, 65536, True):
            self.reject(lambda p: p['integrationPlan']['instanceDb'].update(provisionalNumber=number))

    def test_caller_mode_identity_and_db_path(self):
        self.reject(lambda p: p['integrationPlan']['caller'].update(initiallyAbsent=False))
        self.reject(lambda p: p['integrationPlan']['caller'].update(obCallAdded=True))
        self.reject(lambda p: p['integrationPlan']['instanceDb'].update(path='/other/DB'))
        self.reject(lambda p: p['integrationPlan'].update(callerArtifact=p['integrationPlan']['versions'][0]['artifact']))

    def test_existing_caller_legacy_and_current_artifact_scope(self):
        p = self.existing(); self.assertFalse(self.validate(p)['componentCallerArtifactRetained'])
        p['integrationPlan']['callerArtifact'] = dict(path='D:/Synthetic/Caller.xml', sha256='a' * 64, bytes=100)
        self.assertTrue(self.validate(p)['componentCallerArtifactRetained'])
        p['integrationPlan']['callerArtifact']['path'] = 'relative.xml'
        with self.assertRaises(ValueError): self.validate(p)

    def test_existing_insertion_and_blockers_are_retained(self):
        p = self.existing()
        p['integrationPlan']['blockers'] = [dict(code='NativeIdPreservationUnsupported', message='Following native IDs.')]
        self.validate(p)
        self.graph(p)['insertAfterNetworkId'] = '7'; self.sync_effect(p)
        with self.assertRaises(ValueError): self.validate(p)

    def test_lad_rail_and_inout_are_supported(self):
        p = copy.deepcopy(self.preview); plan = p['integrationPlan']; graph = self.graph(p)
        p['payload']['integration']['caller']['language'] = plan['caller']['language'] = graph['language'] = 'LAD'
        graph['operands'].pop(); graph['connections'].pop()
        graph['nodes'].insert(0, dict(key='rail',kind='powerrail'))
        graph['connections'].insert(0, {'from':{'node':'rail','port':'out'},'to':{'node':'call','port':'en'}})
        for row in (p['payload']['integration']['mappings'][1], plan['interface'][1], plan['mappings'][1]): row['direction'] = 'InOut'
        graph['connections'][2] = {'from':{'node':'$p1','port':'value'},'to':{'node':'call','port':'Active'}}
        self.sync_effect(p); self.validate(p)

    def test_no_native_or_atomicity_claim_can_be_added(self):
        for section, field in (('coordinator','automaticReplayAllowed'),('coordinator','publicEntryPointAvailable'),
                               ('recoveryPlan','separateStepsAreAtomic')):
            self.reject(lambda p: p['integrationPlan'][section].update({field:True}))
        self.reject(lambda p: p['integrationPlan'].update(nativeMutationDispatched=True))

    def test_prepared_change_preserves_no_execution(self):
        f = operation_fixtures.LibraryOperationObservations(); f.setUp()
        f.change['journal']['sessionGeneration'] = self.preview['sessionGeneration']
        response = f.response(self.preview, state='prepared')
        self.assertTrue(self.validate(response, 'component-change')['componentPreviewValidated'])
        with self.assertRaises(ValueError): self.validate(f.response(self.preview, {'objects':[]}), 'component-change')

    def test_original_artifact_paths_are_never_opened(self):
        self.validate()  # Load local schemas before refusing any further file reads.
        with patch.object(Path, 'open', side_effect=AssertionError('Unexpected file read')):
            self.assertFalse(self.validate()['componentArtifactBytesVerified'])

    def test_relocated_cli_uses_only_local_resources(self):
        with tempfile.TemporaryDirectory() as temporary:
            target = Path(temporary); (target/'scripts').mkdir(); (target/'contracts').mkdir()
            for source in [validation.ROOT/'scripts/validate_library_observations.py', *list((validation.ROOT/'scripts').glob('library_*observations.py'))]:
                shutil.copyfile(source, target/'scripts'/source.name)
            for source in (validation.ROOT/'contracts').glob('*.schema.json'):
                shutil.copyfile(source, target/'contracts'/source.name)
            raw = self.path.read_bytes(); observation = target/'plan.json'; observation.write_bytes(raw)
            args = [sys.executable,'-B',str(target/'scripts/validate_library_observations.py'),'--kind','component-plan','--path',str(observation),
                    '--expected-sha256',hashlib.sha256(raw).hexdigest()]
            run = subprocess.run(args, cwd=target, env={k:v for k,v in os.environ.items() if k.upper()!='PYTHONPATH'}, capture_output=True,text=True)
            self.assertEqual(0,run.returncode,run.stdout+run.stderr)
            self.assertTrue(json.loads(run.stdout)['componentPreviewValidated']); self.assertEqual(raw,observation.read_bytes())


if __name__ == '__main__': unittest.main()
