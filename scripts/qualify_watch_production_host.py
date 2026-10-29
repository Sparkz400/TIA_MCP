"""Owned Bool workflow and actual process-loss checks through the production Host.

Uses the reviewed exact client; all original handles survive cancellation and
remain held until terminal proof. Frozen packages are checked before each call.
"""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import time

import qualify_watch_mcp as workflow
import verify_watch_expansion as oracle
from exact_qualification_client import ExactClient
from qualify_exact_worker_startup import package, check_sources, emit
from retained_native_lifecycle import require


class Run(workflow.Run):
    def __init__(self, args):
        super().__init__(args)
        self.packages = {key: package(getattr(args, key)) for key in ('host', 'worker')}
        self.sources = []
        for name in ('qualify_watch_production_host.py', 'exact_qualification_client.py',
                     'retained_native_lifecycle.py', 'qualify_exact_worker_startup.py'):
            source = Path(__file__).with_name(name)
            target = self.root / 'controller-source' / name
            shutil.copyfile(source, target)
            self.sources.append({'path': str(source.resolve()), 'sha256': workflow.digest(source)})
        for row in json.loads((self.root / 'controller-source-manifest.json').read_text(encoding="utf-8")):
            self.sources.append({'path': str(Path(__file__).with_name(Path(row['path']).name).resolve()),
                                 'sha256': row['sha256']})
        self.save('frozen-inputs', {'packages': self.packages, 'sources': self.sources})

    def save(self, name, data):
        emit(self.root / (name + '.json'), data)
        return data

    def check_inputs(self):
        check_sources(self.sources)
        for key, records in self.packages.items():
            require(package(getattr(self.args, key)) == records, 'Frozen package changed: ' + key)

    def start_host(self, point):
        self.check_inputs()
        require(self.client is None, 'Previous exact Host must be closed before restart')
        require(len(self.clients) < (1 if self.args.mode == 'bool' else 2), 'No automatic retry')
        self.client = ExactClient(self.args.host.resolve(), 240, ['--worker', str(self.args.worker.resolve()),
            '--artifacts', str(self.root), '--component-qualification', '--watch-qualification', point])
        entry = {'identity': self.client.exact_tree.anchor['identity'], 'point': point, 'processes': []}
        self.clients.append(entry)
        self.save(f'host-{len(self.clients):02}-start', entry)
        self.client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
            'clientInfo': {'name': 'production-host-bool-qualification', 'version': '1'}})
        self.client.notify('notifications/initialized')
        return self.client.process.pid

    def call(self, name, arguments=None, label=None):
        self.check_inputs()
        return super().call(name, arguments, label)

    def start_tia(self):
        result = self.call('tia_start')
        require(result['ownsPortalProcess'] and not result['projects'], 'Expected an owned empty instance')
        self.native = self.client.exact_tree.one('native', result['processId'])
        self.save('native-' + str(result['processId']) + '-start', self.native['identity'])

    def close_tia(self):
        self.call('tia_detach')
        proof = self.client.exact_tree.close_record(self.native)
        self.lifetimes.append(proof)
        self.save('native-' + str(proof['processId']) + '-exit', proof)
        self.native = None

    def cleanup_worker_fault(self, tree, index):
        # Resolve only an explicitly admitted orphan from the exact fault marker.
        # The original native handle is retained independently throughout.
        if self.args.mode in oracle.NATIVE_POINTS:
            tree.await_capture_complete()
            marker = json.loads((self.root / 'interruption-marker.json').read_text(encoding="utf-8"))
            native = tree.one('native', marker['processId'])
            identities = [r['identity'] for r in tree.snapshot() if r['role'] != 'host']
            self.save(f'host-{index:02}-processes', identities)
            watcher = native['watcher']
            if watcher.api.wait(watcher._handle, 0) == 258:
                helper = self.native_cleanup_helper
                require(helper is not None, 'Explicit exact orphan cleanup helper is required')
                check_sources([helper, *helper['sources']])
                result = subprocess.run([helper['path'], str(self.root)], capture_output=True, text=True)
                self.save('coordinator-native-close-call', {'returncode': result.returncode,
                    'stdout': result.stdout, 'stderr': result.stderr})
                require(result.returncode == 0 or (not (self.root / 'coordinator-native-close-intent.json').exists()
                        and watcher.api.wait(watcher._handle, 0) == 0), 'Exact orphan cleanup failed')
    def close_host(self, expected_loss=False):
        if self.client is None:
            return
        client = self.client
        index = len(self.clients)
        tree = client.exact_tree
        cleanup_failure = None
        if expected_loss and self.args.mode in oracle.NATIVE_POINTS:
            try:
                self.cleanup_worker_fault(tree, index)
            except BaseException as error:
                cleanup_failure = error
                print('Fault cleanup failed; retaining original handles through terminal proof: ' + str(error), flush=True)
        requested = time.time()
        # This barrier must run even if helper execution or evidence writing
        # fails. It retains live handles until explicit external reconciliation.
        close = client.close()
        self._closed_client = client
        self.client = None
        exits = [r for r in close['tree']['exits'] if r['role'] != 'host']
        if not (self.root / f'host-{index:02}-processes.json').exists():
            self.save(f'host-{index:02}-processes', [r['identity'] for r in exits])
        self.save(f'host-{index:02}-close', {'identity': tree.anchor['identity'], 'close': close,
            'exits': exits, 'logs': client.errors, 'expectedLoss': expected_loss,
            'closeRequestedAt': requested, 'closedAt': time.time(),
            'cleanupError': str(cleanup_failure) if cleanup_failure else None})
        if cleanup_failure is not None:
            raise cleanup_failure
        if getattr(self, 'native', None):
            self.lifetimes.append(self.native['proof'])
            self.native = None
        proof = close['tree']
        require(close['forcedHostStop'] is False and close['stderrEofObserved'] is True
                and not close['closeInterrupted'], 'Host EOF/cleanup incomplete')
        require(proof['workerStartupCaptured'] and proof['nativeStartupInventoryComplete']
                and not proof['startupOutcomeUnknown'] and not any(proof[k] for k in
                ('errors', 'unknownReasons', 'finalizationErrors', 'startupInventoryUnknownReasons')),
                'Exact startup or cleanup inventory incomplete')
        expected_host = 74 if expected_loss and self.args.mode in oracle.HOST_POINTS else 1 if expected_loss else 0
        expected_worker = 73 if expected_loss and self.args.mode in oracle.NATIVE_POINTS else 0
        marker = json.loads((self.root / 'interruption-marker.json').read_text(encoding="utf-8")) if expected_loss else {}
        for row in proof['exits']:
            p = row['proof']
            expected = expected_host if row['role'] == 'host' else expected_worker if row['role'] == 'worker' else (
                75 if expected_loss and (self.root / 'coordinator-native-close.json').exists()
                and row['identity']['pid'] == marker['processId'] else 0)
            require(p['completed'] and p['sameOsHandleRetained'] and p['birthAndImageVerified']
                    and not p['deadlineOrCancellationObserved'] and p['exitCode'] == expected,
                    'Exact terminal proof differs from admitted fault/lifecycle')
        require(close['hostExitCode'] == expected_host, 'Unexpected Host exit')
        self.check_inputs()

    def restore(self, oid, original):
        result = self.call('watch_table_recover', {'operationId': oid}, 'recovered')
        restored = result['recovered']['project']['path']
        self.reopen(restored)
        self.call('watch_table_reconcile_recovery', {'operationId': oid, 'role': 'restored'}, 'restored-verified')
        if self.args.mode != 'bool':
            operation = json.loads((self.root / 'operation.json').read_text(encoding="utf-8"))
            self.refuse('watch_table_apply', {'operationId': oid, 'reviewedPlanSha256': operation['digest']},
                        'replay-refusal-before-original-readback', operationId=oid, nativeDispatchInvoked=False)
            self.call('qualification_watch_compete', {'operationId': oid}, 'owner-held-before-original-readback')
        self.reopen(original)
        self.call('watch_table_reconcile_recovery', {'operationId': oid, 'role': 'original'}, 'reconciled')
        return restored


