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

import validate_library_observations as validation
import test_library_operation_observations as operation_fixtures
import test_library_hmi_preview_observations as hmi_fixtures


class LibraryPreviewEffectObservations(unittest.TestCase):
    def setUp(self):
        self.operation = operation_fixtures.LibraryOperationObservations(); self.operation.setUp()
        self.root = validation.ROOT/'fixtures/libraries/observation-contracts'

    def read(self, name): return json.loads((self.root/name).read_bytes())

    def plan(self, kind):
        if kind in ('library_copy_elements','library_publish'):
            p = self.read('synchronization-change.json')['journal']['preview']
            p['kind'] = kind; p['effects'][-1]['action'] = kind
            return p
        if kind == 'library_update_project': return self.read('update-plan.json')
        if kind == 'library_integrate': return self.read('component-plan.json')
        p = self.operation.plan(kind); payload = p['payload']
        if kind in ('library_instantiate','library_create_master_copy','library_create_type','library_edit_version','library_release_version','library_discard_version'):
            p['projectPath'] = 'D:/Synthetic/Fixture.ap21'
        resource_ids = {payload.get('source',{}).get('libraryId'),payload.get('destinationLibraryId')}
        p['resources'] = [r for r in p['resources'] if r['libraryId'] in resource_ids]
        if not payload.get('selection'):p['dependencies']=[]
        if kind in ('library_release_version','library_discard_version'):
            p['dependencies']=[dict(rootVersionGuid=p['selection'][0]['versionGuid'],scopeComplete=False,reason='InWork native dependencies unavailable.')]
        if kind in ('library_create','library_upgrade'): p.pop('projectPath',None)
        if kind in ('library_create','library_upgrade','library_archive'):
            p['outputAbsent'] = 'D:/Synthetic/Change/' + {'library_create':'created','library_upgrade':'upgraded','library_archive':'published.zal21'}[kind]
        if kind == 'library_upgrade':
            p.update(upgradeSourceHash=payload['expectedSha256'],upgradeSourceHashScope='Complete original archive bytes')
        if kind == 'library_create_type':
            p['documentsHash'] = 'a'*64
            p['documentSource'] = dict(originalDirectory=payload['documentDirectory'],snapshotDirectory='D:/Synthetic/Snapshot',sha256='a'*64,
                hashScope='Complete sorted companion-file paths and SHA-256 digests',ownedSnapshot=True)
        if kind == 'library_create_master_copy':
            plc = '/projects/Synthetic/devices/Station/items/CPU/plc/PLC'
            payload['object'] = dict(kind='block',objectPath=plc+'/blocks/Example')
            p['sourceObject'] = dict(projectPath=p['projectPath'],plcPath=plc,scopePath=plc,objectPath=payload['object']['objectPath'],kind='block',name='Example',namespace='')
        if kind == 'library_instantiate': p.update(targetScope='/PLC',compileTarget='/PLC')
        p['effects'] = [dict(action=kind,targetLibraryId=payload.get('destinationLibraryId') or payload.get('source',{}).get('libraryId'),projectPath=p.get('projectPath'))]
        return p

    def validate(self, value, kind='preview-plan'):
        return validation.validate_bytes(json.dumps(value).encode(),kind)

    def reject(self, kind, mutate):
        p = self.plan(kind); mutate(p)
        with self.assertRaises(ValueError): self.validate(p)

    def test_all_eighteen_operation_kinds_dispatch(self):
        for kind in validation.validator('operation').schema['properties']['kind']['enum']:
            with self.subTest(kind=kind):
                report = self.validate(self.plan(kind)); self.assertTrue(report['previewEffectsValidated'])
                self.assertFalse(report['nativeSelectorResolutionVerified']); self.assertFalse(report['qualificationGranted'])

    def test_terminal_effect_target_project_and_action(self):
        for field,value in (('targetLibraryId','library-'+'f'*32),('projectPath','D:/Other.ap21'),('action','library_delete')):
            self.reject('library_type_policy',lambda p:p['effects'][-1].update({field:value}))

    def test_unknown_duplicate_or_missing_effects_refuse(self):
        self.reject('library_type_policy',lambda p:p['effects'].append(copy.deepcopy(p['effects'][0])))
        self.reject('library_type_policy',lambda p:p['effects'].clear())
        self.reject('library_type_policy',lambda p:p['effects'].insert(0,{'action':'Unknown'}))

    def test_operation_only_positive_control_retains_opaque_effects(self):
        p = self.plan('library_type_policy'); p['effects'][0]['targetLibraryId'] = 'library-'+'f'*32
        self.validate(p,'operation-plan')
        with self.assertRaises(ValueError): self.validate(p)

    def test_extra_resource_cannot_be_smuggled_into_preview(self):
        self.reject('library_create',lambda p:p['resources'].append(copy.deepcopy(self.operation.base['resources'][0])))

    def test_general_consumption_dependency_closure_is_not_opaque(self):
        self.reject('library_instantiate',lambda p:p['dependencies'].clear())
        self.reject('library_instantiate',lambda p:p['dependencies'][0]['versions'][0]['requiredVersionGuids'].append('00000000-0000-0000-0000-000000000001'))
        self.reject('library_instantiate',lambda p:p['dependencies'][0]['versions'][0].update(doNotUse=True))

    def test_maintenance_unavailable_dependencies_remain_explicit(self):
        self.assertFalse(self.validate(self.plan('library_release_version'))['selectedDependencyObservationsComplete'])
        p=self.plan('library_instantiate');p['dependencies']=self.plan('library_release_version')['dependencies']
        with self.assertRaises(ValueError):self.validate(p)

    def test_duplicate_and_unselected_dependency_observations_refuse(self):
        self.reject('library_instantiate',lambda p:p['dependencies'].append(copy.deepcopy(p['dependencies'][0])))
        self.reject('library_create',lambda p:p.update(dependencies=copy.deepcopy(self.operation.base['dependencies'])))

    def test_output_destinations_have_explicit_operation_shape(self):
        for kind in ('library_create','library_upgrade','library_archive'):
            self.reject(kind,lambda p:p.pop('outputAbsent'))
            self.reject(kind,lambda p:p.update(outputAbsent='relative/created'))
            self.reject(kind,lambda p:p.update(outputAbsent='D:/Synthetic/other'))

    def test_archive_source_pin_matches_original_request(self):
        self.reject('library_upgrade',lambda p:p.update(upgradeSourceHash='f'*64))
        self.reject('library_upgrade',lambda p:p.update(upgradeSourceHashScope='Name only'))

    def test_directory_upgrade_hash_keeps_distinct_scope(self):
        p = self.plan('library_upgrade'); p['payload']['path']='D:/Synthetic/old.al20'
        p.update(upgradeSourceHash='f'*64,upgradeSourceHashScope='Sorted SHA-256 manifest of every relative companion file; bound to the approved preview hash')
        self.assertFalse(self.validate(p)['sourceSnapshotBytesVerified'])

    def test_document_source_and_owned_snapshot_are_bound(self):
        self.reject('library_create_type',lambda p:p['documentSource'].update(originalDirectory='D:/Other'))
        self.reject('library_create_type',lambda p:p['documentSource'].update(snapshotDirectory=p['documentSource']['originalDirectory']))
        self.reject('library_create_type',lambda p:p['documentSource'].update(hashScope='Names only'))
        self.reject('library_create_type',lambda p:p['documentSource'].update(sha256='f'*64))

    def test_document_directory_normalization_matches_windows(self):
        p = self.plan('library_create_type'); p['payload']['documentDirectory']='d:\\Synthetic\\Sub\\..\\Documents'
        self.validate(p)

    def test_operation_specific_sections_cannot_be_repurposed(self):
        self.reject('library_save',lambda p:p.update(outputAbsent='D:/Synthetic/created'))
        self.reject('library_create',lambda p:p.update(sourceObject=self.plan('library_create_master_copy')['sourceObject']))
        self.reject('library_instantiate',lambda p:p.update(integrationPlan={}))

    def test_unused_null_target_does_not_crash_strict_validation(self):
        for kind in ('library_create','library_create_master_copy','library_save'):
            p=self.plan(kind);p['payload']['target']=None
            with self.assertRaises(ValueError):self.validate(p)

    def test_required_project_context_cannot_disappear(self):
        for kind in ('library_instantiate','library_create_master_copy','library_create_type','library_edit_version'):
            self.reject(kind,lambda p:p.pop('projectPath'))

    def test_selector_project_and_hardware_owners_are_not_plc_objects(self):
        for kind,path,name in (('project','/projects/Synthetic','Synthetic'),('hardware','/projects/Synthetic/devices/Station/items/CPU','CPU')):
            p=self.plan('library_create_master_copy')
            p['sourceObject'].update(kind=kind,objectPath=path,name=name,plcPath=None,scopePath=None)
            p['payload']['object'].update(kind=kind,objectPath=path)
            self.validate(p)

    def test_selector_explicit_project_name_and_null_kind(self):
        p=self.plan('library_create_master_copy');p['payload']['object'].update(projectName='Synthetic',kind=None)
        self.validate(p)
        p['payload']['object']['projectName']='Other'
        with self.assertRaises(ValueError):self.validate(p)

    def test_update_analysis_and_effect_are_both_checked(self):
        self.reject('library_update_project',lambda p:p['effects'][0].update(forceDefault=True))
        self.reject('library_update_project',lambda p:p['effects'][0].update(plcPath='/other'))
        self.reject('library_update_project',lambda p:p['updateCleanupGuard']['proofEdges'].clear())
        self.reject('library_update_project',lambda p:p.pop('updateImpact'))

    def test_synchronization_and_component_details_dispatch(self):
        self.reject('library_publish',lambda p:p['typeSynchronization'][0].update(defaultChange=False))
        self.reject('library_integrate',lambda p:p['integrationPlan']['steps'][1]['prepareArguments']['payload']['model']['nodes'][0].update(calleePath='/Other'))

    def test_all_hmi_routes_dispatch(self):
        f = hmi_fixtures.LibraryHmiPreviewObservations(); f.setUp()
        for action,family in (('instantiate','classic'),('tag_create','unified'),('faceplate_create','unified'),('faceplate_bind','unified')):
            p=f.plan(action,family); source=p['payload'].get('source',{}).get('libraryId')
            p['resources']=[r for r in p['resources'] if r['libraryId']==source]
            p['effects']=[dict(action=p['kind'],targetLibraryId=source,projectPath=p['projectPath'])]
            self.assertTrue(self.validate(p)['hmiPreviewValidated'])
            p['targetPreview']['targetFingerprint']='invalid'
            with self.assertRaises(ValueError):self.validate(p)

    def test_source_selector_exact_path_kind_scope_and_project(self):
        for field,value in (('objectPath','/Other'),('kind','type'),('scopePath','/Other'),('plcPath','/Other'),('projectPath','D:/Other.ap21')):
            self.reject('library_create_master_copy',lambda p:p['payload']['object'].update({field:value}))
        self.reject('library_create_master_copy',lambda p:p['payload']['object'].pop('kind'))

    def test_source_identity_must_agree_with_its_canonical_path(self):
        for field,value in (('name','Other'),('kind','type'),('plcPath','/Other'),('scopePath','/Other'),('namespace','Other')):
            self.reject('library_create_master_copy',lambda p:p['sourceObject'].update({field:value}))

    def test_scope_and_namespaces_use_structural_positions(self):
        p=self.plan('library_create_master_copy'); identity=p['sourceObject']
        plc='/projects/Synthetic/groups/plc/devices/plc/items/plc/plc/PLC'
        scope=plc+'/units/Unit'; path=scope+'/blocks/groups/namespaces/namespaces/Area.Sub/Example'
        identity.update(plcPath=plc,scopePath=scope,objectPath=path,namespace='Area.Sub')
        p['payload']['object'].update(objectPath=path,namespace='Area.Sub',scopePath=scope)
        self.validate(p)

    def test_source_name_comparison_preserves_unicode_uncertainty(self):
        p=self.plan('library_create_master_copy'); p['payload']['object']['name']='EXAMPLE'
        self.assertEqual('ASCII-case-insensitive',self.validate(p)['sourceObjectNameComparison'])
        p['payload']['object']['name']='Other'
        with self.assertRaises(ValueError):self.validate(p)
        p['payload']['object']['name']='ÉXAMPLE'
        self.assertEqual('unverified-non-ASCII-case-comparison',self.validate(p)['sourceObjectNameComparison'])

    def test_legacy_namespace_omission_and_inferred_kind_are_explicit(self):
        p=self.plan('library_create_master_copy');p['sourceObject'].pop('namespace');p['payload']['object']['kind']=None
        self.assertFalse(self.validate(p)['sourceObjectNamespaceRetained'])
        p['payload']['object']['name']=123
        with self.assertRaises(ValueError):self.validate(p)

    def test_legacy_namespace_can_still_bind_a_requested_path_namespace(self):
        p=self.plan('library_create_master_copy');p['sourceObject'].pop('namespace')
        p['payload']['object']['namespace']='Other'
        with self.assertRaises(ValueError):self.validate(p)

    def recovery(self):
        p=self.plan('library_save');source=p['resources'][0]
        p['payload']['recoveryChangeId']='lib-'+'a'*32
        p['existingRecoveryCheckpoint']=dict(kind='library',originalPath=source['path'],archivePath='D:/Checkpoint.zal21',sha256='c'*64)
        p['effects'].insert(0,dict(action='persistCurrentPartialNativeState',recoveryChangeId=p['payload']['recoveryChangeId'],
            scope="All current unsaved content in the owned working copy. The prior failed operation's checkpoint remains the recovery baseline."))
        return p

    def test_partial_state_save_binds_checkpoint_and_failed_operation(self):
        p=self.recovery();self.assertFalse(self.validate(p)['recoveryCheckpointBytesVerified'])
        p['effects'][0]['recoveryChangeId']='lib-'+'b'*32
        with self.assertRaises(ValueError):self.validate(p)
        p=self.recovery();p['existingRecoveryCheckpoint']['originalPath']='D:/Other.al21'
        with self.assertRaises(ValueError):self.validate(p)

    def test_recovery_claims_cannot_be_detached_or_reused(self):
        p=self.recovery();p.pop('existingRecoveryCheckpoint')
        with self.assertRaises(ValueError):self.validate(p)
        self.reject('library_archive',lambda p:p.update(existingRecoveryCheckpoint=self.recovery()['existingRecoveryCheckpoint']))

    def component_import(self):
        p=self.plan('library_integrate');p['componentContentPins']=p['integrationPlan']['versions']
        p['payload']=p['integrationPlan']['steps'][0]['prepareArguments']['payload']
        for key in ('integrationPlan','applySupported','applyError'):p.pop(key)
        p['kind']='library_instantiate';p['effects']=[dict(action=p['kind'],targetLibraryId=p['payload']['source']['libraryId'],projectPath=p['projectPath'])]
        return p

    def test_internal_component_import_pins_bind_complete_metadata(self):
        p=self.component_import();self.assertTrue(self.validate(p)['componentImportContentPinsBound'])
        p['componentContentPins'].pop()
        with self.assertRaises(ValueError):self.validate(p)

    def test_internal_component_import_hash_and_policy_bindings(self):
        for mutate in (lambda p:p['componentContentPins'][0]['artifact'].update(sha256='f'*64),
                       lambda p:p['componentContentPins'][0]['contentPolicy'].update(sourceOracleSha256='f'*64),
                       lambda p:p['componentContentPins'][0].update(author='Other')):
            p=self.component_import();mutate(p)
            with self.assertRaises(ValueError):self.validate(p)

    def test_change_mode_includes_compiler_scope_validation(self):
        change=self.read('synchronization-change.json')
        self.assertTrue(self.validate(change,'preview-change')['synchronizationResultBound'])
        change=self.read('change.json')
        # Build a consistent prepared synthetic project response with no result.
        plan=self.plan('library_instantiate');change=self.operation.response(plan,state='prepared')
        self.assertEqual('not-present',self.validate(change,'preview-change')['compilerScopeBinding'])
        change['journal']['engineeringVerified']=True
        with self.assertRaises(ValueError):self.validate(change,'preview-change')

    def test_relocated_cli_preserves_original_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            target=Path(directory);(target/'scripts').mkdir();(target/'contracts').mkdir()
            for source in [validation.ROOT/'scripts/validate_library_observations.py',*list((validation.ROOT/'scripts').glob('library_*observations.py'))]:
                shutil.copyfile(source,target/'scripts'/source.name)
            for source in (validation.ROOT/'contracts').glob('*.schema.json'):shutil.copyfile(source,target/'contracts'/source.name)
            raw=(self.root/'synchronization-change.json').read_bytes();path=target/'change.json';path.write_bytes(raw)
            run=subprocess.run([sys.executable,'-B',str(target/'scripts/validate_library_observations.py'),'--kind','preview-change','--path',str(path),
                '--expected-sha256',hashlib.sha256(raw).hexdigest()],cwd=target,env={k:v for k,v in os.environ.items() if k.upper()!='PYTHONPATH'},capture_output=True,text=True)
            self.assertEqual(0,run.returncode,run.stdout+run.stderr);self.assertTrue(json.loads(run.stdout)['previewEffectsValidated'])
            self.assertEqual(raw,path.read_bytes())


if __name__=='__main__':unittest.main()
