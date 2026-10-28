"""Portable failure-controller and verifier refusal checks; never starts TIA."""
import json
from pathlib import Path
import tempfile
import unittest

import qualify_translation_import as controller
import verify_translation_failure as verifier
import verify_translation_admission as admission_verifier


class FailurePacketChecks(unittest.TestCase):
    def test_same_instance_key_drift_stops_before_apply(self):
        for field in ('nativeKey', 'projectPath', 'source', 'target', 'peer', 'binary'):
            with self.subTest(field=field), tempfile.TemporaryDirectory() as directory:
                run = controller.Run.__new__(controller.Run)
                run.root = run.artifacts = Path(directory); run.failure_case = 'before-dispatch'; run.same_instance = True
                run.forward_key = {field: 'original'}; calls = []
                def call(name, args=None, expect_error=False):
                    calls.append(name)
                    self.assertTrue(name.endswith('_prepare'))
                    changed = {field: 'changed'}
                    return {'admissionKey': changed, 'admissionKeySha256': controller.canonical_sha(changed)}
                run.call = call
                with self.assertRaisesRegex(ValueError, 'instance drift'):
                    run.failure_trial({}, {}, str(run.root/'original'/'Fixture.ap21'), {'trials': []})
                self.assertEqual(calls, ['qualification_translation_prepare'])

    def test_same_instance_only_reverses_independently_verified_applied_cases(self):
        with tempfile.TemporaryDirectory() as directory:
            run = controller.Run.__new__(controller.Run); run.root = run.artifacts = Path(directory)
            run.same_instance = True; run.forward_key = {'exact': 'instance'}; calls = []
            op = {'id': 'a'*32, 'planSha256': 'b'*64, 'revision': 0}
            def trial(selector, edit, project, report):
                report['trials'].append({'name': run.failure_case, 'originalDisposition': 'verifiedApplied' if run.failure_case in ('save-reply', 'reopen-reply', 'restore-reply') else 'unchanged'})
            def call(name, args=None, expect_error=False):
                calls.append((name, args))
                if name == 'project_open': return {}
                if name.endswith('_prepare'): return {'operation': op, 'admissionKey': run.forward_key, 'admissionKeySha256': controller.canonical_sha(run.forward_key)}
                if name.endswith('_apply'): return {**op, 'state': 'succeeded', 'revision': 10}
                if name.endswith('_recover'): return {**op, 'state': 'recoveryIntent', 'revision': 11}
                if name.endswith('_reconcile'): return {**op, 'state': 'reconciled', 'revision': 12, 'reconciliation': [{'disposition': 'verifiedApplied'}], 'recoveryResolutions': [{'disposition': 'verifiedRestored'}]}
                raise AssertionError(name)
            run.failure_trial = trial; run.call = call; report = {'trials': []}
            run.same_instance_trials({}, {'nativeKey': 'K', 'replacement': 'after'}, str(run.root/'Original.ap21'), 'before', report)
            self.assertEqual([t['case'] for t in report['reverseTrials']], ['save-reply', 'reopen-reply', 'restore-reply'])
            self.assertEqual(len([c for c in calls if c[0].endswith('_apply')]), 3)
            for name, args in calls:
                if name.endswith('_prepare') and args['idempotencyKey'].startswith('reverse-'): self.assertEqual(args['replacement'], 'before')

    def test_actual_peer_equality_normalizes_time_but_keeps_source_and_code(self):
        baseline = b'<Document><DocumentInfo><Created>first</Created></DocumentInfo><SW.Blocks.OB><AttributeList><Name>Main</Name><Number>1</Number><Code>KEEP</Code></AttributeList><ObjectList><MultilingualText CompositionName="Comment"><ObjectList><MultilingualTextItem><AttributeList><Culture>en-GB</Culture><Text>before</Text></AttributeList></MultilingualTextItem><MultilingualTextItem><AttributeList><Culture>de-DE</Culture><Text>SOURCE</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.OB></Document>'
        normalized = lambda data: admission_verifier.peer(data, 'tia21-simaticml-translation-v1', 'before', 'after')
        self.assertEqual(normalized(baseline), normalized(baseline.replace(b'first', b'second').replace(b'>before<', b'>after<')))
        for old, new in ((b'SOURCE', b'DRIFT'), (b'KEEP', b'DRIFT'), (b'>1<', b'>2<')):
            with self.subTest(field=old):
                try: self.assertNotEqual(normalized(baseline), normalized(baseline.replace(old, new)))
                except ValueError: pass

    def test_controller_uses_only_one_attempt_and_one_new_restore(self):
        for case in controller.FAILURE_CASES:
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory:
                run = controller.Run.__new__(controller.Run)
                run.root = run.artifacts = Path(directory)
                run.failure_case = case
                calls = []
                op = {'id': 'a' * 32, 'planSha256': 'b' * 64, 'revision': 0, 'state': 'prepared'}
                unknown = {**op, 'revision': 4, 'state': 'outcomeUnknown', 'checkpoints': [{}]}
                succeeded = {**unknown, 'state': 'succeeded'}
                restored = False
                def call(name, args=None, expect_error=False):
                    nonlocal restored
                    calls.append((name, args, expect_error))
                    if name.endswith('_prepare'): return {'operation': op}
                    if name.endswith('_status'): return unknown if restored or case != 'restore-reply' else succeeded
                    if name.endswith('_recover') and args.get('loseRestoreReply'): restored = True
                    if expect_error: return {'isError': True, 'content': [{'text': 'StaleWorkbook' if case == 'source-drift' else 'refused'}]}
                    if name.endswith('_apply'): return succeeded
                    if name.endswith('_recover'): return {**op, 'revision': 6, 'state': 'recoveryIntent'}
                    if name.endswith('_reconcile'): return {**op, 'revision': 7, 'state': 'reconciled',
                        'recoveryResolutions': [{'disposition': 'verifiedRestored'}], 'reconciliation': [{'disposition': 'unchanged'}]}
                    raise AssertionError(name)
                run.call = call
                report = {'trials': []}
                run.failure_trial({'projectPath': str(run.root/'original'/'Fixture.ap21'), 'plcPath': 'PLC'},
                                  {'nativeKey': 'K', 'replacement': 'after'}, str(run.root/'original'/'Fixture.ap21'), report)
                apply = [c for c in calls if c[0].endswith('_apply')]
                self.assertEqual(len(apply), 2)
                self.assertEqual([c[2] for c in apply], [case != 'restore-reply', True])
                self.assertEqual(apply[1][1], {'operationId': op['id'], 'planSha256': op['planSha256'], 'revision': 4})
                if case == 'before-dispatch': self.assertTrue(apply[0][1]['interruptBeforeDispatch'])
                elif case == 'source-drift': self.assertTrue(apply[0][1]['driftSourceAfterCheckpoint'])
                elif case != 'restore-reply': self.assertEqual(apply[0][1]['loseReplyAfterStage'], case.removesuffix('-reply'))
                recover = [c for c in calls if c[0].endswith('_recover')]
                self.assertEqual([c[2] for c in recover], [True, case == 'restore-reply'])
                self.assertNotEqual(recover[0][1]['newProjectPath'], recover[1][1]['newProjectPath'])
                self.assertEqual(report['trials'][0]['expectedImports'], 0 if case in ('before-dispatch', 'source-drift') else 1)

    def test_controller_stops_without_replay_when_unknown_outcome_is_missing(self):
        with tempfile.TemporaryDirectory() as directory:
            run = controller.Run.__new__(controller.Run)
            run.root = run.artifacts = Path(directory); run.failure_case = 'import-reply'
            calls = []
            def call(name, args=None, expect_error=False):
                calls.append(name)
                if name.endswith('_prepare'): return {'operation': {'id': 'a'*32, 'planSha256': 'b'*64, 'revision': 0}}
                if name.endswith('_apply'): return {'isError': True}
                if name.endswith('_status'): return {'state': 'succeeded', 'checkpoints': [{}]}
                raise AssertionError('Unsafe continuation: ' + name)
            run.call = call
            with self.assertRaisesRegex(ValueError, 'unknown outcome'):
                run.failure_trial({}, {}, str(run.root/'original'/'Fixture.ap21'), {'trials': []})
            self.assertEqual(len([name for name in calls if name.endswith('_apply')]), 1)
            self.assertFalse(any(name.endswith('_recover') for name in calls))

    def test_verifier_refuses_incomplete_or_historical_packet_before_reading_native_evidence(self):
        for damage in ('not-completed', 'production-enabled', 'replay', 'old-root', 'resume', 'wrong-case', 'two-trials'):
            with self.subTest(damage=damage), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                admission = {'failureCase': 'import-reply', 'kind': 'translation-failure-qualification', 'artifactRoot': str(root), 'resumeOperation': None}
                result = {'completed': True, 'productionEnabled': False, 'automaticReplay': False, 'preexistingPreserved': True, 'ownedPortalExited': True,
                          'trials': [{'name': 'import-reply'}]}
                if damage == 'not-completed': result['completed'] = False
                elif damage == 'production-enabled': result['productionEnabled'] = True
                elif damage == 'replay': result['automaticReplay'] = True
                elif damage == 'old-root': admission['artifactRoot'] = str(root/'old')
                elif damage == 'resume': admission['resumeOperation'] = 'historical'
                elif damage == 'wrong-case': admission['failureCase'] = 'unbounded'
                else: result['trials'].append({'name': 'import-reply'})
                (root/'admission.json').write_text(json.dumps(admission), encoding='utf-8')
                (root/'result.json').write_text(json.dumps(result), encoding='utf-8')
                with self.assertRaises(ValueError): verifier.verify(root)
                with self.assertRaises(ValueError): admission_verifier.verify(root)


if __name__ == '__main__': unittest.main()
