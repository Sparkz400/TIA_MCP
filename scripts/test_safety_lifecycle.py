"""Safety lifetime checks with fake original process handles; no Siemens launch."""
import copy
import tempfile
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import safety_lifecycle as lifetime
from block_test_runtime import NativeToolError
from component_exact_lifetime import ComponentOwnedScratch
from test_component_exact_lifetime import FakeTree


def terminal_session(pid, host_pid=None):
    host_pid = pid + 100 if host_pid is None else host_pid
    worker_pid = host_pid + 1
    exits = []
    for role, selected in (('host', host_pid), ('worker', worker_pid), ('native', pid)):
        exits.append({'role': role, 'identity': {'pid': selected, 'created': 0}, 'proof': {
            'processId': selected, 'completed': True, 'processExitVerified': True, 'handleClosed': True,
            'sameOsHandleRetained': True, 'forcedTermination': False, 'exitCode': 0, 'deadlineOrCancellationObserved': False}})
    return {'hostProcessId': host_pid, 'processId': pid, 'workerHandle': {'processId': worker_pid},
        'handleCapture': {'birthAndImageVerified': True}, 'cleanup': {'completed': True, 'errors': [],
        'automaticTermination': False, 'workerShutdownReceiptBoundToBirth': True, 'launcherShutdown': {
        'hostExitCode': 0, 'forcedHostStop': False, 'stderrEofObserved': True, 'closeInterrupted': None,
        'workerExits': [{'processId': worker_pid, 'startedAtUtc': '1970-01-01T00:00:00Z', 'processExitVerified': True,
                         'exitCode': 0, 'forcedTermination': False}],
        'tree': {'completed': True, 'forcedTermination': False, 'startupOutcomeUnknown': False, 'exits': exits}}}}


