"""Synthetic admission chains and real Windows archive sharing; no TIA dispatch."""
import copy
import json
import os
from pathlib import Path
import tempfile
import unittest

import native_dispatch_guard as guard
import qualify_translation_category_probe as coordinator
from provisioning_checkpoint_read import ProvisioningCheckpointRead, archive_ref, read_json
from test_qualify_translation_category_probe import FakeLocks


class ReadLocks(FakeLocks):
    def read_only(self, path):
        return self.lock(path)


class CheckpointReadTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.store, self.output, self.registry = (self.root/n for n in ('source', 'recovery', 'guard'))
        self.storage, self.project = self.store/'simulation/owned', self.store/'scratch/P/P.ap21'
        self.storage.mkdir(parents=True); self.project.parent.mkdir(parents=True)
        self.project.write_bytes(b'synthetic project')
        self.output.mkdir(); self.registry.mkdir()
        self.archive = self.storage/'before-provision.zap21'; self.archive.write_bytes(b'synthetic archive only')
        self.worker = self.root/'worker.exe'; self.worker.write_bytes(b'synthetic adapter')
        self.id = 'a'*32
        self.report_path = self.storage/('simulation-provision-'+'b'*32+'.json')
        self.checkpoints = self.report_path.with_suffix('.checkpoints.jsonl')
        self.scope = dict(operation='simulation_provision_scratch_credential', adapterSha256=guard.reference(self.worker)['sha256'],
            processId=7, resources=[dict(kind='project',path=str(self.project)),dict(kind='workspace',path=str(self.storage))])
        baseline = dict(project={'path':str(self.project)}, nativeScope=self.scope,
                        projectArchivePath=str(self.archive), projectArchive=archive_ref(self.archive))
        self.report = dict(reportPath=str(self.report_path), checkpointPath=str(self.checkpoints),
                           failure={'code':'SyntheticDiscoveryFailure'}, recoveryBaseline=baseline)
        self.rows = []
        for phase in ('attemptPrepared','beforeProjectArchive','afterProjectArchive','beforeStageProtectedCard','beforeEmptyPowerOn'):
            b = copy.deepcopy(baseline)
            if phase in ('attemptPrepared','beforeProjectArchive'): b['projectArchive']=None
            self.rows.append(dict(phase=phase,recoveryBaseline=b))
        self.directory = self.store/'engineering-admissions'/self.id
        self.directory.mkdir(parents=True)
        self.failure_path = self.store/'mutation-responses'/self.id/'failure.json'
        self.failure_path.parent.mkdir(parents=True)
        self.write_evidence()
        self.request_path = self.root/'request.json'
        self.request = dict(automaticReplay=False,
            resources=[dict(kind='workspace',path=str(self.store)),dict(kind='project',path=str(self.store/'scratch'))],
            binaries={'worker':dict(path=str(self.worker),files=[archive_ref(self.worker)])})
        self.put(self.request_path,self.request)
        self.ticket = guard.begin(self.registry,job_name='Local\\TiaMcp-Owned-synthetic',source=self.request_path)
        self.job = dict(schemaVersion='owned-job-terminal.v1',jobName='Local\\TiaMcp-Owned-synthetic',
            createdAtomicallyInJob=True,helperHandleSignaled=True,totalProcesses=3,activeProcesses=0,limitFlags=0,automaticTermination=False)
        self.job_path = self.root/'job.json';self.put(self.job_path,self.job)
        guard.complete(self.ticket,self.job_path)
        self.destination = self.output/'checkpoint.zap21'
        self.recovered_project = self.output/'project/P.ap21'
        self.resources = [dict(kind='project',path=str(self.recovered_project),effect='Retrieve into new project'),
            dict(kind='workspace',path=str(self.archive),access='readCheckpoint',effect='Read one original checkpoint'),
            dict(kind='workspace',path=str(self.destination),effect='Create one frozen checkpoint copy')]
        self.api = ReadLocks()

    @staticmethod
    def put(path,value): path.write_text(json.dumps(value),encoding='utf-8')

    def write_evidence(self):
        self.put(self.report_path,self.report)
        self.checkpoints.write_text('\n'.join(json.dumps(r) for r in self.rows)+'\n',encoding='utf-8')
        failure=dict(admissionId=self.id,nativeOutcome='unknown',automaticReplay=False,
                     nativeErrorCode=self.report['failure']['code'],nativeDetailsSha256=coordinator.canonical_sha({'details':self.report}))
        self.put(self.failure_path,failure)
        first=dict(id=self.id,revision=0,state='dispatchIntent',previousSha256=None,scope=self.scope,
                   resourceKeys=sorted(coordinator.resource_key(r['kind'],r['path']) for r in self.scope['resources']))
        last={**first,'revision':1,'state':'outcomeUnknown','previousSha256':coordinator.canonical_sha(first),'reconciliation':[],
              'response':dict(relativePath=f'mutation-responses/{self.id}/failure.json',sha256=guard.reference(self.failure_path)['sha256'])}
        self.put(self.directory/'00000000.json',first);self.put(self.directory/'00000001.json',last)

    def policy(self): return ProvisioningCheckpointRead(self.store,self.id,self.report_path,self.ticket/'intent.json')

    def window(self,policy=None,resources=None,mode='isolatedQualificationMutation'):
        return coordinator.StoreExclusiveWindow([self.store,self.output],self.recovered_project,self.output/'window',
            mode=mode,resources=resources or self.resources,effects='Synthetic checkpoint recovery',
            _api=self.api,_guard_root=self.registry,checkpoint_read=policy)

    def test_original_refusal_remains_default_and_no_journal_is_released(self):
        before=guard.reference(self.directory/'00000001.json')
        with self.assertRaisesRegex(ValueError,'Unresolved overlapping'):
            with self.window(): self.fail('Unreserved source read')
        self.assertEqual(before,guard.reference(self.directory/'00000001.json'))

    def test_bound_checkpoint_copy_retains_original_unknown_admission(self):
        policy=self.policy();before=guard.reference(self.directory/'00000001.json')
        with self.window(policy) as window:
            original=next(s for s in window.evidence['stores'] if Path(s['root'])==self.store)
            self.assertTrue(original['observations'][0]['pending'])
            self.assertTrue(original['observations'][0]['checkpointReadOnly'])
            copied=policy.copy_to(self.destination)
            self.assertEqual(self.archive.read_bytes(),self.destination.read_bytes())
            self.assertEqual(archive_ref(self.destination),copied)
            with self.assertRaises(ValueError): policy.copy_to(self.destination)
        self.assertEqual(before,guard.reference(self.directory/'00000001.json'))
        self.assertFalse(policy.close_receipt['admissionReleased'])
        self.assertTrue(policy.close_receipt['originalEvidenceUnchanged'])
        self.assertCountEqual(self.api.closed,range(1,len(self.api.opened)+1))

    def test_wrong_failure_report_or_archive_never_enters_body(self):
        for fault in ('report','failure','archive','missingReturn','lateReturn','worker','scope','duplicate'):
            with self.subTest(fault=fault):
                # Fresh setup preserves a separate refusal receipt per case.
                case=CheckpointReadTests();case.setUp()
                try:
                    if fault=='report': case.report['failure']['code']='altered';case.put(case.report_path,case.report)
                    if fault=='failure': case.failure_path.write_text('{}')
                    if fault=='archive': case.archive.write_bytes(b'changed')
                    if fault=='missingReturn': case.rows=[r for r in case.rows if r['phase']!='afterProjectArchive'];case.write_evidence()
                    if fault=='lateReturn': case.rows.append(case.rows.pop(2));case.write_evidence()
                    if fault=='worker': case.worker.write_bytes(b'changed')
                    if fault=='scope': case.report['recoveryBaseline']['nativeScope']=dict(operation='other');case.write_evidence()
                    if fault=='duplicate': case.checkpoints.write_text('{"phase":"x","phase":"x"}\n')
                    with self.assertRaises((ValueError,KeyError)):
                        with case.window(case.policy()): self.fail('Invalid checkpoint admitted')
                    self.assertFalse(case.destination.exists())
                    self.assertCountEqual(case.api.closed,range(1,len(case.api.opened)+1))
                finally: case.doCleanups()

    def test_live_original_job_is_still_quarantined(self):
        self.job['activeProcesses']=1;self.put(self.job_path,self.job)
        with self.assertRaises(ValueError):
            with self.window(self.policy()): self.fail('Live source admitted')

    def test_read_policy_does_not_release_another_overlapping_pending_operation(self):
        directory=self.store/'engineering-operations'/('c'*32);directory.mkdir(parents=True)
        plan={'resources':[dict(kind='workspace',resourceKey=coordinator.resource_key('workspace',self.store))]}
        first=dict(id=directory.name,revision=0,state='prepared',plan=plan,planSha256=coordinator.canonical_sha(plan),previousSha256=None)
        second={**first,'revision':1,'state':'acquired','previousSha256':coordinator.canonical_sha(first)}
        self.put(directory/'000000.json',first);self.put(directory/'000001.json',second)
        with self.assertRaisesRegex(ValueError,'Unresolved overlapping'):
            with self.window(self.policy()): self.fail('Other pending operation bypassed')

    def test_generic_boolean_or_foreign_policy_cannot_override_scan(self):
        with self.assertRaisesRegex(ValueError,'Exact checkpoint'):
            coordinator.scan_store(self.store,[],True)
        with self.assertRaisesRegex(ValueError,'exact read policy'):
            self.window(True)

    def test_changed_journal_chain_is_not_excused_by_checkpoint_policy(self):
        first=read_json(self.directory/'00000000.json');first['scope']={**first['scope'],'extra':'changed'}
        self.put(self.directory/'00000000.json',first)
        with self.assertRaisesRegex(ValueError,'Journal hash chain'):
            with self.window(self.policy()): self.fail('Invalid chain admitted')

    def test_archive_parent_project_or_missing_read_intent_remains_blocked(self):
        for fault in ('parent','project','missingIntent','readOnlyMode'):
            case=CheckpointReadTests();case.setUp()
            try:
                resources=copy.deepcopy(case.resources)
                if fault=='parent': resources.append(dict(kind='workspace',path=str(case.storage),effect='write source'))
                if fault=='project': resources.append(dict(kind='project',path=str(case.project),effect='open original'))
                if fault=='missingIntent': resources[1].pop('access')
                with self.subTest(fault=fault),self.assertRaises(ValueError):
                    with case.window(case.policy(),resources,mode='readOnlyProbe' if fault=='readOnlyMode' else 'isolatedQualificationMutation'):
                        self.fail('Broader source operation admitted')
            finally: case.doCleanups()

    def test_partial_copy_or_changed_original_evidence_fails_and_closes_handles(self):
        policy=self.policy()
        with self.assertRaisesRegex(ValueError,'evidence changed'):
            with self.window(policy):
                policy.copy_to(self.destination)
                self.failure_path.write_text('{}')
        self.assertTrue(policy.close_receipt['allReadHandlesClosed'])
        self.assertFalse(policy.close_receipt['originalEvidenceUnchanged'])
        self.assertTrue(self.destination.exists())

    def test_copy_cannot_write_outside_declared_destination_or_before_admission(self):
        policy=self.policy()
        with self.assertRaises(ValueError): policy.copy_to(self.destination)
        with self.window(policy):
            with self.assertRaises(ValueError): policy.copy_to(self.output/'undeclared.zap21')
            self.destination.write_bytes(b'original collision')
            with self.assertRaises(ValueError): policy.copy_to(self.destination)
            self.assertEqual(b'original collision',self.destination.read_bytes())

    @unittest.skipUnless(os.name=='nt','Windows file sharing')
    def test_real_windows_leases_allow_reads_but_block_write_delete_and_rename(self):
        self.api=coordinator.WindowsFiles();policy=self.policy()
        with self.window(policy):
            policy.copy_to(self.destination)
            for path in (self.archive,self.destination):
                self.assertEqual(b'synthetic archive only',path.read_bytes())
                with self.assertRaises(OSError): path.write_bytes(b'changed')
                with self.assertRaises(OSError): path.unlink()
                with self.assertRaises(OSError): path.rename(path.with_suffix('.moved'))
        self.destination.write_bytes(b'released copy')
        self.assertTrue(policy.close_receipt['allReadHandlesClosed'])


if __name__=='__main__': unittest.main()
