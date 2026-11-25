import copy
import json
import unittest

import validate_library_observations as validation
import test_library_operation_observations as operation_fixtures


class LibraryUpdateObservations(unittest.TestCase):
    def setUp(self):
        self.plan = json.loads((validation.ROOT / 'fixtures/libraries/observation-contracts/update-plan.json').read_bytes())

    def validate(self, plan=None, kind='update-plan'):
        return validation.validate_bytes(json.dumps(self.plan if plan is None else plan).encode(), kind)

    def reject(self, mutate):
        plan = copy.deepcopy(self.plan)
        mutate(plan)
        with self.assertRaises(ValueError):
            self.validate(plan)

    def test_retained_preview_validates_without_native_authority(self):
        result = self.validate()
        for flag in ('updateAnalysisValidated', 'updateComparisonsComplete', 'recordedCleanupReachabilityValidated'):
            self.assertTrue(result[flag])
        for flag in ('nativeReferenceGraphVerified', 'engineeringContentPreservationVerified', 'nativeExecuted', 'qualificationGranted', 'referencedFilesRead'):
            self.assertFalse(result[flag])

    def test_each_analysis_required_and_operation_specific(self):
        for field in ('updateImpact', 'updateBaseline', 'updateCleanupGuard'):
            self.reject(lambda p: p.pop(field))
        self.reject(lambda p: p.update(kind='library_instantiate'))

    def test_envelope_mode_still_accepts_opaque_analyses(self):
        plan = copy.deepcopy(self.plan); plan['updateImpact'] = {'opaque': True}
        self.validate(plan, 'plan')

    def test_change_mode_checks_both_retained_previews(self):
        fixtures = operation_fixtures.LibraryOperationObservations(); fixtures.setUp()
        result = {'objects': [self.plan['updateImpact']['types'][0]['requested']]}
        value = fixtures.response(self.plan, result)
        value['journal']['sessionGeneration'] = self.plan['sessionGeneration']
        self.assertTrue(self.validate(value, 'update-change')['updateAnalysisValidated'])
        value['preview']['updateImpact']['noOp'] = True
        with self.assertRaises(ValueError): self.validate(value, 'update-change')

    def test_baseline_affected_links_and_counts_are_bound(self):
        for mutate in (
            lambda p: p['updateBaseline']['uses'][0].update(versionGuid='00000000-0000-0000-0000-000000000000'),
            lambda p: p['updateImpact']['affectedUses'].clear(),
            lambda p: p['updateImpact']['types'][0].update(affectedUseCount=0),
            lambda p: p['updateImpact']['affectedUses'][0].update(requestedVersion='9.9'),
            lambda p: p['updateImpact']['affectedUses'][0].update(beforeVersion='9.9'),
            lambda p: p['updateImpact']['affectedUses'][0].update(action='keepExactVersion'),
            lambda p: p['updateImpact'].update(noOp=True),
            lambda p: p['updateImpact']['types'][0].update(scope='librarySynchronizationOnly_NoNewEngineeringInstance')):
            self.reject(mutate)

    def test_forced_default_is_bound_to_payload_roots_and_project_default(self):
        plan = copy.deepcopy(self.plan); row = plan['updateImpact']['types'][0]
        newer = dict(copy.deepcopy(row['requested']), version='2.0.0', versionGuid='11111111-1111-1111-1111-111111111111')
        plan['payload']['forceDefault'] = True; plan['effects'][0]['forceDefault'] = True; plan['updateImpact']['forceDefault'] = True
        row.update(forceDefault=True, projectDefaultBefore=newer, defaultEffect='forcedDowngrade')
        self.validate(plan)
        for mutate in (lambda p: p['payload'].pop('forceDefault'),
                       lambda p: p['updateImpact']['types'][0].update(defaultEffect='forcedDefault'),
                       lambda p: p['updateImpact']['types'][0].update(forceDefault=False, defaultEffect='promoteIfHigher')):
            p = copy.deepcopy(plan); mutate(p)
            with self.assertRaises(ValueError): self.validate(p)

    def test_peer_plc_uses_exclude_the_selected_plc(self):
        plc = self.plan['updateImpact']['plcPath']; version = self.plan['updateImpact']['types'][0]['requested']
        peer = dict(typeGuid=version['typeGuid'], versionGuid=version['versionGuid'], uses=1)
        self.plan['updateBaseline']['peerPlcUses'] = [dict(peer, plcPath=plc + '2')]
        self.validate()
        self.reject(lambda p: p['updateBaseline']['peerPlcUses'][0].update(plcPath=plc))
        self.reject(lambda p: p['updateBaseline']['peerPlcUses'].append(copy.deepcopy(p['updateBaseline']['peerPlcUses'][0])))

    def test_duplicate_observations_refuse(self):
        for parent, field in (('updateBaseline', 'uses'), ('updateBaseline', 'objectPaths'),
                              ('updateImpact', 'affectedUses'), ('updateImpact', 'types'),
                              ('updateCleanupGuard', 'proofEdges'), ('updateCleanupGuard', 'obRoots')):
            self.reject(lambda p: p[parent][field].append(copy.deepcopy(p[parent][field][0])))

    def test_requested_and_eligible_versions_bind_dependency_closure(self):
        for mutate in (
            lambda p: p['dependencies'].clear(),
            lambda p: p['dependencies'].append(copy.deepcopy(p['dependencies'][0])),
            lambda p: p['dependencies'][0]['versions'].clear(),
            lambda p: p['dependencies'][0]['versions'][0]['requiredVersionGuids'].append('00000000-0000-0000-0000-000000000000'),
            lambda p: p['updateImpact']['types'][0]['requested'].update(version='8.0'),
            lambda p: p['updateImpact']['types'][0]['libraryVersionsEligibleForCopy'].clear(),
            lambda p: p['updateImpact']['types'][0]['libraryVersionsEligibleForCopy'][0].update(isDefault=True),
            lambda p: p['updateImpact']['types'][0]['libraryVersionsEligibleForCopy'][0].update(version='8.0')):
            self.reject(mutate)

    def test_unavailable_comparisons_are_evidence_not_success(self):
        row = self.plan['updateImpact']['types'][0]['nativeComparisons'][0]
        self.plan['updateImpact']['types'][0]['nativeComparisons'] = [
            {'beforeVersionGuid': row['beforeVersionGuid'], 'comparisonAvailable': False, 'reason': 'Recorded failure'}]
        self.plan['updateImpact']['nativeComparisonsComplete'] = False
        self.assertFalse(self.validate()['updateComparisonsComplete'])
        self.reject(lambda p: p['updateImpact'].update(nativeComparisonsComplete=True))

    def test_comparison_coverage_and_property_bound(self):
        self.reject(lambda p: p['updateImpact']['types'][0]['nativeComparisons'].clear())
        self.reject(lambda p: p['updateImpact']['types'][0]['nativeComparisons'][0].update(requestedVersionGuid='00000000-0000-0000-0000-000000000000'))
        self.reject(lambda p: p['updateImpact']['types'][0]['nativeComparisons'][0].update(properties=[{'description':None,'status':'NotCompared','before':'','after':''}] * 201))

    def test_exact_version_noop_has_no_comparison(self):
        impact = self.plan['updateImpact']; row = impact['affectedUses'][0]
        row.update(beforeVersionGuid=row['requestedVersionGuid'], beforeVersion=row['requestedVersion'], action='keepExactVersion')
        self.plan['updateBaseline']['uses'][0]['versionGuid'] = row['requestedVersionGuid']
        impact['types'][0]['nativeComparisons'] = []; impact['noOp'] = True
        self.validate()
        self.reject(lambda p: p['updateImpact']['affectedUses'][0].update(beforeVersion='7.0'))

    def test_dependency_without_uses_is_library_synchronization_only(self):
        version = copy.deepcopy(self.plan['updateImpact']['types'][0]['requested'])
        version.update(typeGuid='00000000-0000-0000-0000-000000000001', versionGuid='00000000-0000-0000-0000-000000000002')
        self.plan['dependencies'][0]['versions'][0]['requiredVersionGuids'] = [version['versionGuid']]
        self.plan['dependencies'][0]['versions'].append({**version,'requiredVersionGuids':[]})
        self.plan['updateImpact']['types'].append({'requested':version,'affectedUseCount':0,
            'scope':'librarySynchronizationOnly_NoNewEngineeringInstance','nativeComparisons':[],
            'libraryVersionsEligibleForCopy':[copy.deepcopy(version)]})
        self.validate()
        self.reject(lambda p: p['dependencies'][0]['versions'][0]['requiredVersionGuids'].clear())

    def test_multiple_uses_share_one_native_version_comparison(self):
        affected = copy.deepcopy(self.plan['updateImpact']['affectedUses'][0])
        affected['objectPath'] += '_Additional'
        self.plan['updateImpact']['affectedUses'].append(affected)
        self.plan['updateImpact']['types'][0]['affectedUseCount'] += 1
        self.plan['updateBaseline']['uses'].append({'objectPath':affected['objectPath'],
            'typeGuid':affected['typeGuid'],'versionGuid':affected['beforeVersionGuid']})
        self.plan['updateBaseline']['objectPaths'].append(affected['objectPath'])
        guard = self.plan['updateCleanupGuard']
        guard['requiredLibraryUses'].append(affected['objectPath']); guard['reachablePaths'].append(affected['objectPath'])
        guard['proofEdges'].append({'callerPath':guard['obRoots'][0],'calleePath':affected['objectPath']})
        self.validate()
        self.reject(lambda p: p['updateImpact']['affectedUses'][1].update(beforeVersion='8.0'))

    def test_missing_unrelated_use_proof_refuses(self):
        self.reject(lambda p: p['updateCleanupGuard']['requiredLibraryUses'].pop())
        self.reject(lambda p: p['updateCleanupGuard']['reachablePaths'].pop())
        self.reject(lambda p: p['updateCleanupGuard']['proofEdges'].clear())

    def test_changed_objects_cannot_supply_proof_edges(self):
        changed = self.plan['updateImpact']['affectedUses'][0]['objectPath']
        other = self.plan['updateBaseline']['uses'][1]['objectPath']
        self.reject(lambda p: p['updateCleanupGuard']['proofEdges'].append({'callerPath':changed,'calleePath':other}))

    def test_disconnected_cycle_does_not_prove_required_uses(self):
        guard = self.plan['updateCleanupGuard']
        a = self.plan['updateBaseline']['uses'][1]['objectPath']
        b = next(p for p in self.plan['updateBaseline']['objectPaths'] if p.endswith('/TiaMcpLibraryState_DB'))
        guard['proofEdges'] = [e for e in guard['proofEdges'] if e['calleePath'] not in (a,b)]
        guard['proofEdges'] += [{'callerPath':a,'calleePath':b},{'callerPath':b,'calleePath':a}]
        guard['reachablePaths'] = [p for p in guard['reachablePaths'] if p not in (a,b)]
        with self.assertRaises(ValueError): self.validate()

    def test_unavailable_cross_reference_service_does_not_erase_positive_evidence(self):
        self.plan['updateCleanupGuard']['objectsWithoutCrossReferenceService'] = self.plan['updateBaseline']['objectPaths'][:]
        self.validate()

    def test_unknown_endpoints_empty_roots_and_self_edges_refuse(self):
        for mutate in (
            lambda p: p['updateCleanupGuard']['obRoots'].clear(),
            lambda p: p['updateCleanupGuard']['proofEdges'][0].update(calleePath='/Unknown'),
            lambda p: p['updateCleanupGuard']['proofEdges'][0].update(calleePath=p['updateCleanupGuard']['proofEdges'][0]['callerPath'])):
            self.reject(mutate)

    def test_scope_and_qualification_claims_cannot_be_promoted(self):
        for parent, field in (('updateImpact','usesScopeComplete'),('updateBaseline','scopeComplete'),('updateCleanupGuard','allRequiredUsesProven')):
            self.reject(lambda p: p[parent].update({field:False}))
        for parent, field in (('updateImpact','runtimeCompatibilityVerified'),('updateCleanupGuard','completeReferenceGraphClaimed'),('updateCleanupGuard','nativeCleanupDisabled'),('updateCleanupGuard','runtimeReachabilityClaimed')):
            self.reject(lambda p: p[parent].update({field:True}))

    def test_canonical_paths_and_exact_plc_scope(self):
        for invalid in ('/a//b', '/a/../b', '/a/./b', '/a\\b', '/a\n', '/a\x85', '/'+ '\U0001f600'*2048):
            self.reject(lambda p: p['updateBaseline']['objectPaths'].append(invalid))
        self.reject(lambda p: p['payload']['target'].update(plcPath='/OtherPLC'))
        self.reject(lambda p: p['updateBaseline'].update(plcPath='/OtherPLC'))
        self.reject(lambda p: p['updateBaseline']['objectPaths'].append('/OtherPLC/blocks/X'))


if __name__ == '__main__':
    unittest.main()