class SafetyLifecycleTests(unittest.TestCase):
    def run_session(self, fault=None):
        events = []; report = {'completed': False}; FakeTree.events = events
        with tempfile.TemporaryDirectory() as directory:
            class Host:
                process = SimpleNamespace(pid=10, returncode=0)
                errors = []
                error_reader = SimpleNamespace(is_alive=lambda: False)
                def notify(self, *args): pass
                def close(self): raise AssertionError('Automatic-kill close must never run')
                def request(self, method, params):
                    if method == 'initialize': return {}
                    tool = params['name']; events.append(tool)
                    if tool == 'tia_discover': value = {'processes': []}
                    elif tool == 'tia_start':
                        if fault == 'lostStart': raise TimeoutError('lost startup response')
                        value = {'attached': True, 'ownsPortalProcess': True, 'processId': 12, 'projects': []}
                    elif tool == 'tia_detach': value = {'attached': False, 'disposedOwnedPortalConnection': fault != 'detach'}
                    elif tool == 'project_inventory':
                        if fault == 'lostRead': raise TimeoutError('lost response')
                        value = {}
                    else: raise AssertionError(tool)
                    return {'structuredContent': value}
            class Tree(FakeTree):
                def __init__(self, *args):
                    super().__init__(*args)
                    for row in self.snapshot(): row['watcher'].evidence['birthAndImageVerified'] = True
                def close_record(self, row):
                    events.append('exact-native-terminal')
                    proof = copy.deepcopy(terminal_session(12, 10)['cleanup']['launcherShutdown']['tree']['exits'][2]['proof'])
                    if fault == 'nativeExit': proof['exitCode'] = 1
                    return proof
                def close_all(self):
                    events.append('same-tree-terminal')
                    return terminal_session(12, 10)['cleanup']['launcherShutdown']['tree']
            def closer(client, tree):
                events.append('host-eof')
                if fault == 'interruptedClose': raise KeyboardInterrupt()
                result = copy.deepcopy(terminal_session(12, 10)['cleanup']['launcherShutdown'])
                if fault == 'hostExit': result['hostExitCode'] = 1
                if fault == 'workerReceipt': result['workerExits'] = []
                if fault == 'forced': result['forcedHostStop'] = True
                return result
            def factory(*args, **kwargs):
                kwargs.update(client_factory=lambda *a: Host(), tree_factory=Tree, closer=closer, manifest_factory=lambda *a: {})
                return ComponentOwnedScratch(*args, **kwargs)
            with patch.object(lifetime, 'ComponentOwnedScratch', side_effect=factory):
                session = lifetime.new_session('host.exe', 'worker.exe', directory, report, lambda: None, Path(directory)/'lifetime')
            try:
                session.start()
                lifetime.call(session, 'project_inventory')
                report['qualificationChecksPassed'] = True
            except BaseException as error:
                report['failure'] = str(error)
                with self.assertRaises(AssertionError): lifetime.call(session, 'project_inventory')
            finally:
                try: lifetime.finish(session, report, lambda: None)
                except BaseException: pass
        return report, events

    def test_success_requires_exact_terminal_evidence(self):
        report, events = self.run_session()
        self.assertTrue(report['completed']); lifetime.verify(report)
        self.assertLess(events.index('exact-native-terminal'), events.index('host-eof'))

    def test_cleanup_failures_override_completed_checks(self):
        for fault in ('detach', 'nativeExit', 'hostExit', 'workerReceipt', 'forced', 'interruptedClose'):
            with self.subTest(fault=fault):
                report, events = self.run_session(fault)
                self.assertTrue(report['qualificationChecksPassed']); self.assertFalse(report['completed'])
                self.assertFalse(report['cleanupVerified']); self.assertIn('host-eof', events)

    def test_uncertain_requests_do_not_dispatch_followups_or_detach(self):
        for fault in ('lostStart', 'lostRead'):
            with self.subTest(fault=fault):
                report, events = self.run_session(fault)
                self.assertFalse(report['completed']); self.assertNotIn('tia_detach', events)
                self.assertEqual(0 if fault == 'lostStart' else 1, events.count('project_inventory'))

    def test_retained_verifier_rejects_missing_wrong_and_unclean_receipts(self):
        report, _ = self.run_session()
        for defect in ('policy', 'missingWorker', 'wrongBirth', 'boolExit', 'wrongNative', 'missingHandle'):
            bad = copy.deepcopy(report); session = bad['sessions'][0]; shutdown = session['cleanup']['launcherShutdown']
            if defect == 'policy': bad['lifetimePolicy'] = 'encoder-exact-retained-terminal-v1'
            elif defect == 'missingWorker': shutdown['workerExits'] = []
            elif defect == 'wrongBirth': shutdown['workerExits'][0]['startedAtUtc'] = '1970-01-02T00:00:00Z'
            elif defect == 'boolExit': shutdown['hostExitCode'] = False
            elif defect == 'wrongNative': shutdown['tree']['exits'][2]['proof']['processId'] = 99
            else: session['handleCapture'] = {}
            with self.subTest(defect=defect), self.assertRaises(AssertionError): lifetime.verify(bad)

    def test_only_explicit_recorded_safety_boundary_refusal_allows_continuation(self):
        for tool, kind, code, allow in (('change_prepare', 'source_upsert', 'SafetyBoundary', True),
                                        ('change_prepare', 'source_upsert', 'TimeoutError', True),
                                        ('change_apply', 'source_upsert', 'SafetyBoundary', True),
                                        ('change_prepare', 'safety_import_xml', 'SafetyBoundary', True),
                                        ('change_prepare', 'source_upsert', 'SafetyBoundary', False)):
            records = []
            def dispatch(name, arguments):
                if name == 'project_inventory': return {}
                result = {'error': {'code': code}}
                records.append({'tool': name, 'arguments': arguments, 'status': 'failed', 'result': result})
                raise NativeToolError(name, result)
            session = SimpleNamespace(session={'calls': records}, call=dispatch, persist=lambda: None)
            accepted = tool == 'change_prepare' and kind == 'source_upsert' and code == 'SafetyBoundary' and allow
            with self.subTest(tool=tool, kind=kind, code=code, allow=allow):
                if accepted:
                    self.assertEqual(code, lifetime.call(session, tool, {'kind': kind}, allow)['error']['code'])
                    self.assertEqual({}, lifetime.call(session, 'project_inventory'))
                    self.assertEqual('failed', records[0]['status'])
                else:
                    with self.assertRaises(NativeToolError): lifetime.call(session, tool, {'kind': kind}, allow)
                    with self.assertRaises(AssertionError): lifetime.call(session, 'project_inventory')


if __name__ == '__main__': unittest.main()
