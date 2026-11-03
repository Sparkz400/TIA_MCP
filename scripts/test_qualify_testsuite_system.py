"""Offline TS-Q03 orchestration checks. No Siemens processes or simulator calls."""
import json
from pathlib import Path
import tempfile
import unittest

from qualify_testsuite_system import exercise, exploratory_result


class Fake:
    def __init__(self, directory):
        self.directory = Path(directory)
        self.report = {}
        self.calls = []
        self.state = 'prepared'
        self.revision = 0
        self.released = False
        (self.directory / 'raw.json').write_text(json.dumps({'untouchedNative': True}), encoding='utf-8')

    def status(self):
        operation = {'id': 'op', 'revision': self.revision, 'state': self.state, 'checkpoints': [] if self.released else ['cp'],
                     'recoveryResolutions': [{'disposition': 'verifiedRestored'}]}
        return {'job': {'id': 'job', 'reviewSha256': 'hash'}, 'operations': [operation]}

    def stage(self, label, call):
        return call()

    def release(self):
        self.released = True

    def call_error(self, name, args):
        self.calls.append(name)
        if name == 'testsuite_style_job_execute_next':
            return {'code': 'TestSuiteProfileUnqualified'}
        self.state = 'outcomeUnknown'
        self.revision += 1
        return {'code': 'SimulationTargetMismatch' if self.released else 'QualificationResponseLost'}

    def tool(self, name, args):
        self.calls.append(name)
        if name == 'testsuite_style_job_status':
            return self.status()
        if name == 'testsuite_style_job_results':
            return {'raw': {'relativePath': 'raw.json'}, 'result': {'classification': 'incompleteEvidence'}}
        self.state = {'qualification_testsuite_execute': 'recoveryRequired', 'qualification_testsuite_recover': 'recoveryRequired',
                      'qualification_testsuite_reconcile': 'reconciled'}[name]
        self.revision += 1
        return self.status()


class SystemRunnerChecks(unittest.TestCase):
    def test_unknown_native_success_never_passes(self):
        for classification in ('passed', 'assertionFailure', None):
            with self.subTest(classification=classification), self.assertRaises(AssertionError):
                exploratory_result({'raw': {}, 'result': {'classification': classification}})

    def test_raw_capture_is_required(self):
        with self.assertRaises(AssertionError):
            exploratory_result({'result': {'classification': 'incompleteEvidence'}})

    def test_capture_recovery_and_response_loss_without_replay(self):
        for case in ('explore-pass', 'explore-fail', 'afterRun', 'released'):
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory:
                h = Fake(directory)
                exercise(h, {'status': h.status()}, h.release, case)
                self.assertEqual(h.calls.count('qualification_testsuite_execute'), 1)
                if case == 'released':
                    self.assertNotIn('qualification_testsuite_recover', h.calls)
                    self.assertEqual(h.report['finalOperation']['state'], 'outcomeUnknown')
                else:
                    self.assertEqual(h.calls.count('qualification_testsuite_recover'), 1)
                    self.assertEqual(h.calls.count('qualification_testsuite_reconcile'), 1)
                    self.assertEqual(h.report['finalOperation']['state'], 'reconciled')
                    if case != 'afterRun':
                        self.assertEqual(h.report['rawResults'], {'untouchedNative': True})


if __name__ == '__main__':
    unittest.main()