def run_owned(run):
    failure = None
    try:
        result = run.execute()
        run.report(result)
        return result
    except BaseException as error:
        failure = error
        try:
            run.save('runner-failure', {'error': str(error), 'errorType': type(error).__name__,
                                      'automaticReplay': False, 'qualificationPassed': False})
        except BaseException:
            pass
        raise
    finally:
        if run.client is not None:
            try:
                run.close_host(expected_loss=run.args.mode in oracle.NATIVE_POINTS | oracle.HOST_POINTS
                               and (run.root / 'interruption-marker.json').exists())
            except BaseException:
                if failure is None:
                    raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ('host', 'worker', 'root', 'seed'):
        parser.add_argument('--' + key, type=Path, required=True)
    parser.add_argument('--mode', choices=('bool', 'save.after', 'host.watch_apply_native'), required=True)
    parser.add_argument('--native-cleanup-helper', type=Path)
    args = parser.parse_args()
    args.comment_input = None
    require(args.mode != 'save.after' or args.native_cleanup_helper, 'Worker loss requires exact orphan cleanup')
    run = Run(args)
    result = run_owned(run)
    print(json.dumps({'completed': result['completed'], 'mode': args.mode, 'root': str(run.root)}), flush=True)


if __name__ == '__main__':
    main()
