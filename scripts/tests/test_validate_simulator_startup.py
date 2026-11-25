"""Startup evidence must include identity, state, cleanup and process exit."""
import copy
import json
from pathlib import Path
import tempfile
import types
import unittest
from unittest import mock

import validate_simulator_startup as startup


class FakeClient:
    def __init__(self, fault=None, instance_id=0):
        self.fault, self.calls, self.closed = fault, [], False
        self.created = self.released = False
        self.published_completion = []
        self.instance = dict(id=instance_id, name='owned', owned=True, operatingState='Off', communicationInterface='Softbus')
        self.process = types.SimpleNamespace(returncode=0)

    def notify(self, *args):
        pass

    def request(self, method, params=None):
        self.published_completion.append(json.loads(self.report_path.read_text())['completed'])
        if method == 'initialize':
            return {}
        name, args = params['name'], params['arguments']
        self.calls.append((name, args))
        if name == self.fault:
            return dict(isError=True, structuredContent={'error': {'code': 'InjectedFailure'}})
        if name == 'sim_status':
            instances = [dict(id=9, name='foreign')] if self.fault == 'foreign' else []
            if self.created and not self.released:
                instances.append(dict(id=self.instance['id'], name='owned'))
            result = dict(available=True, networkMode='Softbus', instances=instances)
        elif name == 'sim_create':
            self.created = True
            result = dict(instance=copy.deepcopy(self.instance), downloadTicket='TICKETVALUE')
        elif name == 'sim_state':
            observed = copy.deepcopy(self.instance)
            observed['operatingState'] = 'Stop' if args['action'] == 'powerOn' else 'Off'
            if self.fault == 'wrong_state':
                observed['operatingState'] = 'Run'
            if self.fault == 'wrong_identity':
                observed['id'] = 10
            result = dict(instance=observed, action=args['action'], runtimeCode='OK' if args['action'] == 'powerOn' else None)
            if self.fault == args['action'] + '_code':
                result['runtimeCode'] = 'Timeout'
            if self.fault == 'missing_code':
                result.pop('runtimeCode')
            if self.fault == 'wrong_action':
                result['action'] = 'run'
        elif name == 'sim_release':
            if self.fault == 'cleanup_throw':
                raise RuntimeError('Release lost response TICKETVALUE')
            self.released = True
            result = dict(instanceId=self.instance['id'], instanceName='owned', unregistered=True)
            if self.fault == 'release_false':
                result['unregistered'] = False
        else:
            raise AssertionError('Unexpected tool ' + name)
        return dict(structuredContent=result)

    def close(self):
        self.published_completion.append(json.loads(self.report_path.read_text())['completed'])
        self.closed = True
        self.process.returncode = 7 if self.fault == 'host_exit' else 0
        if self.fault == 'close_throw':
            raise RuntimeError('Shutdown failed')
        return dict(hostExitCode=self.process.returncode, forcedHostStop=self.fault == 'forced', stderrEofObserved=True,
                    workerExits=[] if self.fault == 'missing_worker_exit' else [dict(processExitVerified=True, exitCode=0, forcedTermination=False)])


class StartupChecks(unittest.TestCase):
    def run_startup(self, client, existing=False):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'report.json'
            client.report_path = path
            if existing:
                path.write_text('existing evidence', encoding='utf-8')
            with mock.patch.object(startup, 'Client', return_value=client) as constructor, \
                    mock.patch.object(startup, 'artifact_path', return_value=path), \
                    mock.patch('sys.argv', ['startup', '--report', str(path)]):
                if existing:
                    with self.assertRaises(FileExistsError):
                        startup.main()
                    self.assertFalse(constructor.called)
                    self.assertEqual('existing evidence', path.read_text())
                    return
                code = startup.main()
            report = json.loads(path.read_text())
            self.assertTrue(client.closed)
            self.assertNotIn('TICKETVALUE', path.read_text())
            return code, report

    def test_valid_zero_identity_requires_state_and_cleanup(self):
        client = FakeClient()
        code, report = self.run_startup(client)
        self.assertEqual(0, code)
        self.assertTrue(report['completed'])
        self.assertEqual(['powerOn', 'powerOff'], [args['action'] for name, args in client.calls if name == 'sim_state'])
        self.assertTrue(report['cleanupSucceeded'])
        self.assertFalse(any(client.published_completion), 'Success must not be published before cleanup and shutdown finish.')

    def test_cleanup_exception_still_closes_host_and_retains_failure(self):
        code, report = self.run_startup(FakeClient('cleanup_throw'))
        self.assertEqual(1, code)
        self.assertFalse(report['completed'])
        self.assertIn('cleanupFailure', report)

    def test_invalid_identity_never_targets_state_or_cleanup(self):
        for value in (False, 0.0, -1, '0', None):
            with self.subTest(value=value):
                client = FakeClient(instance_id=value)
                code, report = self.run_startup(client)
                self.assertEqual(1, code)
                self.assertFalse(report['completed'])
                self.assertFalse(any(name in {'sim_state', 'sim_release'} for name, _ in client.calls))

    def test_failed_or_nonempty_preflight_never_creates(self):
        for fault in ('sim_status', 'foreign'):
            with self.subTest(fault=fault):
                client = FakeClient(fault)
                code, report = self.run_startup(client)
                self.assertEqual(1, code)
                self.assertFalse(report['completed'])
                self.assertFalse(client.created)

    def test_wrong_state_identity_or_release_never_passes(self):
        for fault in ('wrong_state', 'wrong_identity', 'release_false', 'sim_state', 'powerOn_code', 'powerOff_code', 'missing_code', 'wrong_action'):
            with self.subTest(fault=fault):
                client = FakeClient(fault)
                code, report = self.run_startup(client)
                self.assertEqual(1, code)
                self.assertFalse(report['completed'])
                self.assertTrue(client.released)

    def test_missing_failed_or_forced_shutdown_never_passes(self):
        for fault in ('host_exit', 'missing_worker_exit', 'forced', 'close_throw'):
            with self.subTest(fault=fault):
                code, report = self.run_startup(FakeClient(fault))
                self.assertEqual(1, code)
                self.assertFalse(report['completed'])

    def test_existing_report_is_preserved_before_launch(self):
        self.run_startup(FakeClient(), existing=True)


if __name__ == '__main__':
    unittest.main()
