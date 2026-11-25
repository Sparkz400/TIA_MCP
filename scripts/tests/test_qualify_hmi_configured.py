"""Independent captured-state/compile refusal controls; no TIA process starts."""
import copy
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace

import native_dispatch_guard as guard
import qualify_hmi_configured as verifier


class CaptureTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(); self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.xml = self.root / 'original.xml'
        self.xml.write_text('<?xml version="1.0"?><Document><!--keep--><DocumentInfo><Created>2026-10-21T01:00:00Z</Created></DocumentInfo><Unknown id="17"><Value>original</Value></Unknown></Document>', encoding='utf-8')
        self.owner = dict(name='ExactOwner', type='Siemens.Engineering.HmiUnified.HmiSoftware', remainingBudget=1990,
            attributes=dict(Name='ExactOwner', unknown=dict(nativeType='Opaque', notScalar=True)),
            sections=dict(Screens=[dict(nativeType='Screen', attributes=dict(Name='Screen1', Width=800), export=guard.reference(self.xml))]))
        self.observed = copy.deepcopy([self.owner])
        self.other = self.root / 'different-stage.xml'; self.other.write_bytes(self.xml.read_bytes())
        self.observed[0]['sections']['Screens'][0]['export'] = guard.reference(self.other)

    def compare(self): return verifier.compare_capture([self.owner], self.observed)

    def test_exact_capture_and_export_bytes_compare_across_different_output_paths(self):
        receipt = self.compare()
        self.assertTrue(receipt['equal']); self.assertEqual(receipt['ownerCount'], 1)
        self.assertEqual(len(receipt['originalFiles']), 1)
        self.assertFalse(receipt['wholeHmiPreservationQualified'])

    def test_only_documentinfo_export_timestamp_is_normalized(self):
        self.other.write_text(self.other.read_text().replace('01:00:00Z', '02:00:00Z'))
        self.observed[0]['sections']['Screens'][0]['export'] = guard.reference(self.other)
        self.assertTrue(self.compare()['equal'])

    def test_rehashed_xml_unknown_id_comment_content_or_annotation_changes_refuse(self):
        original = self.other.read_bytes()
        for before, after in [(b'id="17"', b'id="18"'), (b'keep', b'changed'),
                              (b'original</Value>', b'altered</Value>'), (b'<Unknown', b'<?pi changed?><Unknown')]:
            with self.subTest(before=before):
                self.other.write_bytes(original.replace(before, after))
                self.observed[0]['sections']['Screens'][0]['export'] = guard.reference(self.other)
                with self.assertRaisesRegex(ValueError, 'differs'): self.compare()

    def test_changed_export_without_rehash_refuses(self):
        self.other.write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'changed'): self.compare()

    def test_opaque_unknown_capture_metadata_cannot_disappear(self):
        del self.observed[0]['attributes']['unknown']
        with self.assertRaisesRegex(ValueError, 'differs'): self.compare()

    def test_nested_array_order_remains_significant(self):
        self.owner['sections']['Unknown'] = [1, 2]
        self.observed[0]['sections']['Unknown'] = [2, 1]
        with self.assertRaisesRegex(ValueError, 'differs'): self.compare()

    def test_boolean_integer_and_float_scalar_types_are_distinct(self):
        for original, altered in [(True, 1), (False, 0), (1, 1.0), (0, False), (1, True)]:
            self.owner['attributes']['TypedField'] = original
            self.observed[0]['attributes']['TypedField'] = altered
            with self.subTest(original=original, altered=altered), self.assertRaisesRegex(ValueError, 'differs'): self.compare()

    def test_nonfinite_captured_scalars_refuse_even_on_both_sides(self):
        for bad in [float('nan'), float('inf'), float('-inf')]:
            self.owner['attributes']['TypedField'] = bad
            with self.subTest(bad=bad), self.assertRaisesRegex(ValueError, 'Nonfinite'):
                verifier.compare_capture([self.owner], copy.deepcopy([self.owner]))

    def test_duplicate_owner_empty_owner_or_exhausted_budget_refuses(self):
        for bad in [[], [self.owner, self.owner], [{**self.owner, 'remainingBudget': 0}],
                    [{**self.owner, 'remainingBudget': True}], [{**self.owner, 'name': ''}]]:
            with self.subTest(bad=bad), self.assertRaises(ValueError): verifier.captured_state(bad)

    def test_truncated_capture_refuses_even_if_both_sides_match(self):
        self.owner['sections']['missing'] = {'truncated': True}
        with self.assertRaisesRegex(ValueError, 'Truncated'): verifier.compare_capture([self.owner], [self.owner])

    def test_explicit_unavailable_getters_remain_equal_without_promoting_coverage(self):
        self.owner['sections']['Unavailable'] = {'error': {'type': 'unsupported', 'message': 'original'}}
        receipt = verifier.compare_capture([self.owner], copy.deepcopy([self.owner]))
        self.assertTrue(receipt['equal']); self.assertFalse(receipt['wholeHmiPreservationQualified'])


class CompileAndLifetimeTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(); self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.owners = [dict(name='Owner', type='Siemens.Engineering.HmiUnified.HmiSoftware')]
        self.rows = [dict(name='Owner', compileTargetType='Siemens.Engineering.HmiUnified.HmiSoftware',
            errorCount=0, warningCount=0, state='Success', messagesComplete=True,
            messages=[dict(state='Information', description='Native complete', path=None, children=[])])]
        self.job_path = self.root / 'job.json'
        self.job = dict(schemaVersion='owned-job-terminal.v1', createdAtomicallyInJob=True,
            helperHandleSignaled=True, activeProcesses=0, totalProcesses=2, limitTerminatedProcesses=0,
            jobName='Local\\TiaMcp-Owned-test', limitFlags=0, automaticTermination=False)
        self.wrapper = dict(completed=True, helperExitCode=0, processProof=dict(completed=True,
            explicitNativeProcessDisposeInvoked=True, explicitNativeTermination=True,
            normalLifecyclePassed=False, projectLockReleaseVerified=False, coordinatorForcedTermination=False,
            jobProof=guard.write(self.job_path, self.job)))
        self.result_path = self.root / 'result.json'
        self.registry = self.root / 'registry'; self.registry.mkdir()
        patcher = patch.object(guard, 'DEFAULT_ROOT', self.registry); patcher.start(); self.addCleanup(patcher.stop)
        guard.write(self.root / 'admission.json', {'testOnly': True})
        self.ticket = guard.begin(self.registry, job_name=self.job['jobName'], source=self.root / 'admission.json')
        guard.complete(self.ticket, self.job_path)

    def test_complete_exact_owner_compiles_pass(self):
        self.assertEqual(verifier.clean_compile(self.owners, self.rows)['status'], 'passed')

    def test_zero_summary_cannot_hide_error_diagnostics(self):
        self.rows[0]['messages'][0]['state'] = 'Error'
        with self.assertRaisesRegex(ValueError, 'complete zero-error'): verifier.clean_compile(self.owners, self.rows)

    def test_wrong_missing_duplicate_owner_or_warning_refuses(self):
        for rows in [[], [self.rows[0], self.rows[0]], [{**self.rows[0], 'name': 'Other'}],
                     [{**self.rows[0], 'warningCount': 1}], [{**self.rows[0], 'errorCount': False}],
                     [{**self.rows[0], 'messagesComplete': False}]]:
            with self.subTest(rows=rows), self.assertRaises(ValueError): verifier.clean_compile(self.owners, rows)

    def test_explicit_sdk_disposal_does_not_claim_normal_lifecycle_or_lock_release(self):
        verifier.completed_tree(self.wrapper, self.result_path)
        self.assertFalse(self.wrapper['processProof']['normalLifecyclePassed'])
        self.assertFalse(self.wrapper['processProof']['projectLockReleaseVerified'])

    def test_coordinator_force_missing_dispose_or_failed_cleanup_refuses(self):
        for field, value in [('completed', False), ('explicitNativeProcessDisposeInvoked', False), ('coordinatorForcedTermination', True)]:
            altered = copy.deepcopy(self.wrapper); altered['processProof'][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError): verifier.completed_tree(altered, self.result_path)

    def test_live_job_unsignaled_helper_or_uncontained_start_refuses(self):
        for field, value in [('activeProcesses', 1), ('helperHandleSignaled', False), ('createdAtomicallyInJob', False)]:
            self.job_path.write_text(__import__('json').dumps({**self.job, field: value}))
            self.wrapper['processProof']['jobProof'] = guard.reference(self.job_path)
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, 'atomic job'): verifier.completed_tree(self.wrapper, self.result_path)

    def test_unrelated_terminal_or_rehashed_job_cannot_replace_original_ticket(self):
        self.job_path.write_text(__import__('json').dumps({**self.job, 'jobName': 'Local\\TiaMcp-Owned-other'}))
        self.wrapper['processProof']['jobProof'] = guard.reference(self.job_path)
        with self.assertRaisesRegex(ValueError, 'registry terminal'): verifier.completed_tree(self.wrapper, self.result_path)

    def test_missing_original_registry_terminal_refuses(self):
        (self.ticket / 'terminal.json').unlink()
        with self.assertRaisesRegex(ValueError, 'registry terminal'): verifier.completed_tree(self.wrapper, self.result_path)

    def test_terminal_for_another_original_admission_refuses(self):
        different = self.root / 'different'; different.mkdir()
        guard.write(different / 'admission.json', {'testOnly': True})
        with self.assertRaisesRegex(ValueError, 'exact original dispatch'): verifier.completed_tree(self.wrapper, different / 'result.json')


