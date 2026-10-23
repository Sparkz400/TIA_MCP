import copy
import json
import unittest

import validate_library_observations as validation


class LibrarySynchronizationObservations(unittest.TestCase):
    def setUp(self):
        self.change = json.loads((validation.ROOT / 'fixtures/libraries/observation-contracts/synchronization-change.json').read_bytes())
        self.plan = self.change['journal']['preview']

    def validate(self, value=None, kind='sync-plan'):
        return validation.validate_bytes(json.dumps(self.plan if value is None else value).encode(), kind)

    def reject(self, mutate):
        value = copy.deepcopy(self.plan); mutate(value)
        with self.assertRaises(ValueError): self.validate(value)

    def change_with_plan(self):
        self.change['preview'] = copy.deepcopy(self.plan)
        return self.change

    def add_transitive_type(self):
        release = copy.deepcopy(self.plan['dependencies'][0]['versions'][0])
        release.update(typeGuid='00000000-0000-0000-0000-000000000001',
                       versionGuid='00000000-0000-0000-0000-000000000002',
                       typeName='Dependency',isDefault=True)
        original = self.plan['dependencies'][0]
        original['versions'][0]['requiredVersionGuids'] = [release['versionGuid']]
        original['versions'].append(copy.deepcopy(release))
        self.plan['dependencies'].append({'rootVersionGuid':release['versionGuid'],'versions':[copy.deepcopy(release)]})
        default = {k:v for k,v in release.items() if k != 'requiredVersionGuids'}
        self.plan['typeSynchronization'].append({'typeGuid':release['typeGuid'],'typeName':release['typeName'],
            'nativeForceUpdateMode':'SetOnlyHigherUpdatedVersionAsDefault','forceUpdate':False,'beforeDefault':None,
            'sourceDefault':copy.deepcopy(default),'expectedDefault':copy.deepcopy(default),
            'defaultChange':True,'defaultEffect':'initializeDefault','deleteVersions':False,
            'expectedVersions':[{'versionGuid':release['versionGuid'],'versionNumber':release['version']}]})

    def test_original_plan_and_change_remain_observations(self):
        result = self.validate()
        self.assertTrue(result['synchronizationPlanValidated'])
        self.assertFalse(result['synchronizationResultBound'])
        result = self.validate(self.change, 'sync-change')
        self.assertTrue(result['synchronizationResultBound'])
        for key in ('nativeSynchronizationVerified','destinationBeforeInventoryVerified','masterCopyContentVerified','qualificationGranted','nativeExecuted','referencedFilesRead'):
            self.assertFalse(result[key])

    def test_initialization_requires_exact_source_versions(self):
        row = self.plan['typeSynchronization'][0]
        row.update(beforeDefault=None,defaultEffect='initializeDefault')
        self.validate()
        self.reject(lambda p: p['typeSynchronization'][0]['expectedVersions'].append({'versionGuid':'00000000-0000-0000-0000-000000000001','versionNumber':'0.9'}))

    def test_higher_destination_default_is_preserved(self):
        row = self.plan['typeSynchronization'][0]
        before = copy.deepcopy(row['beforeDefault']); before.update(versionGuid='00000000-0000-0000-0000-000000000001',version='2.0')
        row.update(beforeDefault=before,expectedDefault=copy.deepcopy(before),defaultChange=False,defaultEffect='preserveDefault')
        row['expectedVersions'].append({'versionGuid':before['versionGuid'],'versionNumber':before['version']})
        self.validate()
        self.reject(lambda p: p['typeSynchronization'][0].update(expectedDefault=copy.deepcopy(p['typeSynchronization'][0]['sourceDefault'])))

    def test_equal_identity_preserves_destination_metadata(self):
        row = self.plan['typeSynchronization'][0]
        before = copy.deepcopy(row['sourceDefault']); before['author'] = 'Destination author'
        row.update(beforeDefault=before,expectedDefault=copy.deepcopy(before),defaultChange=False,defaultEffect='preserveDefault')
        self.validate()
        self.reject(lambda p: p['typeSynchronization'][0]['expectedDefault'].update(author='Source author'))

    def test_default_policy_flags_are_computed(self):
        for field,wrong in (('defaultEffect','preserveDefault'),('defaultChange',False),('forceUpdate',True),('deleteVersions',True),('nativeForceUpdateMode','Force')):
            self.reject(lambda p: p['typeSynchronization'][0].update({field:wrong}))

    def test_expected_union_binds_all_source_releases(self):
        for mutate in (
            lambda p: p['typeSynchronization'][0]['expectedVersions'].pop(0),
            lambda p: p['typeSynchronization'][0]['expectedVersions'][0].update(versionNumber='0.7'),
            lambda p: p['typeSynchronization'].clear(),
            lambda p: p['typeSynchronization'][0].update(typeName='Other')):
            self.reject(mutate)

    def test_same_number_different_guid_collision_refuses(self):
        self.reject(lambda p: p['typeSynchronization'][0]['expectedVersions'].append({'versionGuid':'00000000-0000-0000-0000-000000000001','versionNumber':'01.00.0'}))

    def test_version_comparison_uses_system_version_component_order(self):
        row = self.plan['typeSynchronization'][0]
        before = copy.deepcopy(row['beforeDefault']); before.update(versionGuid='00000000-0000-0000-0000-000000000001',version='1.10.0')
        row.update(beforeDefault=before,expectedDefault=copy.deepcopy(before),defaultChange=False,defaultEffect='preserveDefault')
        row['expectedVersions'].append({'versionGuid':before['versionGuid'],'versionNumber':before['version']})
        self.validate()
        for invalid in ('1','1.0.0.0.0','1.-1','2147483648.0','1.0e2'):
            self.reject(lambda p: p['typeSynchronization'][0]['expectedVersions'][-1].update(versionNumber=invalid))

    def test_every_source_release_requires_its_own_root(self):
        self.reject(lambda p: p['dependencies'].pop(0))
        self.reject(lambda p: p['dependencies'].append(copy.deepcopy(p['dependencies'][0])))

    def test_missing_and_extraneous_dependency_members_refuse(self):
        self.reject(lambda p: p['dependencies'][0]['versions'][0]['requiredVersionGuids'].append('00000000-0000-0000-0000-000000000001'))
        self.reject(lambda p: p['dependencies'][0]['versions'].append(copy.deepcopy(p['dependencies'][1]['versions'][0])))

    def test_source_usable_committed_default_is_required(self):
        for field,wrong in (('state','InWork'),('doNotUse',True),('isDefault',True)):
            self.reject(lambda p: p['dependencies'][0]['versions'][0].update({field:wrong}))
        self.reject(lambda p: p['typeSynchronization'][0]['sourceDefault'].update(isDefault=False))

    def test_copy_effects_bind_complete_selected_releases(self):
        self.reject(lambda p: p['effects'][0]['versions'].pop(0))
        self.reject(lambda p: p['effects'].pop(0))
        self.reject(lambda p: p['effects'].append(copy.deepcopy(p['effects'][0])))
        self.reject(lambda p: p['effects'][0]['versions'][0].update(author='Altered'))

    def test_operation_effect_binds_kind_destination_and_project(self):
        for field,wrong in (('action','library_copy_elements'),('targetLibraryId','wrong'),('projectPath','D:/Other.ap21')):
            self.reject(lambda p: p['effects'][-1].update({field:wrong}))
        self.plan['kind'] = 'library_copy_elements'; self.plan['effects'][-1]['action'] = 'library_copy_elements'
        self.validate()

    def test_returned_union_and_default_must_match_plan(self):
        for mutate in (
            lambda r: r['typeSynchronization'].clear(),
            lambda r: r['typeSynchronization'][0]['versions'].pop(0),
            lambda r: r['typeSynchronization'][0]['versions'][0].update(version='9.0')):
            value = copy.deepcopy(self.change); mutate(value['journal']['result'])
            with self.assertRaises(ValueError): self.validate(value,'sync-change')
        value = copy.deepcopy(self.change); row = value['journal']['result']['typeSynchronization'][0]
        for r in row['versions']: r['isDefault'] = r['versionGuid'] == row['versions'][0]['versionGuid']
        row['defaultVersion'] = copy.deepcopy(row['versions'][0])
        # Operation-only validation accepts this internally consistent result;
        # synchronization mode also binds it to the approved default.
        self.validate(value,'operation-change')
        with self.assertRaises(ValueError): self.validate(value,'sync-change')

    def test_failed_change_without_result_does_not_claim_bound_result(self):
        self.change['state'] = self.change['journal']['state'] = 'recovery_required'
        self.change['journal'].pop('result')
        self.assertFalse(self.validate(self.change,'sync-change')['synchronizationResultBound'])

    def test_old_envelope_mode_and_wrong_operation_scope(self):
        value = copy.deepcopy(self.plan); value['typeSynchronization'] = [{'opaque':True}]
        self.validate(value,'plan')
        self.reject(lambda p: p.update(kind='library_instantiate'))

    def test_master_only_copy_has_no_type_sync_claim(self):
        self.plan['selection'] = self.plan['payload']['selection'] = [{'masterCopyPath':'/masterCopies/Example'}]
        self.plan['dependencies'] = []; self.plan['effects'] = [self.plan['effects'][-1]]
        self.plan['typeSynchronization'] = []
        self.assertFalse(self.validate()['masterCopyContentVerified'])
        # An embedded master dependency does not populate CopyTypes.
        retained = copy.deepcopy(self.change['preview']['dependencies'][0]); retained['masterCopy'] = 'Example'
        self.plan['dependencies'] = [retained]
        self.validate()

    def test_unselected_disconnected_type_is_not_part_of_copy_closure(self):
        self.add_transitive_type()
        self.plan['dependencies'][0]['versions'][0]['requiredVersionGuids'].clear()
        self.plan['dependencies'][0]['versions'].pop()
        with self.assertRaises(ValueError): self.validate()

    def test_dependencies_of_older_releases_are_copied_too(self):
        self.add_transitive_type()
        self.validate()
        self.reject(lambda p: p['typeSynchronization'].pop())
        self.reject(lambda p: p['dependencies'].pop())

    def test_repeated_release_metadata_must_match(self):
        self.add_transitive_type()
        self.reject(lambda p: p['dependencies'][0]['versions'][-1].update(author='Altered'))

    def test_total_capture_budget_includes_known_destination_overlap(self):
        # 2,000 union pins plus the prior default known to exist on both sides
        # implies at least 2,001 captured source/destination releases.
        pins = self.plan['typeSynchronization'][0]['expectedVersions']
        for i in range(1998):
            pins.append({'versionGuid':f'00000000-0000-0000-0001-{i:012x}','versionNumber':f'2.{i}'})
        with self.assertRaises(ValueError): self.validate()

    def test_source_default_must_be_highest_committed_release(self):
        self.plan['dependencies'][0]['versions'][0]['version'] = '9.0'
        self.plan['effects'][0]['versions'][0]['version'] = '9.0'
        row = self.plan['typeSynchronization'][0]
        row['expectedVersions'][0]['versionNumber'] = row['beforeDefault']['version'] = '9.0'
        with self.assertRaises(ValueError): self.validate()


if __name__ == '__main__':
    unittest.main()
