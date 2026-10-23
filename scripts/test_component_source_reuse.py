"""Offline native04 exact-source reuse guard and qualifier-routing tests."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import component_source_reuse as reuse
import qualify_plc_component as qualifier

class ReuseTests(unittest.TestCase):
    def setUp(self):
        # The regression suite must also work in a clean checkout with no native
        # qualification run. Only these test fixtures substitute archive bytes;
        # the production native04 archive pin remains unchanged.
        temporary=tempfile.TemporaryDirectory(prefix='component-reuse-')
        self.addCleanup(temporary.cleanup)
        self.root=Path(temporary.name)
        self.artifacts=self.root/'artifacts'; self.artifacts.mkdir()
        self.fixture=json.loads((qualifier.FIXTURES/'manifest.json').read_text())
        def write(relative, value):
            path=self.artifacts/relative; path.parent.mkdir(parents=True,exist_ok=True)
            path.write_bytes(value if isinstance(value,bytes) else json.dumps(value).encode())
            return reuse.file_record(path)
        archive=write('source/published.zal21',b'offline synthetic library archive')
        archive_pin=mock.patch.object(reuse,'ARCHIVE_SHA256',archive['sha256'])
        archive_pin.start(); self.addCleanup(archive_pin.stop)
        versions=[]
        for name,selected in reuse.PINS.items():
            version=dict(typeName=name,**selected,state='Committed',version='1.0.0',doNotUse=False,
                requiredVersionGuids=[reuse.PINS[self.fixture['dependency']]['versionGuid']] if name==self.fixture['root'] else [])
            if name==self.fixture['root']:
                def observed(mode):
                    data=(qualifier.FIXTURES/f'oracle-xp01/source-43b5c338-b589-4ab5-8c82-cae3e08e9d06-options{mode}.xml').read_bytes()
                    return data.replace(b'43b5c338-b589-4ab5-8c82-cae3e08e9d06',selected['versionGuid'].encode()).replace(
                        b'146cafdb-e1d5-4111-9b23-1078b6557d53',selected['typeGuid'].encode())
                base=write('source/base.xml',observed(1)); oracle=write('source/oracle.xml',observed(3))
                version['contentPolicy']={'policyId':reuse.POLICY,'sourceLibraryName':self.fixture['sourceLibraryName'],
                    'sourceOracleSha256':oracle['sha256'],'sourceOracleArtifact':oracle,
                    'properties':self.fixture['explicitBlockAttributes'],'sourceBaseExportOptions':'WithDefaults',
                    'sourceOracleExportOptions':'WithDefaults, WithReadOnly','documentInfoOptions':'None'}
            else:
                base=write('source/udt.xml',b'<Document><OfflineFixtureUDT/></Document>')
            version.update(artifact=base,contentSha256=base['sha256']); versions.append(version)
        root=next(v for v in versions if v['typeName']==self.fixture['root'])
        plan={'versions':versions}
        import_journal={'state':'applied','fixture':'offline import receipt'}
        plan_journal={'state':'prepared','fixture':'offline component plan'}
        plan_path=write('native/changes/change-lib-plan/journal.json',plan_journal)['path']
        import_path=write('native/changes/change-lib-import/journal.json',import_journal)['path']
        checkpoint=write('native/changes/change-lib-import/before.zap21',b'offline checkpoint')
        observations=[]
        for version in versions:
            observed={key:version[key] for key in ('typeGuid','versionGuid')}
            observed.update(sourceContentSha256=version['contentSha256'],sourceArtifact=version['artifact'])
            if version['typeName']==self.fixture['root']:
                observed.update(sourceOracleSha256=oracle['sha256'],sourceOracleArtifact=oracle,
                    contentVerification={'baseRemainingTreeMatches':True,'oracleRemainingTreeMatches':True,'nativeSettingsMatch':True})
            observations.append(observed)
        record={'state':'recovery_required','graphical':{},'automaticReplayAllowed':False,
            'operationId':'component-'+'a'*32,'failure':{'code':'ComponentStageFailed','message':reuse.FAILURE},
            'events':[{'stage':stage} for stage in ('created','library_prepared','library_dispatching','library_applied',
                'import_readback_captured_unverified','import_readback_verified','pre_typed_readback_captured_unverified','graphical_prepare_failed')],
            'contract':{'plan':plan},'library':{'prepared':{'changeId':'change-lib-import','journalPath':import_path},
                'result':{'state':'applied','journal':import_journal}},'preTypedEvidence':{'versions':observations},
            'initialCheckpoint':{'changeId':'change-lib-import','archivePath':checkpoint['path'],'sha256':checkpoint['sha256']}}
        project=str(self.artifacts/'original-consumer.ap21')
        write(f"native/changes/{record['operationId']}/component-journal.json",{'record':record})
        write('native/changes/change-typed/journal.json',{'kind':'graphical_typed_edit','state':'prepare_failed',
            'failure':reuse.FAILURE,'projectPath':project,'stages':[],'idempotencyKey':None,'recoveryArchive':None})
        fixture_inputs=[{'name':path.relative_to(qualifier.FIXTURES).as_posix(),'sha256':hashlib.sha256(path.read_bytes()).hexdigest()}
            for path in sorted(qualifier.FIXTURES.rglob('*')) if path.is_file()]
        self.previous={'status':'failed','failure':'change_prepare: InstanceCreationProfileUnavailable',
            'failureStatus':{'record':record},'fixture':self.fixture,'fixtureInputs':fixture_inputs,'sourceArchive':archive,
            'sourceContentPolicyVerified':True,'sourceContentEvidenceAttempt':{'verified':True,'pin':root},
            'productionApplySupported':False,'runtimeVerified':False,'callerInitialState':{'exists':False,'calledFromOb':False},
            'authoredDependencyClosure':copy.deepcopy(versions),'nativeArtifactRoot':str(self.artifacts/'native'),
            'consumerScratch':{'project':{'path':project}},'plan':{'preview':{'integrationPlan':plan},'journalPath':plan_path,'journal':plan_journal}}
        self.original=Path(write('original-result.json',self.previous)['path'])
        self.original_sha=hashlib.sha256(self.original.read_bytes()).hexdigest()

    def prepare(self, directory, changed=None, fixture=None, report_hash=None):
        directory=Path(directory)
        report=self.original
        if changed is not None:
            report=directory/'report.json'; report.write_text(json.dumps(changed),encoding='utf-8')
        native=directory/'native'; native.mkdir()
        return reuse.prepare_source_reuse(report,report_hash or hashlib.sha256(report.read_bytes()).hexdigest(),
            qualifier.FIXTURES,fixture or self.fixture,native,self.artifacts,
            qualifier.verify_dependency_graph,qualifier.verify_pinned_source_oracle)

    def test_exact_native04_archive_is_copied_for_fresh_consumer(self):
        with tempfile.TemporaryDirectory(prefix='reuse-test-',dir=self.artifacts) as directory:
            archive,pins,proof=self.prepare(directory)
            self.assertEqual(pins,reuse.PINS)
            self.assertEqual(archive['sha256'],reuse.ARCHIVE_SHA256)
            self.assertNotEqual(archive['path'],self.previous['sourceArchive']['path'])
            self.assertTrue(Path(archive['path']).is_relative_to(Path(directory)/'native'))
            self.assertFalse(proof['authoringExecuted']); self.assertFalse(proof['oldConsumerOpened']); self.assertFalse(proof['oldOperationReplayed'])
            self.assertEqual(self.original_sha,hashlib.sha256(self.original.read_bytes()).hexdigest())
            reuse.validate_original_inputs(proof['originalInputFiles'])
            self.assertEqual(proof['fixtureInputs'],self.previous['fixtureInputs'])

    def test_wrong_report_hash_fails_before_copy(self):
        with tempfile.TemporaryDirectory(prefix='reuse-test-',dir=self.artifacts) as directory:
            with self.assertRaisesRegex(RuntimeError,'report hash differs'): self.prepare(directory,report_hash='0'*64)
            self.assertEqual(list((Path(directory)/'native').iterdir()),[])

    def test_report_hash_read_gap_is_rejected_before_parsing(self):
        with tempfile.TemporaryDirectory(prefix='reuse-test-',dir=self.artifacts) as directory:
            report=Path(directory)/'report.json'; report.write_bytes(self.original.read_bytes())
            native=Path(directory)/'native'; native.mkdir()
            original=reuse.file_record
            def record(path,root=None):
                result=original(path,root)
                if Path(path)==report: report.write_text('{"changed":true}')
                return result
            with mock.patch.object(reuse,'file_record',side_effect=record):
                with self.assertRaisesRegex(RuntimeError,'bytes changed before parsing'):
                    reuse.prepare_source_reuse(report,self.original_sha,qualifier.FIXTURES,self.fixture,native,self.artifacts,
                        qualifier.verify_dependency_graph,qualifier.verify_pinned_source_oracle)
            self.assertEqual(list(native.iterdir()),[])

    def test_missing_hash_rejected(self):
        with self.assertRaisesRegex(RuntimeError,'SHA256'):
            reuse.prepare_source_reuse(self.original,None,qualifier.FIXTURES,self.fixture,self.artifacts,self.artifacts,None,None)

    def test_wrong_failure_state_cannot_resume(self):
        for field,value in [('status','passed'),('failure','different failure')]:
            with self.subTest(field=field), tempfile.TemporaryDirectory(prefix='reuse-test-',dir=self.artifacts) as directory:
                previous=copy.deepcopy(self.previous); previous[field]=value
                with self.assertRaisesRegex(RuntimeError,'eligibility'): self.prepare(directory,previous)

    def test_typed_dispatch_evidence_cannot_be_reused(self):
        with tempfile.TemporaryDirectory(prefix='reuse-test-',dir=self.artifacts) as directory:
            previous=copy.deepcopy(self.previous); previous['failureStatus']['record']['graphical']={'dispatch':True}
            with self.assertRaisesRegex(RuntimeError,'eligibility'): self.prepare(directory,previous)

    def test_stage_sequence_change_rejected(self):
        with tempfile.TemporaryDirectory(prefix='reuse-test-',dir=self.artifacts) as directory:
            previous=copy.deepcopy(self.previous); previous['failureStatus']['record']['events'].append({'stage':'installation_verified'})
            with self.assertRaisesRegex(RuntimeError,'stage sequence'): self.prepare(directory,previous)

    def test_current_fixture_contract_change_rejected(self):
        with tempfile.TemporaryDirectory(prefix='reuse-test-',dir=self.artifacts) as directory:
            fixture=copy.deepcopy(self.fixture); fixture['explicitBlockAttributes']['AutoNumber']='false'
            with self.assertRaisesRegex(RuntimeError,'fixture/content contract'): self.prepare(directory,fixture=fixture)

    def test_new_guid_same_name_rejected(self):
        with tempfile.TemporaryDirectory(prefix='reuse-test-',dir=self.artifacts) as directory:
            previous=copy.deepcopy(self.previous)
            plan=previous['plan']['preview']['integrationPlan']
            root=next(v for v in plan['versions'] if v['typeName']==self.fixture['root'])
            root['typeGuid']='11111111-1111-1111-1111-111111111111'
            previous['failureStatus']['record']['contract']['plan']=copy.deepcopy(plan)
            with self.assertRaisesRegex(RuntimeError,'GUID/version tuple'): self.prepare(directory,previous)

    def test_archive_pin_change_rejected(self):
        with tempfile.TemporaryDirectory(prefix='reuse-test-',dir=self.artifacts) as directory:
            previous=copy.deepcopy(self.previous); previous['sourceArchive']['sha256']='0'*64
            with self.assertRaisesRegex(RuntimeError,'artifact changed'): self.prepare(directory,previous)

    def test_oracle_hash_change_rejected(self):
        with tempfile.TemporaryDirectory(prefix='reuse-test-',dir=self.artifacts) as directory:
            previous=copy.deepcopy(self.previous)
            root=next(v for v in previous['plan']['preview']['integrationPlan']['versions'] if v['typeName']==self.fixture['root'])
            root['contentPolicy']['sourceOracleSha256']='0'*64
            previous['failureStatus']['record']['contract']['plan']=copy.deepcopy(previous['plan']['preview']['integrationPlan'])
            previous['sourceContentEvidenceAttempt']['pin']=copy.deepcopy(root)
            with self.assertRaisesRegex(RuntimeError,'oracle hash differs'): self.prepare(directory,previous)

    def test_original_source_drift_after_copy_detected(self):
        with tempfile.TemporaryDirectory(prefix='reuse-test-',dir=self.artifacts) as directory:
            marker=Path(directory)/'bound.txt'; marker.write_text('original'); row=reuse.file_record(marker)
            marker.write_text('altered')
            with self.assertRaisesRegex(RuntimeError,'input changed'): reuse.validate_original_inputs([row])

    def test_existing_staging_never_overwritten(self):
        with tempfile.TemporaryDirectory(prefix='reuse-test-',dir=self.artifacts) as directory:
            native=Path(directory)/'native'; native.mkdir(); (native/'documents').mkdir(); marker=native/'documents/keep'; marker.write_text('keep')
            with self.assertRaisesRegex(RuntimeError,'replace existing'):
                reuse.prepare_source_reuse(self.original,self.original_sha,qualifier.FIXTURES,self.fixture,native,self.artifacts,
                    qualifier.verify_dependency_graph,qualifier.verify_pinned_source_oracle)
            self.assertEqual(marker.read_text(),'keep')

    def new_run(self,directory):
        run=qualifier.ComponentRun.__new__(qualifier.ComponentRun)
        run.output=Path(directory); run.native=run.output/'native'; run.native.mkdir()
        run.store_root=self.artifacts
        run.lifetime=mock.Mock(owner=None)
        run.args=argparse.Namespace(reuse_source_report=self.original,reuse_source_report_sha256=self.original_sha,
            recover_from_report=None,artifact_policy_root=self.artifacts,host='host',worker='worker',timeout=60)
        run.manifest=self.fixture; run.report={'status':'running','events':[]}; run.session=None; run.component_id=None
        run.author=mock.Mock(side_effect=AssertionError('author must never run'))
        run.consumer=mock.Mock()
        return run

    def test_reuse_runs_consumer_once_never_author(self):
        with tempfile.TemporaryDirectory() as directory:
            run=self.new_run(directory); archive={'path':str(run.native/'local.zal21'),'sha256':reuse.ARCHIVE_SHA256}
            proof={'originalInputFiles':[{'path':'original'}],'fixtureInputs':[],'oldConsumerOpened':False}
            with (mock.patch.object(qualifier,'prepare_source_reuse',return_value=(archive,reuse.PINS,proof)) as preflight,
                 mock.patch.object(qualifier,'OwnedScratch') as owned,
                 mock.patch.object(qualifier,'validate_original_inputs') as verify):
                run.run()
            preflight.assert_called_once(); owned.assert_called_once(); verify.assert_called_once_with(proof['originalInputFiles'])
            run.author.assert_not_called(); run.consumer.assert_called_once_with(archive,reuse.PINS)
            self.assertTrue(run.report['originalSourceInputsPreserved'])

    def test_preflight_failure_starts_no_native_and_is_terminal(self):
        with tempfile.TemporaryDirectory() as directory:
            run=self.new_run(directory)
            with (mock.patch.object(qualifier,'prepare_source_reuse',side_effect=RuntimeError('rejected source')),
                 mock.patch.object(qualifier,'OwnedScratch') as owned):
                with self.assertRaisesRegex(RuntimeError,'rejected source'): run.run()
            owned.assert_not_called(); run.author.assert_not_called(); run.consumer.assert_not_called()
            saved=json.loads((run.output/'result.json').read_text())
            self.assertEqual(saved['status'],'failed'); self.assertIn('completedAtUtc',saved)

    def test_consumer_failure_does_not_fallback_to_author_or_resume(self):
        with tempfile.TemporaryDirectory() as directory:
            run=self.new_run(directory); run.consumer.side_effect=TimeoutError('uncertain consumer dispatch')
            proof={'originalInputFiles':[{'path':'original'}],'fixtureInputs':[]}
            with (mock.patch.object(qualifier,'prepare_source_reuse',return_value=({},reuse.PINS,proof)),
                 mock.patch.object(qualifier,'OwnedScratch'), mock.patch.object(qualifier,'validate_original_inputs') as verify):
                with self.assertRaises(TimeoutError): run.run()
            run.author.assert_not_called(); self.assertEqual(run.consumer.call_count,1); verify.assert_called_once()
            self.assertEqual(run.report['status'],'failed')

    def test_cleanup_failure_does_not_skip_original_input_audit(self):
        with tempfile.TemporaryDirectory() as directory:
            run=self.new_run(directory); proof={'originalInputFiles':[{'path':'original'}],'fixtureInputs':[]}
            with (mock.patch.object(qualifier,'prepare_source_reuse',return_value=({},reuse.PINS,proof)),
                 mock.patch.object(qualifier,'OwnedScratch') as owned, mock.patch.object(qualifier,'validate_original_inputs') as verify):
                owned.return_value.close.side_effect=RuntimeError('cleanup failed')
                with self.assertRaisesRegex(RuntimeError,'cleanup failed'): run.run()
            verify.assert_called_once(); self.assertTrue(run.report['originalSourceInputsPreserved'])
            self.assertEqual(run.report['cleanupFailure'],'cleanup failed'); self.assertEqual(run.report['status'],'failed')

    def test_primary_failure_survives_cleanup_and_original_audit_failures(self):
        with tempfile.TemporaryDirectory() as directory:
            run=self.new_run(directory); run.consumer.side_effect=TimeoutError('primary dispatch uncertainty')
            proof={'originalInputFiles':[{'path':'original'}],'fixtureInputs':[]}
            with (mock.patch.object(qualifier,'prepare_source_reuse',return_value=({},reuse.PINS,proof)),
                 mock.patch.object(qualifier,'OwnedScratch') as owned,
                 mock.patch.object(qualifier,'validate_original_inputs',side_effect=RuntimeError('input changed')) as verify):
                owned.return_value.close.side_effect=RuntimeError('cleanup failed')
                with self.assertRaisesRegex(TimeoutError,'primary dispatch uncertainty'): run.run()
            verify.assert_called_once(); run.author.assert_not_called(); self.assertEqual(run.consumer.call_count,1)
            self.assertEqual(run.report['failure'],'primary dispatch uncertainty')
            self.assertEqual(run.report['cleanupFailure'],'cleanup failed')
            self.assertEqual(run.report['originalSourceInputFailure'],'input changed')
            self.assertFalse(run.report['originalSourceInputsPreserved'])


class ExactStorageReopenTests(unittest.TestCase):
    XML=b'<?xml version="1.0" encoding="utf-8"?>\r\n<Document>\r\n  <DocumentInfo><Created>old</Created></DocumentInfo>\r\n  <SW.Blocks.InstanceDB ID="0"><Unknown xmlns:p="urn:first" Kind="p:Type" xml:space="preserve"> left <Value/> right </Unknown></SW.Blocks.InstanceDB>\r\n</Document>'

    def compare(self,left,right):
        with tempfile.TemporaryDirectory() as directory:
            snapshots=[]
            for index,raw in enumerate((left,right)):
                path=Path(directory)/f'{index}.xml'; path.write_bytes(raw)
                snapshots.append({'objects':{'block:/DB':{'file':str(path),'sha256':hashlib.sha256(raw).hexdigest(),'bytes':len(raw)}}})
            return reuse.verify_exact_storage_reopen(*snapshots,'block:/DB')

    def test_only_direct_native_provenance_span_can_differ(self):
        proof=self.compare(self.XML,self.XML.replace(b'>old<',b'>new<'))
        self.assertTrue(proof['verified'])
        self.assertNotEqual(proof['exports'][0]['sha256'],proof['exports'][1]['sha256'])
        self.assertEqual(proof['exports'][0]['exactPayloadSha256'],proof['exports'][1]['exactPayloadSha256'])

    def test_remaining_native_bytes_are_exact(self):
        for before,after in ((b' left ',b'  left '),(b' right ',b'right '),(b'urn:first',b'urn:second'),
                (b'ID="0"',b'ID="1"'),(b'encoding="utf-8"',b'encoding="UTF-8"'),
                (b'<Value/>',b'<Value/><Unknown/>'),(b'<Value/>',b'<Value/><!-- added -->'),(b'\r\n  <SW.',b'\n  <SW.')):
            with self.subTest(before=before),self.assertRaisesRegex(RuntimeError,'changed on fresh reopen'):
                self.compare(self.XML,self.XML.replace(before,after))

    def test_nested_or_foreign_provenance_is_never_removed(self):
        for extra in (b'<DocumentInfo>one</DocumentInfo>',b'<p:DocumentInfo xmlns:p="urn:foreign">one</p:DocumentInfo>'):
            raw=self.XML.replace(b'<Value/>',extra)
            with self.subTest(extra=extra),self.assertRaisesRegex(RuntimeError,'changed on fresh reopen'):
                self.compare(raw,raw.replace(b'>one<',b'>two<'))

    def test_foreign_missing_duplicate_and_noncanonical_provenance_rejected(self):
        for raw in (self.XML.replace(b'<DocumentInfo>',b'<DocumentInfo xmlns="urn:foreign">'),
                self.XML.replace(b'<DocumentInfo>',b'<DocumentInfo/><DocumentInfo>'),
                self.XML.replace(b'<DocumentInfo>',b'<DocumentInfo extra="true">'),
                self.XML.replace(b'<DocumentInfo>',b'<DocumentInfo >'),
                self.XML.replace(b'<DocumentInfo><Created>old</Created></DocumentInfo>',b'')):
            with self.subTest(raw=raw),self.assertRaises(RuntimeError): reuse.exact_native_payload(raw)

    def test_doctype_entities_and_outside_root_metadata_rejected(self):
        for raw in (self.XML.replace(b'<Document>',b'<!DOCTYPE Document><Document>'),
                self.XML.replace(b'<Document>',b'<!DOCTYPE Document [<!ENTITY x "y">]><Document>'),
                self.XML+b'<!-- epilog -->',self.XML.replace(b'<Document>',b'<?unknown value?><Document>')):
            with self.subTest(raw=raw),self.assertRaises(RuntimeError): reuse.exact_native_payload(raw)

    def test_retained_snapshot_tamper_rejected_before_comparison(self):
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'db.xml'; path.write_bytes(self.XML)
            snapshot={'objects':{'block:/DB':{'file':str(path),'sha256':'0'*64,'bytes':len(self.XML)}}}
            with self.assertRaisesRegex(RuntimeError,'snapshot bytes changed'):
                reuse.verify_exact_storage_reopen(snapshot,snapshot,'block:/DB')


if __name__=='__main__': unittest.main()