class CoordinatorDriftTests(unittest.TestCase):
    def test_bridge_requires_fresh_retrieval_before_source_or_native_access(self):
        with tempfile.TemporaryDirectory() as folder:
            worker = Path(folder) / 'worker.exe'; worker.write_bytes(b'test-only')
            args = SimpleNamespace(bridge_worker=worker, phase='recovered-reopen')
            with patch.object(verifier, 'configured_source') as source, patch.object(verifier, 'execute_helper') as dispatch:
                with self.assertRaisesRegex(ValueError, 'fresh archive copy'): verifier.run(args)
                source.assert_not_called(); dispatch.assert_not_called()

    def test_changed_bridge_dependency_refuses_before_native_dispatch(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            bundle = root / 'bundle'; bundle.mkdir()
            worker = bundle / 'worker.exe'; worker.write_bytes(b'test-only')
            dependency = bundle / 'dependency.dll'; dependency.write_bytes(b'original')
            (root / 'project').mkdir()
            project = root / 'project/TiaMcpHmi_test.ap21'; project.write_bytes(b'owned test')
            configured_path = root / 'configured.json'; configured_path.write_text('{}')
            helper = root / 'helper.exe'; helper.write_bytes(b'test-only')
            configured = dict(nativeResult=dict(projectPath=str(project), archive={}, after=[]))
            args = SimpleNamespace(bridge_worker=worker, phase='reopen-retrieve', configured_result=configured_path,
                helper=helper, output=root / 'output', known_store=[], prior_result=None)
            def window(*unused, **options):
                dependency.write_bytes(b'changed while waiting for ownership')
                guard.write(args.output / 'window-intent.json', {'testOnly': True})
                return __import__('contextlib').nullcontext()
            with patch.object(verifier, 'configured_source', return_value=configured), \
                 patch.object(verifier, 'StoreExclusiveWindow', side_effect=window), \
                 patch.object(verifier, 'completed_tree', return_value={}), patch.object(verifier, 'execute_helper') as dispatch:
                self.assertEqual(verifier.run(args), 1)
                dispatch.assert_not_called()
            self.assertIn('Bridge bundle changed before dispatch', guard.read(args.output / 'result.json')['failure']['message'])

    def test_configured_raw_baseline_drift_during_native_lifetime_refuses_completion(self):
        temporary = tempfile.TemporaryDirectory(); self.addCleanup(temporary.cleanup)
        root = Path(temporary.name).resolve()
        (root / 'project').mkdir()
        project = root / 'project/TiaMcpHmi_test.ap21'; project.write_bytes(b'owned synthetic project')
        configured_path = root / 'configured.json'; configured_path.write_text('{}')
        helper = root / 'helper.exe'; helper.write_bytes(b'not executable')
        configured = dict(nativeResult=dict(projectPath=str(project), archive={}, after=[]))
        args = SimpleNamespace(configured_result=configured_path, prior_result=None, helper=helper,
            phase='reopen-retrieve', output=root / 'output', known_store=[])
        def complete(helper, request, artifact_root, output, result, **kwargs):
            output.mkdir()
            result.update(helperExitCode=0, processProof=dict(completed=True))
            guard.write(output / 'result.json', dict(completed=True, projectPath=str(project),
                reopened=[], reopenedCompile=[], recovered=[], recoveredCompile=[]))
        def claimed_window(*unused, **options):
            guard.write(args.output / 'window-intent.json', {'testOnly': True})
            return __import__('contextlib').nullcontext()
        with patch.object(verifier, 'configured_source', side_effect=[configured, configured,
                ValueError('Exact completed raw configured native result required')]) as source, \
             patch.object(verifier, 'StoreExclusiveWindow', side_effect=claimed_window), patch.object(verifier, 'completed_tree', return_value={}), \
             patch.object(verifier, 'execute_helper', side_effect=complete), \
             patch.object(verifier, 'compare_capture', return_value={'equal': True}), \
             patch.object(verifier, 'clean_compile'), patch.object(verifier, 'retained'):
            self.assertEqual(verifier.run(args), 1)
            self.assertEqual(source.call_count, 3)
        receipt = guard.read(args.output / 'result.json')
        self.assertFalse(receipt['completed'])
        self.assertIn('raw configured native result', receipt['failure']['message'])

    def test_prior_result_drift_while_waiting_for_store_ownership_refuses_before_dispatch(self):
        temporary = tempfile.TemporaryDirectory(); self.addCleanup(temporary.cleanup)
        root = Path(temporary.name).resolve()
        (root / 'project').mkdir()
        project = root / 'project/TiaMcpHmi_test.ap21'; project.write_bytes(b'owned synthetic project')
        configured_path = root / 'configured.json'; configured_path.write_text('{}')
        prior_path = root / 'prior.json'; prior_path.write_text('{}')
        helper = root / 'helper.exe'; helper.write_bytes(b'not executable')
        configured = dict(nativeResult=dict(projectPath=str(project), archive={}, after=[]))
        prior = {'completed': True, 'beforeOwnership': True}
        altered = {'completed': True, 'beforeOwnership': False}
        args = SimpleNamespace(configured_result=configured_path, prior_result=prior_path, helper=helper,
            phase='recovered-reopen', output=root / 'output', known_store=[])
        with patch.object(verifier, 'configured_source', return_value=configured), \
             patch.object(verifier, 'recovered_source', side_effect=[(prior, project), (altered, project)]) as sources, \
             patch.object(verifier, 'StoreExclusiveWindow'), patch.object(verifier, 'execute_helper') as dispatch:
            self.assertEqual(verifier.run(args), 1)
            dispatch.assert_not_called()
            self.assertEqual(sources.call_count, 2)
        receipt = guard.read(args.output / 'result.json')
        self.assertIn('Recovery source changed before dispatch', receipt['failure']['message'])


if __name__ == '__main__': unittest.main()
