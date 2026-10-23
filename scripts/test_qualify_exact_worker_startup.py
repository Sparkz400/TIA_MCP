"""Actual startup runner/client/tree with synthetic transport and OS boundaries.

No Host, worker, TIA, helper or process-management call can cross the guard.
"""
import copy
from datetime import datetime, timezone
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import qualify_exact_worker_startup as runner
from test_startup_lifecycle_regressions_20261016 import GuardedCase, World, exact


class StartupRunnerChecks(GuardedCase):
    def setup_run(self):
        world = World(self)
        original_binder = world.binder
        def bind(watcher, identity):
            original_binder(watcher, identity)
            watcher.evidence['originalFileTimeTicks'] = 116444736000000000 + int(identity['created'] * 10000000)
        world.tree.binder = bind
        bind(world.watchers[world.HOST], world.host.info)
        output = tempfile.TemporaryDirectory()
        self.addCleanup(output.cleanup)
        args = SimpleNamespace(host=world.folder / 'TiaMcp.Host.exe',
            worker=world.folder / 'TiaMcp.Openness.V21.exe', root=Path(output.name) / 'run', timeout=1, api_dir=None)
        args.host.write_bytes(b'synthetic-host')
        args.worker.write_bytes(b'synthetic-worker')
        client = world.client()
        calls = []

        def eof():
            world.all_terminal()
            client.errors.append(json.dumps({'bridgeWorkerExit': {
                'processId': world.WORKER, 'processExitVerified': True, 'exitCode': 0,
                'forcedTermination': False,
                'startedAtUtc': datetime.fromtimestamp(world.worker.info['created'], timezone.utc).isoformat()}}))

        def transport(method, params):
            if method == 'initialize': return {}
            name = params['name']
            calls.append(name)
            if name == 'tia_start': world.add_native()
            if name == 'tia_detach': world.apis[world.NATIVE].terminal = True
            value = {'processes': []} if name == 'tia_discover' else {}
            if name in ('tia_start', 'tia_session_status'):
                value = {'processId': world.NATIVE, 'ownsPortalProcess': True, 'projects': []}
            return {'structuredContent': value}

        client.process.stdin.close.side_effect = eof
        world.stack.enter_context(patch.object(runner, 'ExactClient', return_value=client))
        request = world.stack.enter_context(patch.object(exact.Client, 'request', side_effect=transport))
        return world, args, client, calls, request

    def test_normal_route_uses_one_original_handle_per_process(self):
        world, args, client, calls, _ = self.setup_run()
        report = runner.run(args)
        self.assertTrue(report['completed'])
        self.assertEqual(calls, ['tia_discover', 'tia_start', 'tia_session_status', 'tia_detach', 'tia_discover'])
        world.assert_handles(True)
        self.assertEqual(set(world.apis), {world.HOST, world.WORKER, world.NATIVE})
        client.process.stdin.close.assert_called_once()
        self.assertTrue(json.loads((args.root / 'startup-verification.json').read_text())['independentReviewRequired'])

    def test_first_log_failure_cleans_actual_client_before_propagating(self):
        for kind in (OSError, KeyboardInterrupt, SystemExit):
            with self.subTest(kind=kind.__name__):
                world, args, _, calls, _ = self.setup_run()
                primary = kind('first Host receipt failed')
                emit = runner.emit
                def fail(path, value):
                    if path.name == 'host-start.json': raise primary
                    return emit(path, value)
                with patch.object(runner, 'emit', side_effect=fail):
                    with self.assertRaises(kind) as raised: runner.run(args)
                self.assertIs(raised.exception, primary)
                self.assertEqual(calls, [])
                world.assert_handles(True)

    def test_cancellation_after_each_request_cannot_skip_cleanup_or_continue(self):
        for ordinal, name in enumerate(('tia_discover', 'tia_start', 'tia_session_status', 'tia_detach', 'tia_discover')):
            for kind in (KeyboardInterrupt, SystemExit):
                with self.subTest(ordinal=ordinal, kind=kind.__name__):
                    world, args, _, calls, _ = self.setup_run()
                    primary = kind('result receipt interrupted')
                    emit = runner.emit
                    def fail(path, value):
                        if path.name == f'step-{ordinal:02}-{name}-result.json': raise primary
                        return emit(path, value)
                    with patch.object(runner, 'emit', side_effect=fail):
                        with self.assertRaises(kind) as raised: runner.run(args)
                    self.assertIs(raised.exception, primary)
                    self.assertEqual(len(calls), ordinal + 1)
                    world.assert_handles(True)

    def test_primary_request_failure_survives_failed_result_logging(self):
        world, args, _, calls, request = self.setup_run()
        primary = KeyboardInterrupt('initialize cancelled')
        request.side_effect = primary
        emit = runner.emit
        def fail(path, value):
            if path.name == 'result.json': raise OSError('disk full')
            return emit(path, value)
        with patch.object(runner, 'emit', side_effect=fail):
            with self.assertRaises(KeyboardInterrupt) as raised: runner.run(args)
        self.assertIs(raised.exception, primary)
        self.assertEqual(calls, [])
        world.assert_handles(True)

    def test_changed_package_stops_before_next_native_request(self):
        world, args, _, calls, request = self.setup_run()
        original = request.side_effect
        def change(method, params):
            result = original(method, params)
            if params.get('name') == 'tia_session_status': args.worker.write_bytes(b'changed')
            return result
        request.side_effect = change
        with self.assertRaisesRegex(RuntimeError, 'package bytes or inventory changed'): runner.run(args)
        self.assertNotIn('tia_detach', calls)
        world.assert_handles(True)

    def test_new_package_payload_stops_before_next_native_request(self):
        world, args, _, calls, request = self.setup_run()
        original = request.side_effect
        def change(method, params):
            result = original(method, params)
            if params.get('name') == 'tia_session_status':
                (args.worker.parent / 'added.dll').write_bytes(b'unreviewed')
            return result
        request.side_effect = change
        with self.assertRaisesRegex(RuntimeError, 'package bytes or inventory changed'): runner.run(args)
        self.assertNotIn('tia_detach', calls)
        world.assert_handles(True)

    def test_output_cannot_overlap_binary_package(self):
        world, args, _, _, _ = self.setup_run()
        args.root = world.folder / 'overlapping-output'
        with self.assertRaisesRegex(RuntimeError, 'outside the frozen binary packages'): runner.run(args)
        runner.ExactClient.assert_not_called()

    def test_final_discovery_drift_fails_after_cleanup(self):
        world, args, _, calls, request = self.setup_run()
        original = request.side_effect
        def drift(method, params):
            result = original(method, params)
            if len(calls) == 5: result['structuredContent']['processes'] = [{'processId': 999}]
            return result
        request.side_effect = drift
        with self.assertRaisesRegex(RuntimeError, 'discovery changed'): runner.run(args)
        world.assert_handles(True)
        self.assertFalse(json.loads((args.root / 'result.json').read_text())['completed'])

    def test_reuse_of_evidence_root_refuses_before_launch(self):
        _, args, _, _, _ = self.setup_run()
        args.root.mkdir()
        with self.assertRaises(FileExistsError): runner.run(args)
        runner.ExactClient.assert_not_called()

    def test_final_receipt_failure_occurs_only_after_exact_cleanup(self):
        world, args, _, _, _ = self.setup_run()
        emit = runner.emit
        def fail(path, value):
            if path.name == 'result.json': raise OSError('final receipt failed')
            return emit(path, value)
        with patch.object(runner, 'emit', side_effect=fail):
            with self.assertRaisesRegex(OSError, 'final receipt'): runner.run(args)
        world.assert_handles(True)
        self.assertFalse((args.root / 'startup-verification.json').exists())

    def test_fsync_failure_never_publishes_success_receipt(self):
        world, args, _, _, _ = self.setup_run()
        emit = runner.emit
        def fail(path, value):
            if path.name == 'startup-verification.json':
                with patch.object(runner.os, 'fsync', side_effect=OSError('verification fsync failed')):
                    return emit(path, value)
            return emit(path, value)
        with patch.object(runner, 'emit', side_effect=fail):
            with self.assertRaisesRegex(OSError, 'verification fsync'): runner.run(args)
        world.assert_handles(True)
        self.assertFalse((args.root / 'startup-verification.json').exists())
        self.assertTrue((args.root / 'startup-verification.json.pending').exists())

    def test_emit_refuses_overwriting_existing_final_or_pending_receipt(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'receipt.json'
            runner.emit(path, {'completed': False})
            original = path.read_bytes()
            with self.assertRaises(RuntimeError): runner.emit(path, {'completed': True})
            self.assertEqual(path.read_bytes(), original)
            pending = Path(temp) / 'other.json.pending'
            pending.write_bytes(b'original failed write')
            with self.assertRaises(FileExistsError): runner.emit(Path(temp) / 'other.json', {})
            self.assertEqual(pending.read_bytes(), b'original failed write')

    def test_late_native_terminal_proof_does_not_qualify(self):
        world, args, _, _, request = self.setup_run()
        original = request.side_effect
        def late(method, params):
            result = original(method, params)
            if params.get('name') == 'tia_detach':
                world.apis[world.NATIVE].wait_script.append(KeyboardInterrupt())
            return result
        request.side_effect = late
        with self.assertRaisesRegex(RuntimeError, 'terminal proof'): runner.run(args)
        world.assert_handles(True)

    def test_tampered_close_packets_cannot_qualify(self):
        _, args, _, _, _ = self.setup_run()
        report = runner.run(args)
        mutations = [lambda p: p.update(stderrEofObserved=False),
            lambda p: p.update(closeInterrupted='KeyboardInterrupt'),
            lambda p: p['tree'].update(startupOutcomeUnknown=True),
            lambda p: p['tree'].update(errors=['unclassified child']),
            lambda p: p['tree']['exits'].pop(),
            lambda p: p['tree']['exits'][0]['proof'].update(handleClosed=False),
            lambda p: p['tree']['exits'][0]['proof'].update(exitCode=3),
            lambda p: p['tree']['exits'][0]['proof'].update(processId=999),
            lambda p: p['tree']['exits'][0]['proof'].update(originalFileTimeTicks=1),
            lambda p: p['workerExits'][0].update(startedAtUtc='2000-01-01T00:00:00Z'),
            lambda p: p['workerExits'][0].update(forcedTermination=True)]
        for mutate in mutations:
            packet = copy.deepcopy(report['close'])
            mutate(packet)
            with self.assertRaises(RuntimeError):
                runner.validate_close(packet, report['host'], report['ownedProcessId'], args.host, args.worker)


if __name__ == '__main__': unittest.main()
