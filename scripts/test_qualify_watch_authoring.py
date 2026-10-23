"""WT-05 refuses unreviewed apply and never replays a failed native creation."""
import argparse
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

import qualify_watch_authoring as subject
from qualify_watch_mcp import ToolError


class Controller(unittest.TestCase):
    def make_run(self, folder, apply_error=None):
        run = subject.Run.__new__(subject.Run)
        run.root = Path(folder)
        run.args = argparse.Namespace(mode='create-bool')
        run.start_host = Mock(return_value=3)
        run.start_tia, run.close_tia, run.reopen, run.close_host = Mock(), Mock(), Mock(), Mock()
        run.restore = Mock(return_value='recovered.ap21')
        def call(name, arguments=None, label=None):
            if name == 'tia_create_scratch':
                return {'completed': True, 'project': {'path': 'scratch.ap21'}, 'exports': [
                    {'block': {'name': n, 'path': '/' + n, 'plcPath': '/plc'}}
                    for n in ('TiaMcp_EnableGate', 'TiaMcp_EnableGate_DB')]}
            if name == 'watch_table_generate':
                return {'generationId': 'gen', 'generationSha256': 'hash', 'plan': {'rows': [
                    {'candidateId': 'member1', 'candidate': {'memberPath': ['Active'], 'datatype': 'Bool'}}]}}
            if name == 'watch_table_prepare_authoring':
                return {'operation': {'id': 'op'}}
            if name == 'watch_table_apply' and apply_error:
                raise apply_error
            if name == 'engineering_operation_status':
                return {'checkpoints': [{}]}
            return {}
        run.call = Mock(side_effect=call)
        return run

    def test_independent_review_refusal_prevents_apply(self):
        with tempfile.TemporaryDirectory() as folder:
            run = self.make_run(folder)
            with patch.object(subject.oracle, 'reviewed_candidate', side_effect=ValueError('wrong row')):
                with self.assertRaisesRegex(ValueError, 'wrong row'):
                    run.execute()
            names = [c.args[0] for c in run.call.call_args_list]
            self.assertEqual(names, ['tia_create_scratch', 'watch_table_generate', 'watch_table_prepare_authoring'])
            run.restore.assert_not_called()

    def test_failed_apply_is_recovered_once_and_never_qualified(self):
        with tempfile.TemporaryDirectory() as folder:
            run = self.make_run(folder, ToolError('watch_table_apply', {'error': {'code': 'VerificationFailed'}}))
            with patch.object(subject.oracle, 'reviewed_candidate', return_value={'reviewedPlanSha256': 'digest'}), \
                 patch.object(subject.oracle, 'verify', return_value={'completed': False, 'issues': ['retained failure']}):
                with self.assertRaisesRegex(RuntimeError, 'retained failure'):
                    run.execute()
            names = [c.args[0] for c in run.call.call_args_list]
            self.assertEqual(names.count('watch_table_apply'), 1)
            self.assertNotIn('watch_table_verify_reopen', names)
            run.restore.assert_called_once_with('op', 'scratch.ap21')
            run.close_host.assert_called_once()
            self.assertTrue((run.root/'failure.json').exists())

    def test_no_reopen_before_first_table_apply(self):
        with tempfile.TemporaryDirectory() as folder:
            run = self.make_run(folder)
            order = Mock()
            order.attach_mock(run.call, 'call')
            order.attach_mock(run.reopen, 'reopen')
            with patch.object(subject.oracle, 'reviewed_candidate', return_value={'reviewedPlanSha256': 'digest'}), \
                 patch.object(subject.oracle, 'verify', return_value={'completed': True, 'issues': []}):
                run.execute()
            apply_index = next(i for i, c in enumerate(order.mock_calls) if c[0] == 'call' and c.args[0] == 'watch_table_apply')
            reopen_index = next(i for i, c in enumerate(order.mock_calls) if c[0] == 'reopen')
            self.assertLess(apply_index, reopen_index)


if __name__ == '__main__':
    unittest.main()
