"""Controller cancellation, cleanup and immutable-package admission boundaries."""
import argparse
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock

import qualify_watch_production_host as subject


class Boundaries(unittest.TestCase):
    def test_all_execute_failures_reach_exact_cleanup(self):
        for error in (RuntimeError('transport lost'), KeyboardInterrupt(), SystemExit(9)):
            with self.subTest(error=type(error).__name__), tempfile.TemporaryDirectory() as folder:
                run = Mock()
                run.root = Path(folder)
                run.args.mode = 'bool'
                run.client = object()
                run.execute.side_effect = error
                run.save.side_effect = OSError('receipt failed')
                with self.assertRaises(type(error)):
                    subject.run_owned(run)
                run.close_host.assert_called_once_with(expected_loss=False)

    def test_fault_marker_required_before_fault_cleanup(self):
        with tempfile.TemporaryDirectory() as folder:
            run = Mock()
            run.root = Path(folder)
            run.args.mode = 'save.after'
            run.client = object()
            run.execute.side_effect = RuntimeError('failure before fault')
            with self.assertRaises(RuntimeError):
                subject.run_owned(run)
            run.close_host.assert_called_once_with(expected_loss=False)

    def test_explicit_fault_cleanup_cannot_skip_original_handle_barrier(self):
        for error in (OSError('helper failed'), KeyboardInterrupt()):
            with self.subTest(error=type(error).__name__), tempfile.TemporaryDirectory() as folder:
                run = subject.Run.__new__(subject.Run)
                run.root = Path(folder)
                run.args = argparse.Namespace(mode='save.after')
                run.clients = [{}]
                run.client = Mock()
                client = run.client
                client.exact_tree.anchor = {'identity': {'pid': 3}}
                client.close.return_value = {'tree': {'exits': []}}
                run.cleanup_worker_fault = Mock(side_effect=error)
                run.save = Mock()
                with self.assertRaises(type(error)):
                    run.close_host(expected_loss=True)
                client.close.assert_called_once()
                self.assertIsNone(run.client)

    def test_complete_package_rejects_changed_and_added_dependency(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for kind in ('host', 'worker'):
                (root / kind).mkdir()
                (root / kind / 'program.exe').write_bytes(b'original')
            run = subject.Run.__new__(subject.Run)
            run.args = argparse.Namespace(host=root/'host/program.exe', worker=root/'worker/program.exe')
            run.sources = []
            run.packages = {k: subject.package(getattr(run.args, k)) for k in ('host', 'worker')}
            run.check_inputs()
            (root/'worker/program.exe').write_bytes(b'changed')
            with self.assertRaisesRegex(RuntimeError, 'Frozen package changed'):
                run.check_inputs()
            (root/'worker/program.exe').write_bytes(b'original')
            (root/'host/extra.dll').write_bytes(b'unadmitted')
            with self.assertRaisesRegex(RuntimeError, 'Frozen package changed'):
                run.check_inputs()

    def test_owner_is_retained_when_host_initialization_fails(self):
        from unittest.mock import patch
        with tempfile.TemporaryDirectory() as folder:
            run = subject.Run.__new__(subject.Run)
            run.root = Path(folder)
            run.args = argparse.Namespace(mode='bool', host=Path('host.exe'), worker=Path('worker.exe'))
            run.client = None
            run.clients = []
            run.check_inputs = Mock()
            run.save = Mock(side_effect=KeyboardInterrupt())
            client = Mock()
            client.exact_tree.anchor = {'identity': {'pid': 7}}
            with patch.object(subject, 'ExactClient', return_value=client):
                with self.assertRaises(KeyboardInterrupt):
                    run.start_host('none')
            self.assertIs(run.client, client)
            self.assertEqual(len(run.clients), 1)


if __name__ == '__main__':
    unittest.main()
